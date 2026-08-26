using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AutoOCRTranslator.Utils;

/// <summary>生成应用图标：蓝底圆角 + 白色 Translate24 字体图标，渲染为位图无需资源文件。</summary>
public static class TrayIcon
{
    private static BitmapSource? _src;

    /// <summary>渲染图标为 BitmapSource（任务栏 Window.Icon 用，缓存避免重复渲染）。</summary>
    public static BitmapSource CreateImageSource()
    {
        if (_src != null) return _src;

        var border = new System.Windows.Controls.Border
        {
            Width = 128,
            Height = 128,
            CornerRadius = new CornerRadius(24),
            Background = new SolidColorBrush(Color.FromRgb(41, 98, 189)),
            Child = new Wpf.Ui.Controls.SymbolIcon(Wpf.Ui.Controls.SymbolRegular.Translate24)
            {
                FontSize = 96,
                Width = 96,
                Height = 96,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        border.Measure(new Size(128, 128));
        border.Arrange(new Rect(0, 0, 128, 128));

        var rtb = new RenderTargetBitmap(128, 128, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(border);
        rtb.Freeze();
        _src = rtb;
        return _src;
    }

    /// <summary>生成托盘图标 System.Drawing.Icon（从渲染位图转换）。</summary>
    public static System.Drawing.Icon Create()
    {
        BitmapSource src = CreateImageSource();
        using var ms = new MemoryStream();
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(src));
        enc.Save(ms);
        ms.Position = 0;
        using var bmp = new System.Drawing.Bitmap(ms);
        nint hIcon = bmp.GetHicon();
        try
        {
            using (System.Drawing.Icon tmp = System.Drawing.Icon.FromHandle(hIcon))
            {
                return (System.Drawing.Icon)tmp.Clone();
            }
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint hIcon);
}
