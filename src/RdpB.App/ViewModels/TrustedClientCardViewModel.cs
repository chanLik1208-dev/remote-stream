using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RdpB.Core.Windows;

namespace RdpB.App.ViewModels;

/// <summary>
/// Host 分頁「已信任裝置」清單的一張卡片,包住一筆 <see cref="TrustedClientRecord"/>。
/// 指令直接放這裡、透過建構子傳進來的 callback 呼叫回 <see cref="HostViewModel"/>——
/// 跟 <see cref="DeviceCardViewModel"/> 同一個理由(見那個類別上的註解:避免 XAML 裡
/// 跨層 binding 在 PublishSingleFile 下炸掉)。
/// </summary>
public sealed partial class TrustedClientCardViewModel : ObservableObject
{
    private readonly Action<TrustedClientCardViewModel> _onRevoke;

    public TrustedClientRecord Record { get; }

    /// <summary>指紋只顯示前 16 個字元(縮短顯示用,畫面上這麼長的 hex string 沒有意義,
    /// 真的要核對完整指紋還是看 Host 分頁上面那個「本機身分憑證指紋」欄位,是完整的)。</summary>
    public string ShortFingerprint => Record.FingerprintHex.Length > 16
        ? Record.FingerprintHex[..16] + "…"
        : Record.FingerprintHex;

    public string PairedAtText => $"配對於:{Record.PairedAt.LocalDateTime:yyyy/MM/dd HH:mm}";
    public string LastConnectedText => $"上次連線:{Record.LastConnected.LocalDateTime:yyyy/MM/dd HH:mm}";

    public TrustedClientCardViewModel(TrustedClientRecord record, Action<TrustedClientCardViewModel> onRevoke)
    {
        Record = record;
        _onRevoke = onRevoke;
    }

    [RelayCommand]
    private void Revoke() => _onRevoke(this);
}
