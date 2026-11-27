import AVFoundation
import CoreMotion
import SwiftUI
import UIKit

@MainActor
final class CameraModel: ObservableObject {
    @Published private(set) var position: AVCaptureDevice.Position = .back
    @Published private(set) var resolution: StreamResolution = .hd720
    @Published private(set) var quality: Double = 0.6
    @Published private(set) var laptopConnected = false
    @Published private(set) var cameraAuthorized = false
    @Published private(set) var cameraError: String?
    /// Keep the picture upright by following how the phone is held.
    @Published private(set) var autoRotate = true
    /// How the phone is physically held, from the motion sensors.
    @Published private(set) var heldOrientation: UIDeviceOrientation = .portrait

    /// The user wants the microphone sent to the computer.
    @Published private(set) var micOn = false
    @Published private(set) var noiseReduction = true
    /// True while the microphone is actually running (wanted, allowed, and a computer is connected).
    @Published private(set) var micLive = false
    @Published private(set) var micError: String?

    /// Quality steps offered by the phone UI (the Windows app uses the same three).
    static let qualitySteps: [(label: String, value: Double)] = [("Low", 0.4), ("Medium", 0.6), ("High", 0.8)]

    let streamer = CameraStreamer()
    let server = StreamServer()
    let mic = MicStreamer()

    private let motion = CMMotionManager()
    private var started = false
    private var hasMotionReading = false

    init() {
        let server = self.server
        streamer.shouldSendFrame = { server.canAcceptFrame }
        streamer.frameHandler = { jpeg in server.send(type: .jpegFrame, payload: jpeg) }

        // Audio header: [UInt32 BE sample rate][UInt8 channels][UInt8 reserved]
        let rate = UInt32(MicStreamer.sampleRate)
        let audioHeader = Data([UInt8(rate >> 24 & 0xFF), UInt8(rate >> 16 & 0xFF),
                                UInt8(rate >> 8 & 0xFF), UInt8(rate & 0xFF), 1, 0])
        mic.chunkHandler = { pcm in
            guard server.isClientConnected else { return }
            server.send(type: .audio, payload: audioHeader + pcm, tracked: false)
        }

        server.onClientChanged = { [weak self] connected in
            Task { @MainActor in
                guard let self else { return }
                self.laptopConnected = connected
                self.updateMic() // the mic only runs while a computer is connected
                if connected { self.sendStatus() }
            }
        }
        server.onCommand = { [weak self] command in
            Task { @MainActor in self?.apply(command) }
        }
    }

    func onAppear() {
        guard !started else { return }
        started = true

        // Keep the phone awake while it's being used as a webcam.
        UIApplication.shared.isIdleTimerDisabled = true
        server.start()
        startMotion()

        switch AVCaptureDevice.authorizationStatus(for: .video) {
        case .authorized:
            cameraAuthorized = true
            startCamera()
        case .notDetermined:
            AVCaptureDevice.requestAccess(for: .video) { granted in
                Task { @MainActor in
                    self.cameraAuthorized = granted
                    if granted { self.startCamera() }
                }
            }
        default:
            cameraAuthorized = false
        }
    }

    func select(_ newPosition: AVCaptureDevice.Position) {
        guard newPosition != position else { return }
        position = newPosition
        if cameraAuthorized { startCamera() } else { sendStatus() }
    }

    func select(resolution newResolution: StreamResolution) {
        guard newResolution != resolution else { return }
        resolution = newResolution
        if cameraAuthorized { startCamera() } else { sendStatus() }
    }

    func setQuality(_ newQuality: Double) {
        quality = min(max(newQuality, 0.1), 1.0)
        streamer.setQuality(quality)
        sendStatus()
    }

    var qualityIndex: Int {
        let steps = Self.qualitySteps
        return steps.indices.min { abs(steps[$0].value - quality) < abs(steps[$1].value - quality) } ?? 1
    }

    func cycleQuality() {
        setQuality(Self.qualitySteps[(qualityIndex + 1) % Self.qualitySteps.count].value)
    }

    func toggleCamera() {
        select(position == .front ? .back : .front)
    }

    func toggleResolution() {
        select(resolution: resolution == .hd720 ? .hd1080 : .hd720)
    }

    // MARK: - Microphone

    func setMic(_ on: Bool) {
        guard on != micOn else { return }
        micOn = on
        if on, AVCaptureDevice.authorizationStatus(for: .audio) == .notDetermined {
            AVCaptureDevice.requestAccess(for: .audio) { _ in
                Task { @MainActor in self.updateMic() }
            }
            sendStatus()
            return
        }
        updateMic()
    }

    func setNoiseReduction(_ on: Bool) {
        guard on != noiseReduction else { return }
        noiseReduction = on
        if micLive { micLive = false } // force a restart with the new processing
        updateMic()
    }

    /// Starts or stops the microphone to match what's wanted and allowed.
    private func updateMic() {
        let authorized = AVCaptureDevice.authorizationStatus(for: .audio) == .authorized
        let shouldRun = micOn && authorized && laptopConnected

        if micOn && !authorized && AVCaptureDevice.authorizationStatus(for: .audio) != .notDetermined {
            micError = "Microphone access is off. Allow it in Settings › PhoneCam."
        } else if !micOn {
            micError = nil
        }

        if shouldRun && !micLive {
            micLive = true
            mic.start(noiseReduction: noiseReduction) { [weak self] error in
                guard let self else { return }
                self.micError = error
                if error != nil { self.micLive = false }
                self.sendStatus()
            }
        } else if !shouldRun && micLive {
            micLive = false
            mic.stop()
        }
        sendStatus()
    }

    func setAutoRotate(_ on: Bool) {
        guard on != autoRotate else { return }
        autoRotate = on
        applyRotation()
    }

    // MARK: - Orientation (gyroscope + accelerometer)

    /// Device motion (gyroscope fused with the accelerometer) gives the direction of gravity.
    /// This works even with Portrait Orientation Lock on, and while the app's UI stays portrait.
    private func startMotion() {
        guard motion.isDeviceMotionAvailable else { return }
        motion.deviceMotionUpdateInterval = 0.1
        motion.startDeviceMotionUpdates(to: .main) { [weak self] data, _ in
            guard let gravity = data?.gravity else { return }
            let x = gravity.x, y = gravity.y, z = gravity.z
            Task { @MainActor in self?.handleGravity(x: x, y: y, z: z) }
        }
    }

    /// Gravity in device coordinates: +x = right edge, +y = top edge, +z = out of the screen.
    private func handleGravity(x: Double, y: Double, z: Double) {
        if abs(z) > 0.85 { return } // lying flat: keep the last orientation
        let margin = 0.25            // dead zone around 45° so it doesn't flicker
        let held: UIDeviceOrientation
        if abs(x) > abs(y) + margin {
            held = x < 0 ? .landscapeLeft : .landscapeRight   // top edge pointing left / right
        } else if abs(y) > abs(x) + margin {
            held = y < 0 ? .portrait : .portraitUpsideDown
        } else {
            return
        }
        // The camera starts in its native orientation, so always apply the first reading.
        guard held != heldOrientation || !hasMotionReading else { return }
        hasMotionReading = true
        heldOrientation = held
        applyRotation()
    }

    private func applyRotation() {
        let videoOrientation: AVCaptureVideoOrientation
        if !autoRotate {
            videoOrientation = .landscapeRight // sensor native; rotate manually on Windows
        } else {
            switch heldOrientation {
            case .portrait: videoOrientation = .portrait
            case .portraitUpsideDown: videoOrientation = .portraitUpsideDown
            case .landscapeRight: videoOrientation = .landscapeLeft  // device and video enums are mirrored
            default: videoOrientation = .landscapeRight
            }
        }
        streamer.setOrientation(videoOrientation)
        sendStatus()
    }

    private var heldOrientationName: String {
        switch heldOrientation {
        case .portrait: return "portrait"
        case .portraitUpsideDown: return "portraitUpsideDown"
        case .landscapeRight: return "landscapeRight"
        default: return "landscapeLeft"
        }
    }

    private func startCamera() {
        streamer.setQuality(quality)
        streamer.start(position: position, resolution: resolution) { [weak self] ok in
            guard let self else { return }
            self.cameraError = ok ? nil : "This camera isn't available on this iPhone."
            self.sendStatus()
        }
    }

    private func apply(_ command: [String: Any]) {
        if let camera = command["camera"] as? String {
            select(camera == "front" ? .front : .back)
        }
        if let res = command["resolution"] as? String, let newResolution = StreamResolution(rawValue: res) {
            select(resolution: newResolution)
        }
        if let q = command["quality"] as? Double {
            setQuality(q)
        }
        if let on = command["autoRotate"] as? Bool {
            setAutoRotate(on)
        }
        if let on = command["noiseReduction"] as? Bool {
            setNoiseReduction(on)
        }
        if let on = command["mic"] as? Bool {
            setMic(on)
        }
        if command["requestStatus"] != nil {
            sendStatus()
        }
    }

    private func sendStatus() {
        server.sendJSON(type: .status, [
            "camera": position == .front ? "front" : "back",
            "resolution": resolution.rawValue,
            "quality": quality,
            "cameraAuthorized": cameraAuthorized,
            "autoRotate": autoRotate,
            "orientation": heldOrientationName,
            "mic": micOn,
            "micLive": micLive,
            "noiseReduction": noiseReduction,
            "micError": micError ?? "",
            "error": cameraError ?? ""
        ])
    }
}
