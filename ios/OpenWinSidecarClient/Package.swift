// swift-tools-version: 6.0
//
// Must be 6.0, not 5.8: `.iOSApplication` is a Swift 6.0 product type. Declaring 5.8
// made SwiftPM reject the manifest at load time, so every command failed regardless
// of the Xcode version selected.
//
// The app product, `AppleProductTypes` and the `PlaceholderIcon` cases are all Xcode 26
// era SwiftPM. None of them exist in Xcode 16, which reports "Product has no member
// 'iOSApplication'" - a toolchain problem, not a manifest problem. CI must run on
// macos-26 / Xcode 26; see .github/workflows/ios-ci.yml.
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
