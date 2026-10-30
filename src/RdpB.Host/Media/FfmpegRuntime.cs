using FFmpeg.AutoGen;

namespace RdpB.Host.Media;

/// <summary>找 FFmpeg shared DLL(跟 remote-stream 的 `Tools.Find` 同一套找法):優先用
/// exe 旁邊的 `ffmpeg\`,再退回已經在 PATH 上的安裝。整個程序只要呼叫一次。</summary>
public static class FfmpegRuntime
{
    private static bool _initialized;
    private static readonly object Lock = new();

    public static void EnsureInitialized()
    {
        if (_initialized) return;
        lock (Lock)
        {
            if (_initialized) return;

            var candidates = new[]
            {
                Path.Combine(AppContext.BaseDirectory, "ffmpeg"),
                Path.Combine(AppContext.BaseDirectory, "ffmpeg", "bin"),
            };
            var found = candidates.FirstOrDefault(p => Directory.Exists(p) && File.Exists(Path.Combine(p, FindAvcodecDllName(p) ?? "")));

            if (found is not null)
                ffmpeg.RootPath = found;
            // 找不到就讓 FFmpeg.AutoGen 走預設(PATH 上的安裝),失敗時呼叫端的 avcodec_find_encoder
            // 之類的呼叫會直接炸出清楚的例外,不會莫名其妙地卡住。

            _initialized = true;
        }
    }

    private static string? FindAvcodecDllName(string dir) =>
        Directory.Exists(dir) ? Directory.GetFiles(dir, "avcodec-*.dll").Select(Path.GetFileName).FirstOrDefault() : null;
}
