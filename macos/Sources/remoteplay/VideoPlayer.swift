// 影像：直接用 ffplay 接 SRT 串流並顯示（與 Windows 版一致的低延遲旗標）。
// mac 上以 Homebrew 安裝 ffmpeg 即含 ffplay；1080p60 H.264 軟解在 Apple Silicon 上很輕鬆。
import Foundation

final class VideoPlayer {
    private var proc: Process?

    @discardableResult
    func start(host: String, port: Int, latency: Int, fullscreen: Bool) -> Bool {
        guard let ff = VideoPlayer.find("ffplay") else {
            print("[client] 找不到 ffplay，請先安裝：brew install ffmpeg")
            return false
        }
        let url = "srt://\(host):\(port)?latency=\(latency)"
        var args = [
            "-hide_banner", "-loglevel", "warning", "-an",
            "-probesize", "32", "-analyzeduration", "0",
            "-fflags", "nobuffer", "-flags", "low_delay", "-framedrop",
            "-window_title", "RemotePlay - \(host)"
        ]
        if fullscreen { args.append("-fs") }
        args.append(url)

        let p = Process()
        p.executableURL = URL(fileURLWithPath: ff)
        p.arguments = args
        do { try p.run() } catch {
            print("[client] 啟動 ffplay 失敗：\(error)")
            return false
        }
        proc = p
        print("[client] 影像視窗已開：\(url)")
        return true
    }

    func stop() { proc?.terminate() }
    var isRunning: Bool { proc?.isRunning ?? false }

    static func find(_ name: String) -> String? {
        for base in ["/opt/homebrew/bin/", "/usr/local/bin/", "/usr/bin/"] {
            let p = base + name
            if FileManager.default.isExecutableFile(atPath: p) { return p }
        }
        return nil
    }
}
