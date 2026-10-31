using Avalonia.Controls;
using RdpB.Core.Media;
using RdpB.Core.Windows;

namespace RdpB.App.Views;

/// <summary>「裝置」分頁每張卡片的齒輪按鈕開出來的編輯視窗——改名稱/位址/指紋,以及
/// 這台裝置要不要用跟全域不同的連線/畫質設定(<see cref="PinnedHostRecord.Overrides"/>/
/// <see cref="PinnedHostRecord.QualityOverrides"/>)。
///
/// 純 code-behind,直接改傳進來的 <see cref="PinnedHostRecord"/>,不另開 ViewModel/
/// 不用跨層 binding——跟 PinDialog/LaunchModeWindow 同一個簡單模式。按「儲存」才會真的
/// 改到 record(呼叫端自己負責存檔),按「取消」或直接關視窗完全不動 record。</summary>
public partial class EditHostDialog : Window
{
    private const string DirectLabel = "直連(DIRECT)";
    private const string ProxyLabel = "透過代理(PROXY)";

    /// <summary>Avalonia 的 XAML 執行期載入器(設計工具預覽用,不是真的執行路徑)需要一個
    /// public 無參數建構子才找得到這個型別——實際程式永遠透過下面那個帶 record 的建構子
    /// 建立,這個只是讓 AVLN3001 警告消失,不會真的被呼叫到。</summary>
    public EditHostDialog() : this(new PinnedHostRecord()) { }

    public EditHostDialog(PinnedHostRecord record)
    {
        InitializeComponent();

        NameBox.Text = record.Name;
        AddressBox.Text = record.HostAddress;
        FingerprintBox.Text = record.FingerprintHex;
        RequiresConnectionPasswordCheckBox.IsChecked = record.RequiresConnectionPassword;

        ModeCombo.ItemsSource = new[] { DirectLabel, ProxyLabel };
        FpsCombo.ItemsSource = FrameRateOptions.AllowedValues;

        var transport = record.Overrides;
        var quality = record.QualityOverrides;
        bool hasOverride = transport is not null || quality is not null;

        ModeCombo.SelectedItem = transport?.Mode is "tcp" or "ws" ? ProxyLabel : DirectLabel;
        DisableQuicCheckBox.IsChecked = transport?.DisableQuic ?? false;
        CamouflageCheckBox.IsChecked = transport?.RdpCamouflageWhenDirect ?? true;
        DedicatedPortCheckBox.IsChecked = transport?.RdpCamouflageUseDedicatedPort ?? false;
        FpsCombo.SelectedItem = quality?.Fps ?? FrameRateOptions.Default;
        BitrateBox.Value = quality?.BitrateKbps ?? 15_000;

        OverrideCheckBox.IsChecked = hasOverride;
        UpdateOverridePanelVisibility();
        OverrideCheckBox.IsCheckedChanged += (_, _) => UpdateOverridePanelVisibility();

        SaveButton.Click += (_, _) => TrySave(record);
        CancelButton.Click += (_, _) => Close(false);
    }

    private void UpdateOverridePanelVisibility() =>
        OverridePanel.IsVisible = OverrideCheckBox.IsChecked == true;

    private void TrySave(PinnedHostRecord record)
    {
        var address = (AddressBox.Text ?? "").Trim();
        if (address.Length == 0)
        {
            ErrorText.Text = "位址不能是空的";
            return;
        }

        // 跟 MainWindowViewModel.AddDevice 同一套格式檢查(64 個十六進位字元,允許冒號/
        // 空白分隔會自動去掉)——這裡是「修改」而不是「新增」,一樣的輸入錯誤不該有兩套
        // 不同的驗證標準,免得使用者改這裡沒擋到,改那裡才被擋。
        string fingerprint = (FingerprintBox.Text ?? "").Trim().Replace(" ", "").Replace(":", "").ToUpperInvariant();
        if (fingerprint.Length > 0 && (fingerprint.Length != 64 || !fingerprint.All(Uri.IsHexDigit)))
        {
            ErrorText.Text = "指紋格式不對——應該是 64 個十六進位字元(SHA-256),可以帶冒號或空白分隔,會自動去掉";
            return;
        }

        record.Name = string.IsNullOrWhiteSpace(NameBox.Text) ? address : NameBox.Text!.Trim();
        record.HostAddress = address;
        record.FingerprintHex = fingerprint;
        record.RequiresConnectionPassword = RequiresConnectionPasswordCheckBox.IsChecked == true;

        if (OverrideCheckBox.IsChecked == true)
        {
            record.Overrides = new TransportSettings
            {
                Mode = Equals(ModeCombo.SelectedItem, ProxyLabel) ? "tcp" : "auto",
                DisableQuic = DisableQuicCheckBox.IsChecked == true,
                RdpCamouflageWhenDirect = CamouflageCheckBox.IsChecked == true,
                RdpCamouflageUseDedicatedPort = DedicatedPortCheckBox.IsChecked == true,
            };
            record.QualityOverrides = new QualitySettings
            {
                Fps = (int)FpsCombo.SelectedItem!,
                BitrateKbps = (int)(BitrateBox.Value ?? 15_000),
            };
        }
        else
        {
            record.Overrides = null;
            record.QualityOverrides = null;
        }

        Close(true);
    }
}
