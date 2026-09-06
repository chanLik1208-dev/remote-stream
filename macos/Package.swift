// swift-tools-version:5.9
import PackageDescription

let package = Package(
    name: "remoteplay",
    platforms: [.macOS(.v12)],
    targets: [
        .executableTarget(
            name: "remoteplay",
            path: "Sources/remoteplay"
        )
    ]
)
