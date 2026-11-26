import AVFoundation
import SwiftUI

struct ContentView: View {
    @EnvironmentObject private var model: CameraModel

    var body: some View {
        VStack(alignment: .leading, spacing: 18) {
            HStack(spacing: 10) {
                Circle()
                    .fill(model.laptopConnected ? Color.green : Color.orange)
                    .frame(width: 10, height: 10)
                Text(model.laptopConnected
                     ? "Streaming to your computer"
                     : "Waiting for your computer — connect the USB-C cable and start PhoneCam on Windows")
                    .font(.subheadline)
                    .fixedSize(horizontal: false, vertical: true)
            }

            CameraPreview(session: model.streamer.session)
                .aspectRatio(3.0 / 4.0, contentMode: .fit)
                .frame(maxWidth: .infinity)
                .background(Color.black)
                .clipShape(RoundedRectangle(cornerRadius: 14))

            if !model.cameraAuthorized {
                Text("Camera access is off. Turn it on in Settings › PhoneCam.")
                    .font(.footnote)
                    .foregroundStyle(.red)
            } else if let error = model.cameraError {
                Text(error)
                    .font(.footnote)
                    .foregroundStyle(.red)
            }

            VStack(alignment: .leading, spacing: 8) {
                Text("Camera").font(.headline)
                Picker("Camera", selection: Binding(
                    get: { model.position == .front ? 1 : 0 },
                    set: { model.select($0 == 1 ? .front : .back) }
                )) {
                    Text("Back").tag(0)
                    Text("Front").tag(1)
                }
                .pickerStyle(.segmented)
            }

            VStack(alignment: .leading, spacing: 8) {
                Text("Resolution").font(.headline)
                Picker("Resolution", selection: Binding(
                    get: { model.resolution },
                    set: { model.select(resolution: $0) }
                )) {
                    ForEach(StreamResolution.allCases) { res in
                        Text(res.rawValue).tag(res)
                    }
                }
                .pickerStyle(.segmented)
            }

            Toggle(isOn: Binding(
                get: { model.autoRotate },
                set: { model.setAutoRotate($0) }
            )) {
                VStack(alignment: .leading, spacing: 2) {
                    Text("Auto-rotate").font(.headline)
                    Text("Keeps the picture upright however you hold the phone")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
            }

            VStack(alignment: .leading, spacing: 8) {
                Text("Quality").font(.headline)
                Slider(value: Binding(
                    get: { model.quality },
                    set: { model.setQuality($0) }
                ), in: 0.3...0.9)
            }

            Spacer(minLength: 0)
        }
        .padding()
        .onAppear { model.onAppear() }
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
        view.previewLayer.videoGravity = .resizeAspect
        return view
    }

    func updateUIView(_ uiView: PreviewView, context: Context) {}
}
