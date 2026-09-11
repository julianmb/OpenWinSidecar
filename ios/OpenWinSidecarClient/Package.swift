// swift-tools-version: 5.8
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
