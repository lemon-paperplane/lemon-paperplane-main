// swift-tools-version:5.9
// PaperplaneKit：Mac 界面的非界面层（模型、API、事件流、daemon 管理、ANSI、SVG、i18n）。
// 不依赖 AppKit/SwiftUI，可用 `swift test` 单独测试。
import PackageDescription

let package = Package(
    name: "PaperplaneKit",
    platforms: [.macOS(.v13)],
    products: [
        .library(name: "PaperplaneKit", targets: ["PaperplaneKit"]),
        .executable(name: "pp-icongen", targets: ["pp-icongen"]),
    ],
    targets: [
        .target(name: "PaperplaneKit", path: "Sources/PaperplaneKit"),
        // 开发工具：用 SVG 渲染器把 shared/branding 下的图标源文件生成 AppIcon 各尺寸 PNG
        .executableTarget(name: "pp-icongen", dependencies: ["PaperplaneKit"], path: "Sources/pp-icongen"),
        .testTarget(
            name: "PaperplaneKitTests",
            dependencies: ["PaperplaneKit"],
            path: "Tests/PaperplaneKitTests",
            resources: [.copy("Fixtures")]
        ),
    ]
)
