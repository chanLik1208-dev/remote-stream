using System.Drawing;
using System.Drawing.Imaging;
using Avalonia.Controls;

namespace RdpB.App.Services;

/// <summary>系統匣圖示先用程式畫一個簡單的實心圓當佔位,不用額外準備美術資源就能動——
/// 之後有真的圖示素材再換成內嵌檔案。</summary>
public static class TrayIconFactory
{
    public static WindowIcon CreateDefaultIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            using var brush = new SolidBrush(Color.FromArgb(255, 47, 111, 237)); // 跟 App.axaml 的強調色一致
            g.FillEllipse(brush, 2, 2, 28, 28);
        }

        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        ms.Position = 0;
        return new WindowIcon(ms);
    }
}
