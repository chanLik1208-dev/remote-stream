// 極簡 UDP 傳送（POSIX socket）。與 Windows 版一樣：客戶端主動送封包到主機。
import Foundation

final class UDPSender {
    private let fd: Int32
    private var addr: sockaddr_in

    init?(host: String, port: UInt16) {
        fd = socket(AF_INET, SOCK_DGRAM, 0)
        if fd < 0 { return nil }

        var a = sockaddr_in()
        a.sin_family = sa_family_t(AF_INET)
        a.sin_port = port.bigEndian

        if inet_pton(AF_INET, host, &a.sin_addr) != 1 {
            // 非 IP 字面值 → 嘗試 DNS 解析
            guard let resolved = UDPSender.resolve(host) else { return nil }
            a.sin_addr = resolved
        }
        addr = a
    }

    func send(_ bytes: [UInt8]) {
        var a = addr
        _ = withUnsafePointer(to: &a) { ptr in
            ptr.withMemoryRebound(to: sockaddr.self, capacity: 1) { sa in
                bytes.withUnsafeBufferPointer { buf in
                    sendto(fd, buf.baseAddress, buf.count, 0, sa, socklen_t(MemoryLayout<sockaddr_in>.size))
                }
            }
        }
    }

    private static func resolve(_ host: String) -> in_addr? {
        var hints = addrinfo()
        hints.ai_family = AF_INET
        hints.ai_socktype = SOCK_DGRAM
        var res: UnsafeMutablePointer<addrinfo>?
        guard getaddrinfo(host, nil, &hints, &res) == 0, let r = res else { return nil }
        defer { freeaddrinfo(r) }
        let sin = r.pointee.ai_addr.withMemoryRebound(to: sockaddr_in.self, capacity: 1) { $0.pointee }
        return sin.sin_addr
    }
}
