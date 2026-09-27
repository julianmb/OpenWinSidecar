// swift-tools-version: 6.0
//
// Must be 6.0, not 5.8. The manifest uses `import AppleProductTypes` and the
// `.iOSApplication` product (with appIcon / accentColor / capabilities), all of
// which are Swift 6.0 features. Declaring 5.8 made SwiftPM reject the manifest at
// load time, so EVERY command failed - `swift build`, `xcodebuild -list`,
// `xcodebuild build` - on every Xcode version, for every run since this package
// was added. The CI job was red for two weeks over this one line.
import PackageDescription
import AppleProductTypes

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
            appIcon: .placeholder(icon: .display),
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
