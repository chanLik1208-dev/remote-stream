using Xunit;

// HostListenerServiceStreamingTests 真的跑螢幕擷取+硬體編碼(NVENC),在 30 秒的配對逾時內
// 要跟其他 132 個測試搶 CPU 是會輸的——完整測試套件還是很快(不到幾秒),序列化執行換來的
// 是這類真機測試不會因為資源搶占而假性失敗,划得來。
[assembly: CollectionBehavior(DisableTestParallelization = true)]
