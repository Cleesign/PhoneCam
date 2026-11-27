import AVFoundation
import SwiftUI
import UIKit

/// Full-screen camera with a minimal glass control bar.
///
/// The app's layout stays portrait (so the preview never jumps around), but every label and
/// button turns in place to follow how the phone is held, like the built-in Camera app.
/// That keeps everything readable when the phone is tilted, even with rotation lock on.
struct ContentView: View {
    @EnvironmentObject private var model: CameraModel
    @Environment(\.scenePhase) private var scenePhase

    /// Rotation for the controls, accumulated so it always turns the short way round.
    @State private var angle: Double = 0

    var body: some View {
        ZStack {
            Color.black.ignoresSafeArea()

            CameraPreview(session: model.streamer.session)
                .ignoresSafeArea()
                .opacity(model.cameraOn ? 1 : 0) // don't show the last frozen frame
                .animation(.easeInOut(duration: 0.25), value: model.cameraOn)

            // Soft shading so the controls stay legible on bright scenes.
            VStack(spacing: 0) {
                LinearGradient(colors: [.black.opacity(0.5), .clear], startPoint: .top, endPoint: .bottom)
                    .frame(height: 150)
                Spacer()
                LinearGradient(colors: [.clear, .black.opacity(0.55)], startPoint: .top, endPoint: .bottom)
                    .frame(height: 240)
            }
            .ignoresSafeArea()
            .allowsHitTesting(false)

            VStack(spacing: 0) {
                ZStack {
                    StatusPill(connected: model.laptopConnected)
                        .rotationEffect(.degrees(angle))

                    HStack {
                        Spacer()
                        RoundIconButton(
                            systemName: model.cameraOn ? "video.fill" : "video.slash.fill",
                            isOn: !model.cameraOn,
                            angle: angle,
                            action: { model.setCameraOn(!model.cameraOn) }
                        )
                    }
                    .padding(.horizontal, 20)
                }
                .frame(height: 96)          // room to turn without clipping

                Spacer()

                if let message = centerMessage {
                    MessageCard(message: message)
                        .rotationEffect(.degrees(angle))
                        .transition(.opacity.combined(with: .scale(scale: 0.96)))
                }

                Spacer()

                controlBar
                    .padding(.horizontal, 16)
                    .padding(.bottom, 12)
            }
        }
        .preferredColorScheme(.dark)
        .statusBarHidden()
        .animation(.spring(response: 0.45, dampingFraction: 0.82), value: angle)
        .animation(.easeInOut(duration: 0.25), value: centerMessage)
        .onAppear {
            model.onAppear()
            angle = Self.targetAngle(for: model.heldOrientation)
        }
        .onChange(of: model.heldOrientation) { orientation in
            turn(to: Self.targetAngle(for: orientation))
        }
        .onChange(of: scenePhase) { phase in
            // .inactive (app switcher, Control Centre) keeps everything running.
            if phase == .background { model.setInBackground(true) }
            if phase == .active { model.setInBackground(false) }
        }
    }

    // MARK: - Controls

    private var controlBar: some View {
        HStack(spacing: 0) {
            ControlButton(
                caption: model.position == .front ? "Front" : "Back",
                isOn: false,
                angle: angle,
                action: { model.toggleCamera() }
            ) {
                Image(systemName: "arrow.triangle.2.circlepath.camera")
            }

            ControlButton(
                caption: model.autoRotate ? "Auto" : "Locked",
                isOn: model.autoRotate,
                angle: angle,
                action: { model.setAutoRotate(!model.autoRotate) }
            ) {
                Image(systemName: model.autoRotate ? "rotate.right" : "lock.rotation")
            }

            ControlButton(
                caption: "Video",
                isOn: false,
                angle: angle,
                action: { model.toggleResolution() }
            ) {
                Text(model.resolution.rawValue)
                    .font(.system(size: 13, weight: .bold, design: .rounded))
                    .monospacedDigit()
            }

            ControlButton(
                caption: CameraModel.qualitySteps[model.qualityIndex].label,
                isOn: false,
                angle: angle,
                action: { model.cycleQuality() }
            ) {
                QualityBars(level: model.qualityIndex)
            }

            ControlButton(
                caption: model.micOn ? "Mic on" : "Mic off",
                isOn: model.micOn,
                angle: angle,
                action: { model.setMic(!model.micOn) }
            ) {
                Image(systemName: model.micOn ? "mic.fill" : "mic.slash")
            }
        }
        .padding(.vertical, 14)
        .frame(maxWidth: .infinity)
        .background(.ultraThinMaterial, in: RoundedRectangle(cornerRadius: 28, style: .continuous))
        .overlay(
            RoundedRectangle(cornerRadius: 28, style: .continuous)
                .strokeBorder(Color.white.opacity(0.08))
        )
    }

    // MARK: - Messages

    private var centerMessage: Message? {
        if !model.cameraAuthorized {
            return Message(icon: "video.slash",
                           title: "Camera access is off",
                           detail: "Allow it in Settings › PhoneCam.",
                           showsSettingsButton: true)
        }
        if let error = model.cameraError {
            return Message(icon: "exclamationmark.triangle", title: "Camera unavailable", detail: error)
        }
        if let error = model.micError {
            return Message(icon: "mic.slash", title: "Microphone", detail: error,
                           showsSettingsButton: error.contains("Settings"))
        }
        if !model.laptopConnected {
            return Message(icon: "cable.connector",
                           title: "Waiting for your PC",
                           detail: "Connect the USB-C cable and start PhoneCam on Windows.")
        }
        if !model.cameraOn {
            return Message(icon: "video.slash",
                           title: "Camera is off",
                           detail: model.micOn
                               ? "Only your microphone is being sent."
                               : "Nothing is being sent. Turn on the camera or the mic.")
        }
        return nil
    }

    // MARK: - Rotation

    /// How far to turn the controls so they read upright. Positive = clockwise.
    static func targetAngle(for orientation: UIDeviceOrientation) -> Double {
        switch orientation {
        case .landscapeLeft: return 90          // top edge pointing left
        case .landscapeRight: return -90        // top edge pointing right
        case .portraitUpsideDown: return 180
        default: return 0
        }
    }

    private func turn(to target: Double) {
        var delta = (target - angle).truncatingRemainder(dividingBy: 360)
        if delta > 180 { delta -= 360 }
        if delta < -180 { delta += 360 }
        angle += delta
    }
}

// MARK: - Pieces

private struct Message: Equatable {
    var icon: String
    var title: String
    var detail: String
    var showsSettingsButton = false
}

private struct StatusPill: View {
    let connected: Bool

    var body: some View {
        HStack(spacing: 7) {
            Circle()
                .fill(connected ? Color.red : Color.white.opacity(0.45))
                .frame(width: 8, height: 8)
                .shadow(color: connected ? .red.opacity(0.8) : .clear, radius: 4)
            Text(connected ? "LIVE" : "OFFLINE")
                .font(.system(size: 12, weight: .semibold, design: .rounded))
                .tracking(1.2)
        }
        .padding(.horizontal, 12)
        .padding(.vertical, 7)
        .background(.ultraThinMaterial, in: Capsule())
        .animation(.easeInOut(duration: 0.2), value: connected)
    }
}

private struct MessageCard: View {
    let message: Message

    var body: some View {
        VStack(spacing: 10) {
            Image(systemName: message.icon)
                .font(.system(size: 26, weight: .light))
                .foregroundStyle(.white.opacity(0.9))
            Text(message.title)
                .font(.system(size: 17, weight: .semibold, design: .rounded))
            Text(message.detail)
                .font(.system(size: 14))
                .foregroundStyle(.white.opacity(0.7))
                .multilineTextAlignment(.center)
                .fixedSize(horizontal: false, vertical: true)
            if message.showsSettingsButton {
                Button("Open Settings") {
                    if let url = URL(string: UIApplication.openSettingsURLString) {
                        UIApplication.shared.open(url)
                    }
                }
                .font(.system(size: 14, weight: .semibold))
                .padding(.horizontal, 16)
                .padding(.vertical, 8)
                .background(.white.opacity(0.15), in: Capsule())
                .padding(.top, 4)
            }
        }
        .padding(22)
        .frame(width: 260)   // narrow enough to fit when turned sideways
        .background(.ultraThinMaterial, in: RoundedRectangle(cornerRadius: 24, style: .continuous))
    }
}

private struct ControlButton<Icon: View>: View {
    let caption: String
    let isOn: Bool
    let angle: Double
    let action: () -> Void
    @ViewBuilder let icon: () -> Icon

    var body: some View {
        Button {
            UIImpactFeedbackGenerator(style: .light).impactOccurred()
            action()
        } label: {
            VStack(spacing: 6) {
                icon()
                    .font(.system(size: 19, weight: .medium))
                    .foregroundStyle(isOn ? Color.black : Color.white)
                    .frame(width: 50, height: 50)
                    .background(
                        Circle().fill(isOn ? Color.white : Color.white.opacity(0.14))
                    )
                Text(caption)
                    .font(.system(size: 11, weight: .medium))
                    .foregroundStyle(.white.opacity(0.75))
                    .lineLimit(1)
                    .fixedSize()
            }
            .rotationEffect(.degrees(angle))
            .frame(maxWidth: .infinity)
            .contentShape(Rectangle())
        }
        .buttonStyle(PressableStyle())
        .animation(.easeInOut(duration: 0.2), value: isOn)
    }
}

/// Small round glass button (top bar). White when "on" (here: camera off), like the control bar.
private struct RoundIconButton: View {
    let systemName: String
    let isOn: Bool
    let angle: Double
    let action: () -> Void

    var body: some View {
        Button {
            UIImpactFeedbackGenerator(style: .light).impactOccurred()
            action()
        } label: {
            Image(systemName: systemName)
                .font(.system(size: 16, weight: .medium))
                .foregroundStyle(isOn ? Color.black : Color.white)
                .frame(width: 42, height: 42)
                .background {
                    if isOn {
                        Circle().fill(Color.white)
                    } else {
                        Circle().fill(.ultraThinMaterial)
                    }
                }
                .rotationEffect(.degrees(angle))
        }
        .buttonStyle(PressableStyle())
        .animation(.easeInOut(duration: 0.2), value: isOn)
    }
}

private struct QualityBars: View {
    let level: Int // 0, 1, 2

    var body: some View {
        HStack(alignment: .bottom, spacing: 3) {
            ForEach(0..<3) { i in
                RoundedRectangle(cornerRadius: 1.5)
                    .frame(width: 4, height: CGFloat(7 + i * 5))
                    .opacity(i <= level ? 1 : 0.3)
            }
        }
    }
}

private struct PressableStyle: ButtonStyle {
    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .scaleEffect(configuration.isPressed ? 0.92 : 1)
            .animation(.easeOut(duration: 0.12), value: configuration.isPressed)
    }
}

/// Live camera preview on the phone itself.
struct CameraPreview: UIViewRepresentable {
    let session: AVCaptureSession

    final class PreviewView: UIView {
        override class var layerClass: AnyClass { AVCaptureVideoPreviewLayer.self }
        var previewLayer: AVCaptureVideoPreviewLayer { layer as! AVCaptureVideoPreviewLayer }
    }

    func makeUIView(context: Context) -> PreviewView {
        let view = PreviewView()
        view.backgroundColor = .black
        view.previewLayer.session = session
        view.previewLayer.videoGravity = .resizeAspectFill
        return view
    }

    func updateUIView(_ uiView: PreviewView, context: Context) {}
}
