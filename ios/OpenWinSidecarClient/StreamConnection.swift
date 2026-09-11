import Foundation
import UIKit
import CryptoKit

public final class StreamConnection: NSObject, URLSessionWebSocketDelegate {
    private var webSocketTask: URLSessionWebSocketTask?
    private var session: URLSession?
    public let videoPipeline = VideoPipeline()

    public var onConnected: (() -> Void)?
    public var onDisconnected: ((String?) -> Void)?
    public var onFpsUpdate: ((Int) -> Void)?
    public var onAuthRequired: (() -> Void)?
    public var onAuthFailed: (() -> Void)?
    public var onAuthenticated: (() -> Void)?
    public var onCodecFallback: (() -> Void)?

    private var isConnected = false
    private var shouldReconnect = false
    private var lastHost = ""
    private var lastPort = 8080
    private var reconnectAttempts = 0
    private var reconnectWorkItem: DispatchWorkItem?
    private var authChallenge: String?

    // Rolling stats window (fps/kbps) for the server Console's live clients view.
    private var statsTimer: Timer?
    private var statsBytes: Int64 = 0
    private var statsFrames = 0
    private var statsStart = Date()

    public override init() {
        super.init()
        let config = URLSessionConfiguration.default
        config.waitsForConnectivity = false
        self.session = URLSession(configuration: config, delegate: self, delegateQueue: OperationQueue())
        // Sustained decode failures mean our reference state is stale (corrupt chunk,
        // missed keyframe): ask the server for a fresh IDR. Server-side restarts are
        // rate-limited, and the pipeline only fires every 3rd consecutive failure.
        videoPipeline.onDecodeError = { [weak self] in self?.sendForceIdr() }
    }

    public func connect(host: String, port: Int = 8080) {
        disconnect()
        shouldReconnect = true
        reconnectAttempts = 0
        lastHost = host
        lastPort = port
        openSocket(host: host, port: port)
    }

    private func openSocket(host: String, port: Int) {
        // Announce HEVC up front so the very first frame is already hardware video (no JPEG flash).
        guard let url = URL(string: "ws://\(host):\(port)/?codec=hevc") else {
            onDisconnected?("Invalid server URL")
            return
        }
        authChallenge = nil
        resetStatsWindow()
        let request = URLRequest(url: url, timeoutInterval: 5.0)
        webSocketTask = session?.webSocketTask(with: request)
        webSocketTask?.resume()
        receiveNextMessage()
    }

    public func disconnect() {
        shouldReconnect = false
        reconnectWorkItem?.cancel()
        reconnectWorkItem = nil
        statsTimer?.invalidate()
        statsTimer = nil
        isConnected = false
        authChallenge = nil
        webSocketTask?.cancel(with: .normalClosure, reason: nil)
        webSocketTask = nil
        videoPipeline.invalidate()
    }

    public func urlSession(_ session: URLSession, webSocketTask: URLSessionWebSocketTask, didOpenWithProtocol protocol: String?) {
        isConnected = true
        reconnectAttempts = 0
        DispatchQueue.main.async { [weak self] in
            self?.onConnected?()
            self?.syncInitialSettings()
            self?.startStatsTimer()
        }
    }

    public func urlSession(_ session: URLSession, webSocketTask: URLSessionWebSocketTask, didCloseWith closeCode: URLSessionWebSocketTask.CloseCode, reason: Data?) {
        let reasonStr = reason != nil ? String(data: reason!, encoding: .utf8) : nil
        isConnected = false
        DispatchQueue.main.async { [weak self] in
            self?.onDisconnected?(reasonStr)
        }
        scheduleReconnect()
    }

    private func scheduleReconnect() {
        guard shouldReconnect else { return }
        reconnectWorkItem?.cancel()
        reconnectAttempts += 1
        let backoffs = [1.0, 2.0, 4.0, 8.0, 15.0]
        let delay = backoffs[min(reconnectAttempts - 1, backoffs.count - 1)]
        let item = DispatchWorkItem { [weak self] in
            guard let self = self, self.shouldReconnect else { return }
            self.openSocket(host: self.lastHost, port: self.lastPort)
        }
        reconnectWorkItem = item
        DispatchQueue.main.asyncAfter(deadline: .now() + delay, execute: item)
    }

    private func syncInitialSettings() {
        // Native screen size (physical pixels) + refresh; the server aspect-matches the VDD.
        let screen = UIScreen.main
        let nativeBounds = screen.nativeBounds
        let w = Int(nativeBounds.width)
        let h = Int(nativeBounds.height)
        let maxHz = Int(screen.maximumFramesPerSecond)

        sendText("set_res:\(w),\(h),\(maxHz)")
        sendText("dpi:175")
        sendText("cursor:host")
        sendText("codec:hevc")
    }

    public func submitPassword(_ password: String) {
        // Challenge-response: SHA-256(password + challenge), so the password never crosses the wire.
        if let challenge = authChallenge {
            let digest = SHA256.hash(data: Data((password + challenge).utf8))
            let hex = digest.map { String(format: "%02x", $0) }.joined()
            sendText("auth:" + hex)
        } else {
            // Legacy servers that never issue a challenge.
            sendText("auth:" + password)
        }
    }

    public func sendForceIdr() {
        sendText("forceidr")
    }

    public func sendText(_ text: String) {
        guard let ws = webSocketTask else { return }
        ws.send(.string(text)) { error in
            if let err = error {
                print("[StreamConnection] Send text error: \(err)")
            }
        }
    }

    public func sendInput(action: String, normX: Float, normY: Float) {
        let xStr = String(format: "%.4f", normX)
        let yStr = String(format: "%.4f", normY)
        sendText("input:\(action),\(xStr),\(yStr)")
    }

    public func sendScroll(delta: Int) {
        sendText("scroll:\(delta)")
    }

    public func sendRightClick() {
        sendText("rightclick")
    }

    private func startStatsTimer() {
        statsTimer?.invalidate()
        resetStatsWindow()
        statsTimer = Timer.scheduledTimer(withTimeInterval: 5.0, repeats: true) { [weak self] _ in
            self?.sendStats()
        }
    }

    private func resetStatsWindow() {
        statsBytes = 0
        statsFrames = 0
        statsStart = Date()
    }

    private func sendStats() {
        let elapsed = Date().timeIntervalSince(statsStart)
        guard elapsed > 0 else { return }
        let fps = Int(Double(statsFrames) / elapsed)
        let kbps = Int(Double(statsBytes * 8) / elapsed / 1000.0)
        sendText("stats:{\"fps\":\(fps),\"kbps\":\(kbps)}")
        resetStatsWindow()
    }

    private func receiveNextMessage() {
        guard let ws = webSocketTask else { return }

        ws.receive { [weak self] result in
            guard let self = self else { return }

            switch result {
            case .success(let message):
                switch message {
                case .data(let data):
                    self.handleBinaryPacket(data: data)
                case .string(let text):
                    self.handleTextMessage(text: text)
                @unknown default:
                    break
                }
                self.receiveNextMessage()

            case .failure(let error):
                if self.isConnected {
                    self.isConnected = false
                    DispatchQueue.main.async {
                        self.onDisconnected?(error.localizedDescription)
                    }
                    self.scheduleReconnect()
                }
            }
        }
    }

    private func handleBinaryPacket(data: Data) {
        guard data.count > 15 else { return }

        let codecType = data[0]
        let flags = data[1]
        let isKeyframe = (flags & 0x01) != 0

        // Parse 8-byte big-endian timestampUs
        var timestampUs: Int64 = 0
        _ = withUnsafeMutableBytes(of: &timestampUs) { ptr in
            data.copyBytes(to: ptr, from: 2..<10)
        }
        timestampUs = Int64(bigEndian: timestampUs)

        statsBytes += Int64(data.count)
        statsFrames += 1

        let payload = data.subdata(in: 15..<data.count)

        if codecType == 2 { // HEVC
            videoPipeline.decodeChunk(data: payload, timestampUs: timestampUs, isKeyframe: isKeyframe)
        }
    }

    private func handleTextMessage(text: String) {
        // Access-password handshake (challenge-response; see server AuthenticateSessionAsync).
        if text == "auth:required" || text.hasPrefix("authreq:") {
            if text.hasPrefix("authreq:") {
                authChallenge = String(text.dropFirst("authreq:".count))
            }
            DispatchQueue.main.async { [weak self] in self?.onAuthRequired?() }
            return
        }
        if text == "auth:ok" {
            authChallenge = nil
            DispatchQueue.main.async { [weak self] in
                self?.onAuthenticated?()
                self?.syncInitialSettings()
            }
            return
        }
        if text == "auth:denied" {
            DispatchQueue.main.async { [weak self] in self?.onAuthFailed?() }
            return
        }
        // The server fell back to JPEG (e.g. encoder cap). This client only decodes HEVC.
        if text == "codec:intra" {
            DispatchQueue.main.async { [weak self] in self?.onCodecFallback?() }
            return
        }
        // One-shot hvcC description + codec string: desc:<codec>|<base64-hvcC>
        if text.hasPrefix("desc:") {
            let body = String(text.dropFirst("desc:".count))
            if let sep = body.firstIndex(of: "|") {
                let b64 = String(body[body.index(after: sep)...])
                if let hvcCData = Data(base64Encoded: b64) {
                    _ = videoPipeline.configureWithHvcC(hvcCData: hvcCData)
                }
            }
            return
        }
    }
}
