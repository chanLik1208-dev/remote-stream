// 手把：讀 macOS GameController → 打包成 XInput 佈局的 PAD 封包，送到主機的 ViGEm 虛擬手把。
// buttons 位元遮罩與 Windows/XInput 一致（主機 GamepadSink 直接對應）。
import GameController
import Foundation

final class Gamepad {
    private let sender: UDPSender
    private var timer: Timer?
    private var announced = false

    init(sender: UDPSender) { self.sender = sender }

    func start() {
        NotificationCenter.default.addObserver(forName: .GCControllerDidConnect, object: nil, queue: .main) { [weak self] _ in
            if self?.announced == false { print("[client] 偵測到手把，開始轉送"); self?.announced = true }
        }
        // ~125Hz 輪詢送出目前狀態
        let t = Timer(timeInterval: 0.008, repeats: true) { [weak self] _ in self?.poll() }
        RunLoop.main.add(t, forMode: .common)
        timer = t
    }

    private func poll() {
        guard let gp = GCController.controllers().first?.extendedGamepad else { return }

        var buttons: UInt16 = 0
        func set(_ pressed: Bool, _ mask: UInt16) { if pressed { buttons |= mask } }
        set(gp.dpad.up.isPressed, 0x0001)
        set(gp.dpad.down.isPressed, 0x0002)
        set(gp.dpad.left.isPressed, 0x0004)
        set(gp.dpad.right.isPressed, 0x0008)
        set(gp.buttonMenu.isPressed, 0x0010)                      // Start
        set(gp.buttonOptions?.isPressed ?? false, 0x0020)         // Back
        set(gp.leftThumbstickButton?.isPressed ?? false, 0x0040)  // L3
        set(gp.rightThumbstickButton?.isPressed ?? false, 0x0080) // R3
        set(gp.leftShoulder.isPressed, 0x0100)
        set(gp.rightShoulder.isPressed, 0x0200)
        set(gp.buttonHome?.isPressed ?? false, 0x0400)            // Guide
        set(gp.buttonA.isPressed, 0x1000)
        set(gp.buttonB.isPressed, 0x2000)
        set(gp.buttonX.isPressed, 0x4000)
        set(gp.buttonY.isPressed, 0x8000)

        let lt = UInt8(max(0, min(255, gp.leftTrigger.value * 255)))
        let rt = UInt8(max(0, min(255, gp.rightTrigger.value * 255)))
        func ax(_ v: Float) -> Int16 { Int16(max(-32767, min(32767, v * 32767))) }
        let lx = ax(gp.leftThumbstick.xAxis.value)
        let ly = ax(gp.leftThumbstick.yAxis.value)   // XInput 與 GC 皆為「上=正」
        let rx = ax(gp.rightThumbstick.xAxis.value)
        let ry = ax(gp.rightThumbstick.yAxis.value)

        var pkt: [UInt8] = [Proto.pad]
        pkt += u16le(buttons)
        pkt += [lt, rt]
        pkt += i16le(lx) + i16le(ly) + i16le(rx) + i16le(ry)
        sender.send(pkt)
    }
}
