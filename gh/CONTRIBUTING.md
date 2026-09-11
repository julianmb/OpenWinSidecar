# Contributing to OpenWinSidecar

Thank you for your interest in contributing to **OpenWinSidecar**! We welcome bug reports, feature suggestions, documentation enhancements, and pull requests.

---

## 🛠️ Development Setup

### Prerequisites
- Windows 10 (1607+) or Windows 11
- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- Visual Studio 2022 / 2025 or JetBrains Rider or VS Code with C# Dev Kit
- (Optional for HEVC encode) Intel Arc GPU or hardware QuickSync / FFmpeg

### Building the Project
```powershell
git clone https://github.com/YourUsername/OpenWinSidecar.git
cd OpenWinSidecar
dotnet build OpenWinSidecar.slnx
```

### Running OpenWinSidecar Service
```powershell
# Interactive development mode:
dotnet run --project src/OpenWinSidecar.Service/OpenWinSidecar.Service.csproj
```

---

## 📐 Project Structure

- `src/OpenWinSidecar.Core`: Win32 CCD DPI engine, Virtual Display Manager, and Windows Service controller.
- `src/OpenWinSidecar.Service`: Real-time screen capture (DXGI Desktop Duplication / GDI), hardware encoder (HEVC QSV), multi-port HTTP/WebSocket server, and input dispatcher.
- `src/OpenWinSidecar`: WPF management dashboard and system tray interface.
- `src/OpenWinSidecar.Cli`: Lightweight command-line utility for automation and headless status queries.
- `drivers/VDD`: Signed IddCx Virtual Display Driver configuration and installer files.
- `docs/`: Technical guides, architecture diagrams, and hardware benchmarks.

---

## 📋 Submitting a Pull Request

1. Fork the repository and create a new feature branch (`git checkout -b feature/amazing-feature`).
2. Follow standard C# coding conventions and `.NET 10` modern idioms.
3. Test your changes with an iPad or remote browser to ensure low-latency responsiveness.
4. Ensure the solution compiles with `dotnet build OpenWinSidecar.slnx` with zero errors or warnings.
5. Submit a descriptive Pull Request detailing the changes and testing results.

---

## 📄 License
By contributing to OpenWinSidecar, you agree that your contributions will be licensed under the [MIT License](LICENSE).
