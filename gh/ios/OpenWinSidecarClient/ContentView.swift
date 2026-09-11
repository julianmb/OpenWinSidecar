import SwiftUI

public struct ContentView: View {
    @StateObject private var viewModel = ContentViewModel()
    @State private var showSettings = false
    @State private var passwordInput = ""
    @Environment(\.scenePhase) private var scenePhase

    public init() {}

    public var body: some View {
        ZStack {
            Color.black.ignoresSafeArea()

            if viewModel.isConnected {
                // Streaming Canvas Layer
                StreamViewRepresentable(connection: viewModel.connection)
                    .ignoresSafeArea()

                // Top Floating Pill (FPS + Status + Settings)
                VStack {
                    HStack(spacing: 12) {
                        Circle()
                            .fill(Color.green)
                            .frame(width: 8, height: 8)

                        Text("OpenWinSidecar")
                            .font(.system(size: 13, weight: .bold))
                            .foregroundColor(.white)

                        Text("\(viewModel.currentFps) FPS")
                            .font(.system(size: 12, weight: .medium, design: .monospaced))
                            .foregroundColor(.white.opacity(0.8))

                        Button(action: { showSettings.toggle() }) {
                            Image(systemName: "gearshape.fill")
                                .font(.system(size: 13))
                                .foregroundColor(.white.opacity(0.85))
                        }
                    }
                    .padding(.horizontal, 16)
                    .padding(.vertical, 8)
                    .background(Color(white: 0.12, opacity: 0.85))
                    .clipShape(Capsule())
                    .overlay(Capsule().stroke(Color.white.opacity(0.18), lineWidth: 1))
                    .shadow(color: .black.opacity(0.4), radius: 10, y: 5)
                    .padding(.top, 12)

                    if let notice = viewModel.serverNotice {
                        Text(notice)
                            .font(.system(size: 12, weight: .medium))
                            .foregroundColor(.white)
                            .padding(.horizontal, 12)
                            .padding(.vertical, 6)
                            .background(Color.orange.opacity(0.85))
                            .clipShape(Capsule())
                    }

                    Spacer()
                }
            } else {
                // Setup / Connect Card
                VStack(spacing: 20) {
                    VStack(spacing: 8) {
                        Image(systemName: "display.2")
                            .font(.system(size: 48))
                            .foregroundColor(.blue)

                        Text("OpenWinSidecar")
                            .font(.system(size: 24, weight: .bold))
                            .foregroundColor(.white)

                        Text("Hardware HEVC 120Hz iPad Display")
                            .font(.system(size: 14))
                            .foregroundColor(.white.opacity(0.7))
                    }
                    .padding(.bottom, 8)

                    VStack(spacing: 12) {
                        VStack(alignment: .leading, spacing: 4) {
                            Text("Host PC IP (USB or Wi-Fi)")
                                .font(.system(size: 12, weight: .medium))
                                .foregroundColor(.white.opacity(0.6))

                            TextField("e.g. 172.20.10.2 or 192.168.1.12", text: $viewModel.hostIp)
                                .textFieldStyle(.plain)
                                .padding(12)
                                .background(Color(white: 0.18))
                                .cornerRadius(10)
                                .foregroundColor(.white)
                                .autocorrectionDisabled()
                                .textInputAutocapitalization(.never)
                        }

                        VStack(alignment: .leading, spacing: 4) {
                            Text("Port")
                                .font(.system(size: 12, weight: .medium))
                                .foregroundColor(.white.opacity(0.6))

                            TextField("8080", text: $viewModel.port)
                                .textFieldStyle(.plain)
                                .padding(12)
                                .background(Color(white: 0.18))
                                .cornerRadius(10)
                                .foregroundColor(.white)
                                .keyboardType(.numberPad)
                        }
                    }

                    if let error = viewModel.errorMessage {
                        Text(error)
                            .font(.system(size: 13))
                            .foregroundColor(.red)
                            .multilineTextAlignment(.center)
                    }

                    Button(action: { viewModel.connect() }) {
                        HStack {
                            Image(systemName: "bolt.fill")
                            Text("Connect Stream")
                                .fontWeight(.semibold)
                        }
                        .frame(maxWidth: .infinity)
                        .padding(.vertical, 14)
                        .background(Color.blue)
                        .foregroundColor(.white)
                        .cornerRadius(12)
                    }

                    HStack(spacing: 16) {
                        Button("Preset: USB (172.20.10.2)") {
                            viewModel.hostIp = "172.20.10.2"
                        }
                        .font(.system(size: 12, weight: .medium))
                        .foregroundColor(.blue)

                        Button("Preset: LAN (192.168.1.12)") {
                            viewModel.hostIp = "192.168.1.12"
                        }
                        .font(.system(size: 12, weight: .medium))
                        .foregroundColor(.blue)
                    }
                }
                .padding(32)
                .frame(maxWidth: 420)
                .background(Color(white: 0.12).opacity(0.96))
                .cornerRadius(24)
                .overlay(RoundedRectangle(cornerRadius: 24).stroke(Color.white.opacity(0.14), lineWidth: 1))
                .shadow(color: .black.opacity(0.6), radius: 30, y: 15)
            }
        }
        .sheet(isPresented: $showSettings) {
            SettingsSheet(viewModel: viewModel)
        }
        .sheet(isPresented: $viewModel.needsPassword) {
            NavigationStack {
                Form {
                    Section("This screen is password protected") {
                        SecureField("Password", text: $passwordInput)
                    }
                    if let authError = viewModel.authError {
                        Text(authError).foregroundColor(.red)
                    }
                }
                .navigationTitle("Password")
                .navigationBarTitleDisplayMode(.inline)
                .toolbar {
                    ToolbarItem(placement: .cancellationAction) {
                        Button("Cancel") {
                            passwordInput = ""
                            viewModel.needsPassword = false
                            viewModel.disconnect()
                        }
                    }
                    ToolbarItem(placement: .confirmationAction) {
                        Button("Watch") {
                            viewModel.connection.submitPassword(passwordInput)
                            passwordInput = ""
                        }
                    }
                }
            }
            .presentationDetents([.medium])
        }
        .onChange(of: scenePhase) { newPhase in
            // Returning from background: Safari-style decoders may have dropped reference frames,
            // so ask for a fresh IDR rather than showing corruption until the next keyframe.
            if newPhase == .active && viewModel.isConnected {
                viewModel.connection.sendForceIdr()
            }
        }
    }
}

final class ContentViewModel: ObservableObject {
    @Published var hostIp: String {
        didSet { UserDefaults.standard.set(hostIp, forKey: "lastHostIp") }
    }
    @Published var port: String = "8080"
    @Published var isConnected = false
    @Published var currentFps = 0
    @Published var errorMessage: String?
    @Published var needsPassword = false
    @Published var authError: String?
    @Published var serverNotice: String?

    let connection = StreamConnection()

    init() {
        self.hostIp = UserDefaults.standard.string(forKey: "lastHostIp") ?? "172.20.10.2"

        connection.onConnected = { [weak self] in
            self?.isConnected = true
            self?.errorMessage = nil
        }
        connection.onDisconnected = { [weak self] reason in
            self?.isConnected = false
            if let r = reason {
                self?.errorMessage = "Disconnected: \(r)"
            }
        }
        connection.onFpsUpdate = { [weak self] fps in
            DispatchQueue.main.async { self?.currentFps = fps }
        }
        connection.onAuthRequired = { [weak self] in
            DispatchQueue.main.async {
                self?.authError = nil
                self?.needsPassword = true
            }
        }
        connection.onAuthFailed = { [weak self] in
            DispatchQueue.main.async { self?.authError = "Wrong password — try again." }
        }
        connection.onAuthenticated = { [weak self] in
            DispatchQueue.main.async {
                self?.needsPassword = false
                self?.authError = nil
            }
        }
        connection.onCodecFallback = { [weak self] in
            DispatchQueue.main.async {
                self?.serverNotice = "Server encoder busy — video paused"
                DispatchQueue.main.asyncAfter(deadline: .now() + 6) { [weak self] in
                    self?.serverNotice = nil
                }
            }
        }
    }

    func connect() {
        errorMessage = nil
        let portInt = Int(port) ?? 8080
        connection.connect(host: hostIp.trimmingCharacters(in: .whitespaces), port: portInt)
    }

    func disconnect() {
        connection.disconnect()
        isConnected = false
    }
}

struct StreamViewRepresentable: UIViewRepresentable {
    let connection: StreamConnection

    func makeUIView(context: Context) -> UIView {
        let container = UIView()
        container.backgroundColor = .black

        let metalView = MetalDisplayView(frame: .zero)
        metalView.translatesAutoresizingMaskIntoConstraints = false
        container.addSubview(metalView)

        let inputManager = TouchInputManager(frame: .zero)
        inputManager.translatesAutoresizingMaskIntoConstraints = false
        container.addSubview(inputManager)

        NSLayoutConstraint.activate([
            metalView.topAnchor.constraint(equalTo: container.topAnchor),
            metalView.bottomAnchor.constraint(equalTo: container.bottomAnchor),
            metalView.leadingAnchor.constraint(equalTo: container.leadingAnchor),
            metalView.trailingAnchor.constraint(equalTo: container.trailingAnchor),

            inputManager.topAnchor.constraint(equalTo: container.topAnchor),
            inputManager.bottomAnchor.constraint(equalTo: container.bottomAnchor),
            inputManager.leadingAnchor.constraint(equalTo: container.leadingAnchor),
            inputManager.trailingAnchor.constraint(equalTo: container.trailingAnchor)
        ])

        connection.videoPipeline.onFrameDecoded = { [weak metalView] pixelBuffer in
            metalView?.enqueuePixelBuffer(pixelBuffer)
        }

        metalView.onFpsUpdate = { [weak connection] fps in
            DispatchQueue.main.async {
                connection?.onFpsUpdate?(fps)
            }
        }

        inputManager.onTouchInput = { [weak connection] action, x, y in
            connection?.sendInput(action: action, normX: x, normY: y)
        }
        inputManager.onScroll = { [weak connection] delta in
            connection?.sendScroll(delta: delta)
        }
        inputManager.onRightClick = { [weak connection] in
            connection?.sendRightClick()
        }

        return container
    }

    func updateUIView(_ uiView: UIView, context: Context) {}
}

struct SettingsSheet: View {
    @ObservedObject var viewModel: ContentViewModel
    @Environment(\.dismiss) var dismiss

    var body: some View {
        NavigationStack {
            Form {
                Section("Connection") {
                    LabeledContent("Status", value: viewModel.isConnected ? "Connected (120Hz Metal)" : "Disconnected")
                    LabeledContent("Host", value: "\(viewModel.hostIp):\(viewModel.port)")
                }

                Section("Windows Display Scale") {
                    Button("175% (Recommended for iPad 11\")") {
                        viewModel.connection.sendText("dpi:175")
                    }
                    Button("150%") { viewModel.connection.sendText("dpi:150") }
                    Button("200%") { viewModel.connection.sendText("dpi:200") }
                }

                Section("Quality") {
                    Button("50% — fastest") { viewModel.connection.sendText("quality:50") }
                    Button("65% — balanced") { viewModel.connection.sendText("quality:65") }
                    Button("80% — high detail") { viewModel.connection.sendText("quality:80") }
                    Button("90% — ultra crisp") { viewModel.connection.sendText("quality:90") }
                }

                Section("Stream Control") {
                    Button("Request Keyframe (Force IDR)") {
                        viewModel.connection.sendText("forceidr")
                    }
                    Button("Disconnect", role: .destructive) {
                        viewModel.disconnect()
                        dismiss()
                    }
                }
            }
            .navigationTitle("Sidecar Settings")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .confirmationAction) {
                    Button("Done") { dismiss() }
                }
            }
        }
    }
}
