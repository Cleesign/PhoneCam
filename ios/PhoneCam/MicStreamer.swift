import AVFoundation

/// Captures the iPhone microphone and hands out 48 kHz, mono, 16-bit PCM chunks.
///
/// Optional "noise reduction" uses Apple's voice processing (noise suppression and
/// automatic gain), the same processing FaceTime uses.
final class MicStreamer {
    static let sampleRate: Double = 48_000

    /// Called on an audio thread with little-endian Int16 samples.
    var chunkHandler: ((Data) -> Void)?
    /// Called on an audio thread with the chunk's peak level, 0…1.
    var levelHandler: ((Float) -> Void)?

    private let queue = DispatchQueue(label: "phonecam.mic")
    private var engine: AVAudioEngine?
    private var converter: AVAudioConverter?
    private let outFormat = AVAudioFormat(commonFormat: .pcmFormatInt16, sampleRate: MicStreamer.sampleRate,
                                          channels: 1, interleaved: true)!

    /// Starts (or restarts) the microphone. Completion gets an error message, or nil on success.
    func start(noiseReduction: Bool, completion: @escaping @MainActor (String?) -> Void) {
        queue.async {
            self.stopEngine()
            let error = self.startEngine(noiseReduction: noiseReduction)
            if error != nil { self.stopEngine() }
            Task { @MainActor in completion(error) }
        }
    }

    func stop() {
        queue.async {
            self.stopEngine()
            try? AVAudioSession.sharedInstance().setActive(false, options: .notifyOthersOnDeactivation)
        }
    }

    // MARK: - Engine (on `queue`)

    private func startEngine(noiseReduction: Bool) -> String? {
        let session = AVAudioSession.sharedInstance()
        do {
            try session.setCategory(.playAndRecord,
                                    mode: noiseReduction ? .videoChat : .default,
                                    options: [.mixWithOthers, .defaultToSpeaker])
            try? session.setPreferredSampleRate(MicStreamer.sampleRate)
            try? session.setPreferredIOBufferDuration(0.02)
            try session.setActive(true)
        } catch {
            return "The microphone couldn't start (\(error.localizedDescription))."
        }

        let engine = AVAudioEngine()
        let input = engine.inputNode
        if noiseReduction {
            try? input.setVoiceProcessingEnabled(true)
        }
        _ = engine.mainMixerNode // gives the engine a complete graph (needed with voice processing)

        let inFormat = input.outputFormat(forBus: 0)
        guard inFormat.sampleRate > 0, inFormat.channelCount > 0,
              let converter = AVAudioConverter(from: inFormat, to: outFormat) else {
            return "No microphone is available."
        }
        converter.downmix = true // voice processing can report several channels
        self.converter = converter

        input.installTap(onBus: 0, bufferSize: 960, format: inFormat) { [weak self] buffer, _ in
            self?.process(buffer)
        }
        do {
            engine.prepare()
            try engine.start()
        } catch {
            input.removeTap(onBus: 0)
            return "The microphone couldn't start (\(error.localizedDescription))."
        }
        self.engine = engine
        return nil
    }

    private func stopEngine() {
        guard let engine else { return }
        engine.inputNode.removeTap(onBus: 0)
        engine.stop()
        self.engine = nil
        converter = nil
    }

    // MARK: - Conversion (audio thread)

    private func process(_ buffer: AVAudioPCMBuffer) {
        guard let converter, buffer.frameLength > 0 else { return }
        let ratio = outFormat.sampleRate / buffer.format.sampleRate
        let capacity = AVAudioFrameCount(Double(buffer.frameLength) * ratio) + 64
        guard let out = AVAudioPCMBuffer(pcmFormat: outFormat, frameCapacity: capacity) else { return }

        var fed = false
        var error: NSError?
        let status = converter.convert(to: out, error: &error) { _, inputStatus in
            if fed {
                inputStatus.pointee = .noDataNow
                return nil
            }
            fed = true
            inputStatus.pointee = .haveData
            return buffer
        }
        guard status != .error, out.frameLength > 0, let samples = out.int16ChannelData?[0] else { return }

        let count = Int(out.frameLength)
        var peak: Int32 = 0
        for i in 0..<count {
            let v = abs(Int32(samples[i]))
            if v > peak { peak = v }
        }
        chunkHandler?(Data(bytes: samples, count: count * MemoryLayout<Int16>.size))
        levelHandler?(min(1, Float(peak) / 32767))
    }
}
