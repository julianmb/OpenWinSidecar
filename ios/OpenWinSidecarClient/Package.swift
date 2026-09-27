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
    // Compile as Swift 5. Declaring tools-version 6.0 opts the whole package into the
    // Swift 6 language mode, where strict concurrency is enforced: the existing
    // UIViewRepresentable update closures that hand `self` to the connection are reported
    // as "sending 'self' risks causing data races", and that is a hard error. This code
    // was written against Swift 5 semantics and has not been concurrency-audited, so pin
    // the language mode rather than pretending it has been. Auditing it properly is real
    // work and belongs with someone who can test on a device.
    swiftLanguageModes: [.v5],
    targets: [
        .executableTarget(
            name: "AppModule",
            path: "."
        )
    ]
)
