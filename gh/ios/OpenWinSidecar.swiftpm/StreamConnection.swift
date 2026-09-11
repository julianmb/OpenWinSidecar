import Foundation
import UIKit

public final class StreamConnection: NSObject, URLSessionWebSocketDelegate {
    private var webSocketTask: URLSessionWebSocketTask?
    private var session: URLSession?
    public let videoPipeline = VideoPipeline()

    public var onConnected: (() -> Void)?
    public var onDisconnected: ((String?) -> Void)?
    public var onFpsUpdate: ((Int) -> Void)?

    private var isConnected = false

    public override init() {
        super.init()
        let config = URLSessionConfiguration.default
        config.waitsForConnectivity = false
        self.session = URLSession(configuration: config, delegate: self, delegateQueue: OperationQueue())
    }

    public func connect(host: String, port: Int = 8080) {
        disconnect()

        guard let url = URL(string: "ws://\(host):\(port)/") else {
            onDisconnected?("Invalid server URL")
            return
        }

        let request = URLRequest(url: url, timeoutInterval: 5.0)
        webSocketTask = session?.webSocketTask(with: request)
        webSocketTask?.resume()

        receiveNextMessage()
    }

    public func disconnect() {
        isConnected = false
        webSocketTask?.cancel(with: .normalClosure, reason: nil)
        webSocketTask = nil
        videoPipeline.invalidate()
    }

    public func urlSession(_ session: URLSession, webSocketTask: URLSessionWebSocketTask, didOpenWithProtocol protocol: String?) {
        isConnected = true
        DispatchQueue.main.async { [weak self] in
            self?.onConnected?()
            self?.syncInitialSettings()
        }
    }

    public func urlSession(_ session: URLSession, webSocketTask: URLSessionWebSocketTask, didCloseWith closeCode: URLSessionWebSocketTask.CloseCode, reason: Data?) {
        let reasonStr = reason != nil ? String(data: reason!, encoding: .utf8) : nil
        isConnected = false
        DispatchQueue.main.async { [weak self] in
            self?.onDisconnected?(reasonStr)
        }
    }

    private func syncInitialSettings() {
        // Send iPad native screen resolution & DPI
        let screen = UIScreen.main
        let nativeBounds = screen.nativeBounds
        let w = Int(nativeBounds.width)
        let h = Int(nativeBounds.height)
        let maxHz = screen.maximumFramesPerSecond

        sendText("resolution:\(w)x\(h)@\(maxHz)")
        sendText("dpi:175")
        sendText("cursor:host")
        sendText("codec:hevc")
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
                    DispatchQueue.main.async {
                        self.onDisconnected?(error.localizedDescription)
                    }
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

        let payload = data.subdata(in: 15..<data.count)

        if codecType == 2 { // HEVC
            videoPipeline.decodeChunk(data: payload, timestampUs: timestampUs)
        }
    }

    private func handleTextMessage(text: String) {
        if text.hasPrefix("hevc:description:") {
            // Format: hevc:description:<codecString>:<base64-hvcC>
            let parts = text.components(separatedBy: ":")
            if parts.count >= 4 {
                let base64HvcC = parts[3]
                if let hvcCData = Data(base64Encoded: base64HvcC) {
                    _ = videoPipeline.configureWithHvcC(hvcCData: hvcCData)
                }
            }
        }
    }
}
