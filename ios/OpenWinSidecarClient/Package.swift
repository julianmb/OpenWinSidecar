// swift-tools-version: 6.0
//
// Must be 6.0, not 5.8: `.iOSApplication` is a Swift 6.0 product type. Declaring 5.8
// made SwiftPM reject the manifest at load time, so EVERY command failed - `swift build`,
// `xcodebuild -list`, `xcodebuild build` - on every Xcode version, for every run since
// this package was added.
//
// Do NOT add `import AppleProductTypes`. That module does not exist in the Xcode 16
// PackageDescription; the app product and its settings types come from PackageDescription
// itself. The import is a hard error ("no such module") and it masks the real
// diagnostics, because a manifest that fails to import never gets far enough to say
// anything useful about its contents.
import PackageDescription

let package = Package(
    name: "OpenWinSidecar",
    platforms: [
        .iOS("16.0")
    ],
    products: [
        .iOSApplication(
            name: "OpenWinSidecar",
            targets: ["AppModule"],
            bundleIdentifier: "com.openwinsidecar.client",
            displayVersion: "1.0",
            bundleVersion: "1",
            appIcon: .placeholder(icon: .app),
            accentColor: .presetColor(.blue),
            supportedDeviceFamilies: [
                .pad,
                .phone
            ],
            supportedInterfaceOrientations: [
                .landscapeRight,
                .landscapeLeft
            ],
            capabilities: [
                .localNetwork(purposeString: "Connect to OpenWinSidecar host on your local network or USB cable.")
            ]
        )
    ],
    targets: [
        .executableTarget(
            name: "AppModule",
            path: "."
        )
    ]
)
