using Avalonia;

namespace RdpB.App;

internal static class Program
{
    // Avalonia 需要一個不經過 Main 初始化複雜流程的進入點,方便設計工具/測試使用。
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .WithInterFont()
        .LogToTrace();
}
