using System.IO.Compression;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace RdpB.App.Services;

/// <summary>
/// 找/下載 mihomo.exe(github.com/MetaCubeX/mihomo,Clash Meta 的現役延續版)。跟
/// <see cref="FrpcRuntime"/> 是同一套手法(§「下載只做一次」「失敗要丟清楚例外」「下載到
/// 暫存檔、全部成功才搬到最終位置」的理由完全一樣,這裡不重複寫),差別只在挑 asset 的
/// 篩選規則。
///
/// **為什麼要 mihomo,不是自己解析訂閱格式**:Kyra 的訂閱裡有 Hysteria2(HY2)這類節點,
/// RdpB 自己的 transport 只握手 SOCKS5/HTTP CONNECT(見 <see cref="ProxyNodeResolver"/>
/// 類別上的註解),重新實作一套 VMess/VLESS/Trojan/Hysteria2 協定客戶端是完全不同量級的
/// 工程,不值得。mihomo 這類工具本來就是幹這件事的,而且是業界最被廣泛使用、持續在維護的
/// 實作之一——下載它、把訂閱連結交給它的 proxy-providers 機制去抓去解析,它自己對外開一個
/// 本機 mixed-port(同時接受 HTTP 和 SOCKS5),RdpB 只需要把這個本機 port 當成一般的
/// SOCKS5 代理接上去就好,不用碰任何一行 VLESS/HY2 的協定邏輯。
/// </summary>
public static class MihomoRuntime
{
    private const string LatestReleaseApiUrl = "https://api.github.com/repos/MetaCubeX/mihomo/releases/latest";

    private static readonly string MihomoDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RdpB", "mihomo");
    private static readonly string MihomoExePath = Path.Combine(MihomoDir, "mihomo.exe");

    private static Task<string>? _ensureTask;
    private static readonly object Lock = new();

    public static Task<string> EnsureAvailableAsync(CancellationToken ct = default)
    {
        lock (Lock)
        {
            if (File.Exists(MihomoExePath))
                return Task.FromResult(MihomoExePath);

            if (_ensureTask is null || _ensureTask.IsFaulted || _ensureTask.IsCanceled)
                _ensureTask = DownloadAndExtractAsync(ct);

            return _ensureTask;
        }
    }

    private static async Task<string> DownloadAndExtractAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(MihomoDir);

        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("RdpB-mihomo-downloader");
        http.Timeout = TimeSpan.FromSeconds(30);

        GitHubRelease release;
        try
        {
            release = await http.GetFromJsonAsync<GitHubRelease>(LatestReleaseApiUrl, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException("GitHub API 回傳了空的 release 資訊");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"查詢 mihomo 最新版本失敗(GitHub API):{ex.Message}", ex);
        }

        // mihomo 每個版本會同時發布一大串 windows/amd64 的變體(依 CPU 指令集分 v1/v2/v3、
        // 依建置用的 Go 版本分 go120~go125、還有 "compatible" 那份)。只要一般的
        // "windows"+"amd64" 關鍵字篩選會同時命中十幾個檔案,挑到哪個完全看字典順序,不可靠。
        // 用正規表示式鎖定「最陽春、相容性最好的那份」:`mihomo-windows-amd64-v<版本號>.zip`
        // ——後面直接接版本號,中間沒有 `-v1-`/`-v2-`/`-v3-`/`-compatible-`/`-go12x-`
        // 這幾種額外的 CPU/Go 版本標記。
        var pattern = new Regex(@"^mihomo-windows-amd64-v[\d.]+\.zip$", RegexOptions.IgnoreCase);
        var asset = release.Assets?.FirstOrDefault(a => a.Name is not null && pattern.IsMatch(a.Name));

        if (asset?.BrowserDownloadUrl is null)
            throw new InvalidOperationException(
                $"在 mihomo 最新版本({release.TagName})的 release 裡找不到通用版 windows/amd64 的 zip 檔" +
                "(mihomo-windows-amd64-v<版本>.zip)——GitHub 那邊的命名慣例可能變了。");

        var tempZip = Path.Combine(Path.GetTempPath(), $"mihomo-download-{Guid.NewGuid():N}.zip");
        var tempExtractDir = Path.Combine(Path.GetTempPath(), $"mihomo-extract-{Guid.NewGuid():N}");
        try
        {
            using (var response = await http.GetAsync(asset.BrowserDownloadUrl, ct).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                await using var fileStream = File.Create(tempZip);
                await response.Content.CopyToAsync(fileStream, ct).ConfigureAwait(false);
            }

            ZipFile.ExtractToDirectory(tempZip, tempExtractDir);

            // mihomo 的 zip 裡執行檔本身的檔名會帶版本/平台字串(例如 mihomo-windows-amd64.exe),
            // 不是固定叫 mihomo.exe——找任何一個 .exe 都直接當成它(zip 裡只有這一個執行檔)。
            var extractedExe = Directory.GetFiles(tempExtractDir, "*.exe", SearchOption.AllDirectories).FirstOrDefault()
                ?? throw new InvalidOperationException("下載的 zip 檔裡找不到任何 .exe");

            File.Copy(extractedExe, MihomoExePath, overwrite: true);

            return MihomoExePath;
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException($"下載/解壓 mihomo.exe 失敗:{ex.Message}", ex);
        }
        finally
        {
            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
            try { if (Directory.Exists(tempExtractDir)) Directory.Delete(tempExtractDir, recursive: true); } catch { }
        }
    }

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")] public string? TagName { get; set; }
        [JsonPropertyName("assets")] public List<GitHubAsset>? Assets { get; set; }
    }

    private sealed class GitHubAsset
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("browser_download_url")] public string? BrowserDownloadUrl { get; set; }
    }
}
