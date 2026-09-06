// remoteplay（macOS 客戶端）——連到現有的 Windows 主機，協定完全相容。
//   remoteplay client --host <ip> [--port 9000] [--input-port 9001] [--latency 40]
//                     [--mouse rel|abs] [--fullscreen] [--no-input] [--no-gamepad] [--no-video]
import Foundation
import Cocoa

let args = CommandLine.arguments
func opt(_ k: String) -> String? {
    if let i = args.firstIndex(of: "--\(k)"), i + 1 < args.count { return args[i + 1] }
    return nil
}
func flag(_ k: String) -> Bool { args.contains("--\(k)") }

guard let host = opt("host") else {
    print("""
    RemotePlay macOS 客戶端
    用法: remoteplay client --host <主機IP> [--port 9000] [--input-port 9001] [--latency 40]
                            [--mouse rel|abs] [--fullscreen] [--no-input] [--no-gamepad] [--no-video]
    操作: F8 = 控制主機 開/關    F9 = 滑鼠 相對(FPS)/絕對 切換
    需求: 已安裝 ffmpeg (brew install ffmpeg)；並在 系統設定→隱私與安全性→輔助使用 授權本程式。
    """)
    exit(1)
}

let videoPort = Int(opt("port") ?? "9000") ?? 9000
let inputPort = UInt16(opt("input-port") ?? "9001") ?? 9001
let latency = Int(opt("latency") ?? "40") ?? 40
let relativeDefault = (opt("mouse") ?? "rel") != "abs"

guard let sender = UDPSender(host: host, port: inputPort) else {
    print("[client] 無法建立 UDP 連線至 \(host):\(inputPort)")
    exit(2)
}
sender.send([Proto.hello]) // 讓主機學到本機 IP（供音訊回送）

let video = VideoPlayer()
if !flag("no-video") {
    _ = video.start(host: host, port: videoPort, latency: latency, fullscreen: flag("fullscreen"))
}

var gamepad: Gamepad?
if !flag("no-gamepad") {
    gamepad = Gamepad(sender: sender)
    gamepad?.start()
}

if !flag("no-input") {
    // F8 = 0x64, F9 = 0x65（macOS 虛擬鍵碼）
    let cap = InputCapture(sender: sender, toggle: 0x64, mode: 0x65, relativeDefault: relativeDefault)
    if cap.start() {
        print("[client] [F8] 控制主機 開/關   [F9] 滑鼠 相對(FPS)/絕對；目前滑鼠：\(relativeDefault ? "相對/FPS" : "絕對/桌面")")
    } else {
        print("[client] ⚠️ 無法安裝事件監聽。請到「系統設定 → 隱私與安全性 → 輔助使用」與「輸入監控」勾選本程式，再重跑。")
    }
}

print("[client] 已連線 \(host)（影像 :\(videoPort)、輸入 :\(inputPort)）。Ctrl+C 結束。")
CFRunLoopRun()
