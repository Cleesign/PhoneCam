import AVFoundation
import CoreImage
import ImageIO
import QuartzCore

enum StreamResolution: String, CaseIterable, Identifiable {
    case hd720 = "720p"
    case hd1080 = "1080p"

    var id: String { rawValue }

    var preset: AVCaptureSession.Preset {
        switch self {
        case .hd720: return .hd1280x720
        case .hd1080: return .hd1920x1080
        }
    }
}

/// Captures from the front or back camera and turns each frame into a JPEG.
///
/// Frames are sent unmirrored. Their orientation follows `setOrientation`: CameraModel
/// reads the motion sensors and asks AVFoundation to rotate the frames so the picture is
/// upright however the phone is held. Cropping to 16:9 is done on the Windows side.
final class CameraStreamer: NSObject, AVCaptureVideoDataOutputSampleBufferDelegate {
    let session = AVCaptureSession()

    /// Called on the video queue with each encoded frame.
    var frameHandler: ((Data) -> Void)?
    /// Called on the video queue; return false to drop the frame (back-pressure).
    var shouldSendFrame: (() -> Bool)?

    private let sessionQueue = DispatchQueue(label: "phonecam.session")
    private let videoQueue = DispatchQueue(label: "phonecam.video", qos: .userInteractive)
    private let videoOutput = AVCaptureVideoDataOutput()
    private let ciContext = CIContext(options: [.useSoftwareRenderer: false, .cacheIntermediates: false])
    private let colorSpace = CGColorSpace(name: CGColorSpace.sRGB)!
    private var currentInput: AVCaptureDeviceInput?
    /// Only touched on sessionQueue. landscapeRight is the sensor's native orientation.
    private var orientation: AVCaptureVideoOrientation = .landscapeRight

    // Only touched on videoQueue.
    private var jpegQuality: CGFloat = 0.6
    private var minFrameInterval: CFTimeInterval = 1.0 / 30.0
    private var lastFrameTime: CFTimeInterval = 0

    func setQuality(_ quality: Double) {
        videoQueue.async { self.jpegQuality = CGFloat(quality) }
    }

    func setMaxFPS(_ fps: Double) {
        videoQueue.async { self.minFrameInterval = 1.0 / max(1, fps) }
    }

    /// Starts the session, or switches camera / resolution if it is already running.
    func start(position: AVCaptureDevice.Position,
               resolution: StreamResolution,
               completion: @escaping @MainActor (Bool) -> Void) {
        sessionQueue.async {
            let ok = self.configure(position: position, resolution: resolution)
            if !self.session.isRunning {
                self.session.startRunning()
            }
            Task { @MainActor in completion(ok) }
        }
    }

    /// Rotates the outgoing frames so they come out upright for this orientation.
    func setOrientation(_ newOrientation: AVCaptureVideoOrientation) {
        sessionQueue.async {
            guard newOrientation != self.orientation else { return }
            self.orientation = newOrientation
            self.applyOrientation()
        }
    }

    /// Call on sessionQueue.
    private func applyOrientation() {
        guard let connection = videoOutput.connection(with: .video),
              connection.isVideoOrientationSupported else { return }
        // videoOrientation (rather than iOS 17's videoRotationAngle) because it accounts for
        // the front and back camera facing opposite ways, and works on iOS 16.
        connection.videoOrientation = orientation
    }

    func stop() {
        sessionQueue.async {
            if self.session.isRunning { self.session.stopRunning() }
        }
    }

    private func configure(position: AVCaptureDevice.Position, resolution: StreamResolution) -> Bool {
        session.beginConfiguration()
        defer { session.commitConfiguration() }

        if let currentInput {
            session.removeInput(currentInput)
            self.currentInput = nil
        }

        guard let device = AVCaptureDevice.default(.builtInWideAngleCamera, for: .video, position: position),
              let input = try? AVCaptureDeviceInput(device: device),
              session.canAddInput(input) else {
            return false
        }
        session.addInput(input)
        currentInput = input

        if session.canSetSessionPreset(resolution.preset) {
            session.sessionPreset = resolution.preset
        } else {
            session.sessionPreset = .high
        }

        if !session.outputs.contains(videoOutput) {
            videoOutput.videoSettings = [kCVPixelBufferPixelFormatTypeKey as String: kCVPixelFormatType_32BGRA]
            videoOutput.alwaysDiscardsLateVideoFrames = true
            videoOutput.setSampleBufferDelegate(self, queue: videoQueue)
            guard session.canAddOutput(videoOutput) else { return false }
            session.addOutput(videoOutput)
        }

        if let connection = videoOutput.connection(with: .video), connection.isVideoMirroringSupported {
            connection.automaticallyAdjustsVideoMirroring = false
            connection.isVideoMirrored = false
        }
        applyOrientation() // the connection is new after switching camera
        return true
    }

    // MARK: - AVCaptureVideoDataOutputSampleBufferDelegate

    func captureOutput(_ output: AVCaptureOutput,
                       didOutput sampleBuffer: CMSampleBuffer,
                       from connection: AVCaptureConnection) {
        let now = CACurrentMediaTime()
        guard now - lastFrameTime >= minFrameInterval * 0.9 else { return }
        guard shouldSendFrame?() ?? false else { return }
        guard let pixelBuffer = CMSampleBufferGetImageBuffer(sampleBuffer) else { return }

        let image = CIImage(cvPixelBuffer: pixelBuffer)
        let options: [CIImageRepresentationOption: Any] = [
            CIImageRepresentationOption(rawValue: kCGImageDestinationLossyCompressionQuality as String): jpegQuality
        ]
        guard let jpeg = ciContext.jpegRepresentation(of: image, colorSpace: colorSpace, options: options) else { return }

        lastFrameTime = now
        frameHandler?(jpeg)
    }
}
