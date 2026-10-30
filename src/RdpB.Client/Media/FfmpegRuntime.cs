using FFmpeg.AutoGen;

namespace RdpB.Client.Media;

/// <summary>跟 RdpB.Host 那份一樣的找法,各自獨立一份避免 Client/Host 互相依賴對方的組件。</summary>
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
            var found = candidates.FirstOrDefault(p =>
                Directory.Exists(p) && Directory.GetFiles(p, "avcodec-*.dll").Length > 0);

            if (found is not null) ffmpeg.RootPath = found;
            _initialized = true;
        }
    }
}
