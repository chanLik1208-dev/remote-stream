using Avalonia.Platform.Storage;

namespace RdpB.App.Services;

/// <summary>讓 ViewModel 可以跳檔案選擇視窗,又不用直接持有 Window 參考(MVVM 避免 VM 碰 View)。
/// App.axaml.cs 在建好主視窗後把它的 StorageProvider 塞進來。</summary>
public static class FilePickerService
{
    public static IStorageProvider? StorageProvider { get; set; }

    public static async Task<string?> PickFileAsync(string title)
    {
        if (StorageProvider is null) return null;

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
        });

        return files.Count > 0 ? files[0].Path.LocalPath : null;
    }
}
