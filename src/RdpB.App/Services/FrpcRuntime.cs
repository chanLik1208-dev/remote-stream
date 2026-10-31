using System.IO.Compression;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace RdpB.App.Services;

/// <summary>
/// 找/下載 frpc.exe(github.com/fatedier/frp)。跟 FfmpegRuntime 不一樣——FFmpeg 是
/// Kyra 自己手動放好的,這個是 Kyra 明確要求「RDP-B 自動從 GitHub 下載」,所以這裡真的會
/// 打一次網路請求去 GitHub Releases API。
///
/// **安全/意外處理的重點**(跟這個 session 稍早複查 FFmpeg 打包那次不一樣,這次是執行期
/// 會真的跑的程式碼,不是建置腳本):
///  - 下載只做一次(用 <see cref="Lazy{T}"/> 包住整個流程),同時間多個呼叫端(例如使用者
///    連按兩次「啟動 Host」,或 GUI 上同時有「測試 frpc」按鈕)不會各自重複下載、互相干擾
///    同一個檔案的寫入。
///  - 任何一步失敗(網路、GitHub API 格式變了、找不到對應平台的 asset、解壓縮失敗)都要
///    丟出清楚的例外,讓呼叫端(FrpTunnelService)能夠優雅處理,不是讓整個 Host 開不起來。
///  - 下載到暫存檔、解壓完驗證 frpc.exe 真的存在、整個成功才搬到最終位置——避免下載
///    一半或解壓失敗時,留下一個「看起來存在但其實是空的/壞的」frpc.exe,之後每次都误判
///    成「已經下載過了」而不會重試。
/// </summary>
public static class FrpcRuntime
{
    private const string LatestReleaseApiUrl = "https://api.github.com/repos/fatedier/frp/releases/latest";

    private static readonly string FrpDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RdpB", "frp");
    private static readonly string FrpcExePath = Path.Combine(FrpDir, "frpc.exe");

    private static Task<string>? _ensureTask;
    private static readonly object Lock = new();

    /// <summary>回傳可用的 frpc.exe 路徑——本機已經有就直接用,沒有才去下載。
    /// 多個呼叫端同時呼叫只會真的下載一次(見類別上的註解)。</summary>
    public static Task<string> EnsureAvailableAsync(CancellationToken ct = default)
    {
        lock (Lock)
        {
            if (File.Exists(FrpcExePath))
                return Task.FromResult(FrpcExePath);

            // 這個 Lazy 風格的快取只在「還沒成功過」時建立——如果上一次下載失敗了(Task
            // Faulted),下一次呼叫應該要能重新再試一次,不是永遠卡在同一個失敗的 Task 上。
            if (_ensureTask is null || _ensureTask.IsFaulted || _ensureTask.IsCanceled)
                _ensureTask = DownloadAndExtractAsync(ct);

            return _ensureTask;
        }
    }

    private static async Task<string> DownloadAndExtractAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(FrpDir);

        using var http = new HttpClient();
        // GitHub API 要求一定要帶 User-Agent,不帶會被拒絕(不是認證問題,是 GitHub 的硬性規定)。
        http.DefaultRequestHeaders.UserAgent.ParseAdd("RdpB-frpc-downloader");
        http.Timeout = TimeSpan.FromSeconds(30);

        GitHubRelease release;
        try
        {
            release = await http.GetFromJsonAsync<GitHubRelease>(LatestReleaseApiUrl, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException("GitHub API 回傳了空的 release 資訊");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"查詢 frp 最新版本失敗(GitHub API):{ex.Message}", ex);
        }

        // frp 的 release asset 命名慣例:frp_<version去掉開頭的v>_windows_amd64.zip
        // (例如 frp_0.58.1_windows_amd64.zip)。用「windows」+「amd64」+「.zip」當關鍵字
        // 篩選,不要整段死綁版本字串格式——版本號格式本身可能隨時間變動,但平台/架構這兩個
        // 關鍵字是穩定的。
        var asset = release.Assets?.FirstOrDefault(a =>
            a.Name is not null &&
            a.Name.Contains("windows", StringComparison.OrdinalIgnoreCase) &&
            a.Name.Contains("amd64", StringComparison.OrdinalIgnoreCase) &&
            a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));

        if (asset?.BrowserDownloadUrl is null)
            throw new InvalidOperationException(
                $"在 frp 最新版本({release.TagName})的 release 裡找不到 windows/amd64 的 zip 檔——GitHub 那邊的命名慣例可能變了。");

        var tempZip = Path.Combine(Path.GetTempPath(), $"frpc-download-{Guid.NewGuid():N}.zip");
        var tempExtractDir = Path.Combine(Path.GetTempPath(), $"frpc-extract-{Guid.NewGuid():N}");
        try
        {
            using (var response = await http.GetAsync(asset.BrowserDownloadUrl, ct).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                await using var fileStream = File.Create(tempZip);
                await response.Content.CopyToAsync(fileStream, ct).ConfigureAwait(false);
            }

            ZipFile.ExtractToDirectory(tempZip, tempExtractDir);

            var extractedFrpc = Directory.GetFiles(tempExtractDir, "frpc.exe", SearchOption.AllDirectories).FirstOrDefault()
                ?? throw new InvalidOperationException("下載的 zip 檔裡找不到 frpc.exe");

            // 整個流程都成功了才搬到最終位置——avoid 留下半成品。File.Copy 用 overwrite:true,
            // 因為理論上這裡走到時 FrpcExePath 應該還不存在(上面已經檢查過),但多個並行呼叫
            // 在極端時機下可能都走到這裡,覆蓋寫同一個目的地是安全的(反正內容一樣)。
            File.Copy(extractedFrpc, FrpcExePath, overwrite: true);

            return FrpcExePath;
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException($"下載/解壓 frpc.exe 失敗:{ex.Message}", ex);
        }
        finally
        {
            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { /* 暫存檔清不掉不是致命問題 */ }
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
