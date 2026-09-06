// 線路協定 —— 與 Windows 版 (input/Program.cs 的 Proto) 完全一致，little-endian。
//   HELLO(0)    : [0]=0                       客戶端連線通知(讓主機學到 IP，供音訊回送)
//   KEY(1)      : [0]=1,[1..2]=u16 scanCode,[3]=flags(bit0 down, bit1 extended)  ★PC set-1 掃描碼
//   MOUSEABS(2) : [0]=2,[1..2]=u16 x(0..65535),[3..4]=u16 y
//   MBTN(3)     : [0]=3,[1]=button(0=L,1=R,2=M,3=X1,4=X2),[2]=down
//   WHEEL(4)    : [0]=4,[1..2]=i16 delta(120=一格),[3]=horizontal(0/1)
//   MOUSEREL(5) : [0]=5,[1..2]=i16 dx,[3..4]=i16 dy
//   PAD(6)      : [0]=6,[1..2]=u16 buttons(XInput bitmask),[3]=LT,[4]=RT,[5..12]=4x i16 thumb(LX,LY,RX,RY)
enum Proto {
    static let hello: UInt8 = 0
    static let key: UInt8 = 1
    static let mouseAbs: UInt8 = 2
    static let mbtn: UInt8 = 3
    static let wheel: UInt8 = 4
    static let mouseRel: UInt8 = 5
    static let pad: UInt8 = 6
}

@inline(__always) func u16le(_ v: UInt16) -> [UInt8] { [UInt8(v & 0xff), UInt8(v >> 8)] }
@inline(__always) func i16le(_ v: Int16) -> [UInt8] { let u = UInt16(bitPattern: v); return [UInt8(u & 0xff), UInt8(u >> 8)] }
