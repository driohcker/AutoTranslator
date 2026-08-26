using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using AutoOCRTranslator.Capture;
using AutoOCRTranslator.Models;
using AutoOCRTranslator.Settings;
using Serilog;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace AutoOCRTranslator.Overlay;

/// <summary>
/// 翻译覆盖层窗口（对应 Python 版 overlay_window.py）。
/// 无边框、透明、置顶、点击穿透，100ms 跟随目标窗口客户区；
/// 在原文位置绘制译文（半透明圆角背景框 + 自动换行文本），
/// 并按需高亮自定义 ROI 区域。只在内容变化时重建子元素，不做逐帧重绘。
/// </summary>
public sealed partial class OverlayWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x00000020; // 点击穿透
    private const int WsExToolWindow = 0x00000080;  // 不占任务栏

    private readonly WindowCapture _capture = new();
    private readonly DispatcherTimer? _followTimer;
    private readonly OverlaySection _style;
    // 全屏模式下覆盖层需排除出屏幕截图（BitBlt 会抓到叠加的层级窗口，
    // 导致 OCR 反复识别自身译文 → 振荡）。WDA_EXCLUDEFROMCAPTURE 让窗口对用户可见但不出现在截图里。
    private readonly bool _excludeFromCapture;

    // 预解析的颜色/字体（避免每帧重建）
    private readonly Brush _fontBrush;
    private readonly Brush _bgBrush;
    private readonly Brush _borderBrush;
    private readonly FontFamily _fontFamily;
    private readonly Brush _zoneFill = new SolidColorBrush(Color.FromArgb(30, 255, 0, 0));
    private readonly Brush _zoneBorder = new SolidColorBrush(Color.FromArgb(120, 255, 0, 0));

    public OverlayWindow(nint targetHwnd, OverlaySection style, CaptureMode mode)
    {
        InitializeComponent();
        _style = style;
        _excludeFromCapture = mode == CaptureMode.FullScreen;

        _fontBrush = ParseColor(style.FontColor, Colors.White);
        _bgBrush = ParseColor(style.BgColor, Color.FromArgb(128, 0, 0, 0));
        _borderBrush = ParseColor(style.BorderColor, Colors.Black);
        _fontFamily = new FontFamily(style.FontFamily);
        _zoneFill.Freeze();
        _zoneBorder.Freeze();

        EnableClickThrough();

        if (mode == CaptureMode.FullScreen)
        {
            // 全屏模式：固定铺满虚拟屏幕，不跟随任何窗口（target rect = 虚拟屏，不可移动）
            Left = SystemParameters.VirtualScreenLeft;
            Top = SystemParameters.VirtualScreenTop;
            Width = SystemParameters.VirtualScreenWidth;
            Height = SystemParameters.VirtualScreenHeight;
            Show();
            Log.Information("覆盖层窗口创建（全屏模式）：{Width}x{Height}, 字号={Size}", Width, Height, style.FontSize);
        }
        else
        {
            _capture.SetTarget(targetHwnd);
            _followTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            _followTimer.Tick += (_, _) => FollowTarget();
            _followTimer.Start();
            FollowTarget();
            Log.Information("覆盖层窗口创建：目标 hwnd={Hwnd}, 字号={Size}", targetHwnd, style.FontSize);
        }
    }

    /// <summary>更新显示内容（原文+译文+区域高亮），内容变化时重建子元素。</summary>
    public void UpdateItems(IReadOnlyList<OcrItem> items, IReadOnlyList<IReadOnlyList<double>> zones)
    {
        if (!IsVisible) return;
        RootCanvas.Children.Clear();

        // 自定义 ROI 区域高亮（半透明红色）
        foreach (IReadOnlyList<double> zone in zones)
        {
            if (zone.Count != 4) continue;
            var rect = new Rectangle
            {
                Fill = _zoneFill,
                Stroke = _zoneBorder,
                StrokeThickness = 2,
                Width = Width * zone[2],
                Height = Height * zone[3],
            };
            Canvas.SetLeft(rect, Width * zone[0]);
            Canvas.SetTop(rect, Height * zone[1]);
            RootCanvas.Children.Add(rect);
        }

        // 译文文本（无译文时显示原文；box[0] 为文本左上角）
        foreach (OcrItem item in items)
        {
            string text = string.IsNullOrEmpty(item.Translated) ? item.Original : item.Translated;
            if (item.Box.Count < 4) continue;

            System.Drawing.Point p = item.Box[0];
            var border = new Border
            {
                Background = _bgBrush,
                BorderBrush = _borderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(4, 2, 4, 2),
                MaxWidth = _style.MaxWidth,
                Child = new TextBlock
                {
                    Text = text,
                    Foreground = _fontBrush,
                    FontFamily = _fontFamily,
                    FontSize = _style.FontSize,
                    TextWrapping = TextWrapping.Wrap,
                },
            };
            Canvas.SetLeft(border, p.X);
            Canvas.SetTop(border, p.Y);
            RootCanvas.Children.Add(border);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _followTimer?.Stop();
        base.OnClosed(e);
    }

    /// <summary>100ms 跟随目标窗口客户区位置；目标失效则隐藏。</summary>
    private void FollowTarget()
    {
        if (!_capture.IsValid())
        {
            if (IsVisible) Hide();
            return;
        }
        var rect = _capture.GetClientRect();
        if (rect is not (int left, int top, int width, int height) || width <= 0 || height <= 0)
        {
            return;
        }
        if (Left != left || Top != top || Width != width || Height != height)
        {
            Left = left;
            Top = top;
            Width = width;
            Height = height;
        }
        if (!IsVisible) Show();
    }

    /// <summary>窗口源初始化后设置 WS_EX_TRANSPARENT，实现点击穿透。</summary>
    private void EnableClickThrough()
    {
        SourceInitialized += (_, _) =>
        {
            nint hwnd = new WindowInteropHelper(this).Handle;
            nint exStyle = GetWindowLongPtr(hwnd, GwlExStyle);
            SetWindowLongPtr(hwnd, GwlExStyle, exStyle | WsExTransparent | WsExToolWindow);
            // 全屏模式：把自己排除出屏幕截图，避免 OCR 抓到自身译文形成反馈环
            if (_excludeFromCapture)
            {
                SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE);
            }
        };
    }

    /// <summary>解析 #AARRGGBB / #RRGGBB 颜色，失败回退默认值。</summary>
    private static Brush ParseColor(string text, Color fallback)
    {
        try
        {
            var color = (Color)ColorConverter.ConvertFromString(text);
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
        catch
        {
            var brush = new SolidColorBrush(fallback);
            brush.Freeze();
            return brush;
        }
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
    private static extern nint GetWindowLongPtr(nint hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
    private static extern nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(nint hwnd, uint affinity);

    private const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;
}
