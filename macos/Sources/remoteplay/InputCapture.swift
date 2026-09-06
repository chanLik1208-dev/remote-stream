// 鍵鼠擷取（macOS 對應 Windows 的全域 hook + Raw Input）：
//   - CGEventTap 攔截鍵盤/滑鼠；active 時「吃掉」事件(回傳 nil)＝不影響本機，等同 Windows 攔截。
//   - F8 切換控制主機開/關；F9 切換 相對(FPS)/絕對。
//   - 相對模式：CGAssociateMouseAndMouseCursorPosition(false) 解除游標連動 + 讀 deltaX/deltaY，
//     游標不會被拉回中央，也不會亂跑 —— 就是 FPS 能玩的關鍵。
import Cocoa
import CoreGraphics

final class InputCapture {
    private let sender: UDPSender
    private let toggleVK: UInt16
    private let modeVK: UInt16
    private var active = false
    private var relative: Bool
    private let screenW: Double
    private let screenH: Double
    private var tap: CFMachPort?

    init(sender: UDPSender, toggle: UInt16, mode: UInt16, relativeDefault: Bool) {
        self.sender = sender
        self.toggleVK = toggle
        self.modeVK = mode
        self.relative = relativeDefault
        let b = CGDisplayBounds(CGMainDisplayID())
        self.screenW = Double(b.width)
        self.screenH = Double(b.height)
    }

    func start() -> Bool {
        func bit(_ t: CGEventType) -> UInt64 { (1 as UInt64) << t.rawValue }
        let mask: CGEventMask =
            bit(.keyDown) | bit(.keyUp) | bit(.flagsChanged) |
            bit(.mouseMoved) |
            bit(.leftMouseDown) | bit(.leftMouseUp) | bit(.leftMouseDragged) |
            bit(.rightMouseDown) | bit(.rightMouseUp) | bit(.rightMouseDragged) |
            bit(.otherMouseDown) | bit(.otherMouseUp) | bit(.otherMouseDragged) |
            bit(.scrollWheel)

        let refcon = Unmanaged.passUnretained(self).toOpaque()
        guard let tap = CGEvent.tapCreate(
            tap: .cgSessionEventTap,
            place: .headInsertEventTap,
            options: .defaultTap,
            eventsOfInterest: mask,
            callback: rpEventCallback,
            userInfo: refcon
        ) else { return false }

        self.tap = tap
        let src = CFMachPortCreateRunLoopSource(kCFAllocatorDefault, tap, 0)
        CFRunLoopAddSource(CFRunLoopGetCurrent(), src, .commonModes)
        CGEvent.tapEnable(tap: tap, enable: true)
        return true
    }

    // 由 C callback 轉呼叫。回傳 nil = 攔截本機；回傳 event = 放行。
    func handle(_ type: CGEventType, _ event: CGEvent) -> Unmanaged<CGEvent>? {
        let pass = Unmanaged.passUnretained(event)

        if type == .tapDisabledByTimeout || type == .tapDisabledByUserInput {
            if let t = tap { CGEvent.tapEnable(tap: t, enable: true) }
            return pass
        }

        switch type {
        case .keyDown, .keyUp:
            let vk = UInt16(event.getIntegerValueField(.keyboardEventKeycode))
            let down = (type == .keyDown)
            if down && vk == toggleVK {
                active.toggle()
                if active && relative { enterRelative() } else { exitRelative() }
                print(active ? "[client] 控制 ON  → 主機（滑鼠：\(relative ? "相對/FPS" : "絕對/桌面")）"
                             : "[client] 控制 OFF → 操作本機")
                return nil
            }
            if down && vk == modeVK {
                relative.toggle()
                if active { if relative { enterRelative() } else { exitRelative() } }
                print("[client] 滑鼠模式 → \(relative ? "相對/FPS（不重置游標）" : "絕對/桌面")")
                return nil
            }
            if active {
                sendKey(vk: vk, down: down)
                return nil
            }
            return pass

        case .flagsChanged:
            let vk = UInt16(event.getIntegerValueField(.keyboardEventKeycode))
            if active {
                let down = KeyMap.modifierIsDown(vk: vk, flags: event.flags)
                sendKey(vk: vk, down: down)
                return nil
            }
            return pass

        case .mouseMoved, .leftMouseDragged, .rightMouseDragged, .otherMouseDragged:
            if active {
                if relative {
                    let dx = event.getIntegerValueField(.mouseEventDeltaX)
                    let dy = event.getIntegerValueField(.mouseEventDeltaY)
                    if dx != 0 || dy != 0 {
                        sender.send([Proto.mouseRel] + i16le(Int16(clamping: dx)) + i16le(Int16(clamping: dy)))
                    }
                } else {
                    let loc = event.location
                    let nx = UInt16(max(0.0, min(65535.0, loc.x / max(1.0, screenW) * 65535.0)))
                    let ny = UInt16(max(0.0, min(65535.0, loc.y / max(1.0, screenH) * 65535.0)))
                    sender.send([Proto.mouseAbs] + u16le(nx) + u16le(ny))
                }
                return nil
            }
            return pass

        case .leftMouseDown:  return button(0, true, pass)
        case .leftMouseUp:    return button(0, false, pass)
        case .rightMouseDown: return button(1, true, pass)
        case .rightMouseUp:   return button(1, false, pass)
        case .otherMouseDown, .otherMouseUp:
            let n = event.getIntegerValueField(.mouseEventButtonNumber)
            let b: UInt8? = (n == 2) ? 2 : (n == 3) ? 3 : (n == 4) ? 4 : nil
            guard let bb = b else { return pass }
            return button(bb, type == .otherMouseDown, pass)

        case .scrollWheel:
            if active {
                let v1 = event.getIntegerValueField(.scrollWheelEventDeltaAxis1) // 垂直
                let v2 = event.getIntegerValueField(.scrollWheelEventDeltaAxis2) // 水平
                if v1 != 0 { sender.send([Proto.wheel] + i16le(Int16(clamping: v1 * 120)) + [0]) }
                if v2 != 0 { sender.send([Proto.wheel] + i16le(Int16(clamping: v2 * 120)) + [1]) }
                return nil
            }
            return pass

        default:
            return pass
        }
    }

    private func sendKey(vk: UInt16, down: Bool) {
        guard let (sc, ext) = KeyMap.scancode(for: vk) else { return }
        var flags: UInt8 = 0
        if down { flags |= 1 }
        if ext { flags |= 2 }
        sender.send([Proto.key, UInt8(sc & 0xff), UInt8(sc >> 8), flags])
    }

    private func button(_ b: UInt8, _ down: Bool, _ pass: Unmanaged<CGEvent>) -> Unmanaged<CGEvent>? {
        if active { sender.send([Proto.mbtn, b, down ? 1 : 0]); return nil }
        return pass
    }

    private func enterRelative() {
        CGAssociateMouseAndMouseCursorPosition(0)
        CGDisplayHideCursor(CGMainDisplayID())
    }
    private func exitRelative() {
        CGAssociateMouseAndMouseCursorPosition(1)
        CGDisplayShowCursor(CGMainDisplayID())
    }
}

// C 函式指標不能捕捉環境，故放在檔案層級，用 refcon 取回 InputCapture 實例。
private func rpEventCallback(proxy: CGEventTapProxy,
                            type: CGEventType,
                            event: CGEvent,
                            refcon: UnsafeMutableRawPointer?) -> Unmanaged<CGEvent>? {
    guard let refcon = refcon else { return Unmanaged.passUnretained(event) }
    let capture = Unmanaged<InputCapture>.fromOpaque(refcon).takeUnretainedValue()
    return capture.handle(type, event)
}
