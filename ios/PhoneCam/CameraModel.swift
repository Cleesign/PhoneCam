import AVFoundation
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

    let streamer = CameraStreamer()
    let server = StreamServer()

    private var started = false

    init() {
        let server = self.server
        streamer.shouldSendFrame = { server.canAcceptFrame }
        streamer.frameHandler = { jpeg in server.send(type: .jpegFrame, payload: jpeg) }

        server.onClientChanged = { [weak self] connected in
            Task { @MainActor in
                guard let self else { return }
                self.laptopConnected = connected
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
            "error": cameraError ?? ""
        ])
    }
}
