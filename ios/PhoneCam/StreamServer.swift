import Foundation
import Network

/// TCP server on the iPhone's loopback interface.
///
/// The Windows app reaches it through the USB cable via Apple's usbmux service
/// (the same mechanism Xcode and iTunes use). Because it only binds to 127.0.0.1,
/// it is NOT reachable over Wi-Fi.
///
/// Wire format, both directions:  [UInt32 big-endian payload length][UInt8 type][payload]
///   0x01  phone → PC   JPEG frame
///   0x02  phone → PC   status (JSON)
///   0x03  phone → PC   microphone audio: [UInt32 BE sample rate][UInt8 channels][UInt8 0][Int16 LE PCM]
///   0x10  PC → phone   command (JSON), e.g. {"camera":"front"}
final class StreamServer {
    static let port: UInt16 = 5858

    enum MessageType: UInt8 {
        case jpegFrame = 0x01
        case status = 0x02
        case audio = 0x03
        case command = 0x10
    }

    /// Called on the server's queue.
    var onClientChanged: ((Bool) -> Void)?
    /// Called on the server's queue.
    var onCommand: (([String: Any]) -> Void)?

    private let queue = DispatchQueue(label: "phonecam.net")
    private var listener: NWListener?
    private var client: NWConnection?

    private let lock = NSLock()
    private var inFlight = 0
    private var hasClient = false

    /// True when a laptop is connected and the send queue isn't backed up.
    /// Frames are dropped (rather than queued) when this is false, which keeps latency low.
    var isClientConnected: Bool {
        lock.lock(); defer { lock.unlock() }
        return hasClient
    }

    var canAcceptFrame: Bool {
        lock.lock(); defer { lock.unlock() }
        return hasClient && inFlight < 2
    }

    func start() {
        queue.async { self.startListener() }
    }

    // MARK: - Listening

    private func startListener() {
        guard listener == nil, let port = NWEndpoint.Port(rawValue: Self.port) else { return }

        let params = NWParameters.tcp
        params.allowLocalEndpointReuse = true
        params.requiredLocalEndpoint = .hostPort(host: .ipv4(.loopback), port: port)

        let newListener: NWListener
        do {
            newListener = try NWListener(using: params)
        } catch {
            retryListener()
            return
        }

        newListener.newConnectionHandler = { [weak self] connection in
            self?.accept(connection)
        }
        newListener.stateUpdateHandler = { [weak self] state in
            guard let self else { return }
            switch state {
            case .failed, .cancelled:
                self.listener?.cancel()
                self.listener = nil
                self.retryListener()
            default:
                break
            }
        }
        listener = newListener
        newListener.start(queue: queue)
    }

    private func retryListener() {
        queue.asyncAfter(deadline: .now() + 1) { [weak self] in self?.startListener() }
    }

    private func accept(_ connection: NWConnection) {
        // One laptop at a time: a new connection replaces the old one.
        if let old = client {
            client = nil
            old.cancel()
        }
        client = connection
        setClientState(connected: false)

        connection.stateUpdateHandler = { [weak self, weak connection] state in
            guard let self, let connection else { return }
            switch state {
            case .ready:
                guard connection === self.client else { return }
                self.setClientState(connected: true)
                self.onClientChanged?(true)
                self.receiveHeader(on: connection)
            case .failed, .cancelled:
                self.connectionEnded(connection)
            default:
                break
            }
        }
        connection.start(queue: queue)
    }

    private func connectionEnded(_ connection: NWConnection) {
        guard connection === client else { return }
        client = nil
        setClientState(connected: false)
        onClientChanged?(false)
    }

    private func setClientState(connected: Bool) {
        lock.lock()
        hasClient = connected
        inFlight = 0
        lock.unlock()
    }

    // MARK: - Sending

    /// `tracked` messages count towards the video back-pressure; small audio packets don't,
    /// so the microphone never makes the camera drop frames (or the other way round).
    func send(type: MessageType, payload: Data, tracked: Bool = true) {
        if tracked { lock.lock(); inFlight += 1; lock.unlock() }

        queue.async {
            guard let connection = self.client else {
                if tracked { self.finishSend() }
                return
            }
            var packet = Data(capacity: payload.count + 5)
            let length = UInt32(payload.count)
            packet.append(UInt8((length >> 24) & 0xFF))
            packet.append(UInt8((length >> 16) & 0xFF))
            packet.append(UInt8((length >> 8) & 0xFF))
            packet.append(UInt8(length & 0xFF))
            packet.append(type.rawValue)
            packet.append(payload)

            connection.send(content: packet, completion: .contentProcessed { [weak self] error in
                if tracked { self?.finishSend() }
                if error != nil { connection.cancel() }
            })
        }
    }

    func sendJSON(type: MessageType, _ object: [String: Any]) {
        guard let data = try? JSONSerialization.data(withJSONObject: object) else { return }
        send(type: type, payload: data)
    }

    private func finishSend() {
        lock.lock()
        inFlight = max(0, inFlight - 1)
        lock.unlock()
    }

    // MARK: - Receiving

    private func receiveHeader(on connection: NWConnection) {
        connection.receive(minimumIncompleteLength: 5, maximumLength: 5) { [weak self] data, _, _, error in
            guard let self else { return }
            guard error == nil, let data, data.count == 5 else {
                connection.cancel()
                return
            }
            let bytes = [UInt8](data)
            let length = Int(bytes[0]) << 24 | Int(bytes[1]) << 16 | Int(bytes[2]) << 8 | Int(bytes[3])
            let type = bytes[4]

            guard length <= 1_000_000 else {
                connection.cancel()
                return
            }
            if length == 0 {
                self.handle(type: type, payload: Data())
                self.receiveHeader(on: connection)
                return
            }
            connection.receive(minimumIncompleteLength: length, maximumLength: length) { [weak self] body, _, _, error in
                guard let self else { return }
                guard error == nil, let body, body.count == length else {
                    connection.cancel()
                    return
                }
                self.handle(type: type, payload: body)
                self.receiveHeader(on: connection)
            }
        }
    }

    private func handle(type: UInt8, payload: Data) {
        guard type == MessageType.command.rawValue,
              let object = try? JSONSerialization.jsonObject(with: payload),
              let command = object as? [String: Any] else { return }
        onCommand?(command)
    }
}
