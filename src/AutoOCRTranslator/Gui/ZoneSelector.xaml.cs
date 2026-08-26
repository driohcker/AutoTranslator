using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using AutoOCRTranslator.Settings;
using Serilog;
using CaptureMode = AutoOCRTranslator.Settings.CaptureMode;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace AutoOCRTranslator.Gui;

/// <summary>
/// 全屏蒙版区域划分器：软件最小化后显示覆盖整个虚拟屏幕的半透明蒙版，
/// 用户在蒙版上拖拽框选一个或多个矩形区域（底层画面动态可见，无需截图）。
/// 坐标转换：画布坐标（虚拟屏幕 DIPs）→ 相对目标窗口的归一化 [x, y, w, h]（0~1），
/// 超出目标窗口范围的部分自动裁剪。
/// Enter 完成 / Esc 取消 / Ctrl+Z 撤销 / Delete 清空。
/// </summary>
public sealed partial class ZoneSelector : Window
{
    /// <summary>用户点击完成时返回区域列表；取消或 Esc 返回 null。</summary>
    public List<double[]>? Result { get; private set; }

    private readonly List<double[]> _zones;
    private readonly Rect _targetDip;   // 目标窗口在屏幕上的矩形（DIPs）
    private readonly double _vsLeft, _vsTop; // 虚拟屏幕左上角（DIPs）
    private bool _dragging;
    private System.Windows.Point _dragStart, _dragCurrent;
    private double _shotWidth, _shotHeight;

    private static readonly Brush ZoneFill = new SolidColorBrush(Color.FromArgb(40, 0, 255, 0));
    private static readonly Brush ZoneStroke = new SolidColorBrush(Color.FromArgb(160, 0, 255, 0));
    private static readonly Brush DragFill = new SolidColorBrush(Color.FromArgb(50, 255, 0, 0));
    private static readonly Brush DragStroke = new SolidColorBrush(Color.FromArgb(200, 255, 0, 0));
    // 目标窗口边框：蓝色虚线，让用户看到"合法拖拽区域"在哪
    private static readonly Brush TargetFill = new SolidColorBrush(Color.FromArgb(15, 80, 180, 255));
    private static readonly Brush TargetStroke = new SolidColorBrush(Color.FromArgb(180, 80, 180, 255));

    public ZoneSelector(List<double[]> existingZones, nint targetHwnd, CaptureMode mode)
    {
        InitializeComponent();
        ZoneFill.Freeze();
        ZoneStroke.Freeze();
        DragFill.Freeze();
        DragStroke.Freeze();
        TargetFill.Freeze();
        TargetStroke.Freeze();

        _vsLeft = SystemParameters.VirtualScreenLeft;
        _vsTop = SystemParameters.VirtualScreenTop;
        _shotWidth = SystemParameters.VirtualScreenWidth;
        _shotHeight = SystemParameters.VirtualScreenHeight;

        // 目标 rect：全屏模式 = 整个虚拟屏幕（蓝色边框框住整屏，意为"OCR 范围"）；
        // 目标窗口模式 = 目标窗口屏幕矩形（Win32 物理像素 → DIPs）。zone 始终是相对
        // target rect 的归一化 [0,1]，下游 CanvasToZone/DrawZoneRect 无需改动。
        _targetDip = mode == CaptureMode.FullScreen
            ? new Rect(_vsLeft, _vsTop, _shotWidth, _shotHeight)
            : WindowRectOf(targetHwnd);

        // 窗口覆盖整个虚拟屏幕
        Left = _vsLeft;
        Top = _vsTop;
        Width = _shotWidth;
        Height = _shotHeight;
        DrawCanvas.Width = _shotWidth;
        DrawCanvas.Height = _shotHeight;

        // 提示与按钮锚定到主屏工作区：相对主屏右下/居中排布，避免多显示器时
        // 落在副屏或屏间中点（HorizontalAlignment=Center/Right 默认相对整个虚拟屏幕）
        var workArea = SystemParameters.WorkArea;
        PrimaryAnchor.Width = workArea.Width;
        PrimaryAnchor.Height = workArea.Height;
        PrimaryAnchor.Margin = new Thickness(workArea.X - _vsLeft, workArea.Y - _vsTop, 0, 0);

        _zones = [.. existingZones];
        Redraw();   // 统一走 Redraw：包含目标窗口边框 + 已存区域，避免重复绘制逻辑
    }

    /// <summary>模态显示选择器。</summary>
    public List<double[]>? Select()
    {
        ShowDialog();
        return Result;
    }

    private void Canvas_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragging = true;
        _dragStart = e.GetPosition(DrawCanvas);
        _dragCurrent = _dragStart;
    }

    private void Canvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        _dragCurrent = e.GetPosition(DrawCanvas);
        Redraw();
    }

    private void Canvas_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        double[]? zone = CanvasToZone(_dragStart, _dragCurrent);
        if (zone is not null)
        {
            _zones.Add(zone);
            Log.Information("新增区域: [{X:F3}, {Y:F3}, {W:F3}, {H:F3}]", zone[0], zone[1], zone[2], zone[3]);
        }
        Redraw();
    }

    private void DoneButton_Click(object sender, RoutedEventArgs e) => Finish();

    private void UndoButton_Click(object sender, RoutedEventArgs e) => UndoLast();

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        _zones.Clear();
        Redraw();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        Result = null;
        Close();
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Result = null; Close(); }
        else if (e.Key is Key.Enter or Key.Return) Finish();
        else if (e.Key == Key.Z && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) UndoLast();
        else if (e.Key == Key.Delete) { _zones.Clear(); Redraw(); }
    }

    private void Finish()
    {
        Result = [.. _zones];
        Log.Information("区域选择完成，共 {Count} 个区域", Result.Count);
        Close();
    }

    private void UndoLast()
    {
        if (_zones.Count > 0)
        {
            _zones.RemoveAt(_zones.Count - 1);
            Redraw();
        }
    }

    /// <summary>全量重绘：目标窗口边框（蓝虚线）+ 已保存区域（绿）+ 拖拽中矩形（红/虚线）。</summary>
    private void Redraw()
    {
        DrawCanvas.Children.Clear();
        DrawTargetWindowOutline();
        foreach (double[] zone in _zones)
        {
            DrawZoneRect(zone, ZoneFill, ZoneStroke);
        }
        if (_dragging)
        {
            var rect = new System.Windows.Rect(_dragStart, _dragCurrent);
            rect.Intersect(new System.Windows.Rect(0, 0, _shotWidth, _shotHeight));
            if (rect.Width > 0 && rect.Height > 0)
            {
                // 转屏幕坐标检查是否与目标窗口相交：不相交时拖拽预览变虚线，提示"松开后不会保存"
                var screenDrag = new Rect(rect.X + _vsLeft, rect.Y + _vsTop, rect.Width, rect.Height);
                bool intersects = screenDrag.IntersectsWith(_targetDip);
                var shape = MakeRect(rect, DragFill, DragStroke);
                if (!intersects)
                {
                    shape.StrokeDashArray = new DoubleCollection { 4, 4 };
                }
                DrawCanvas.Children.Add(shape);
            }
        }
    }

    /// <summary>画目标窗口边框（蓝色虚线）：让用户在多屏蒙版上能看到"合法拖拽区域"在哪。</summary>
    private void DrawTargetWindowOutline()
    {
        var rect = new System.Windows.Rect(
            _targetDip.Left - _vsLeft,
            _targetDip.Top - _vsTop,
            _targetDip.Width,
            _targetDip.Height);
        var shape = MakeRect(rect, TargetFill, TargetStroke);
        shape.StrokeDashArray = new DoubleCollection { 6, 4 };
        DrawCanvas.Children.Add(shape);
    }

    private void DrawZoneRect(double[] zone, Brush fill, Brush stroke)
    {
        var rect = new System.Windows.Rect(
            _targetDip.Left - _vsLeft + zone[0] * _targetDip.Width,
            _targetDip.Top - _vsTop + zone[1] * _targetDip.Height,
            zone[2] * _targetDip.Width,
            zone[3] * _targetDip.Height);
        DrawCanvas.Children.Add(MakeRect(rect, fill, stroke));
    }

    private static Rectangle MakeRect(System.Windows.Rect rect, Brush fill, Brush stroke)
    {
        var shape = new Rectangle
        {
            Fill = fill,
            Stroke = stroke,
            StrokeThickness = 2,
            Width = rect.Width,
            Height = rect.Height,
            // 不参与命中测试：松开鼠标时事件直接落到 Canvas，避免拖拽预览矩形拦截 MouseUp
            IsHitTestVisible = false,
        };
        // Canvas 子元素位置由 Canvas.Left/Top 附加属性决定，缺省为 0 会导致矩形全部堆在左上角
        Canvas.SetLeft(shape, rect.X);
        Canvas.SetTop(shape, rect.Y);
        return shape;
    }

    /// <summary>
    /// 将拖拽矩形（画布坐标 = 虚拟屏幕 DIPs）转换为相对目标窗口的归一化坐标；
    /// 与目标窗口不相交的区域返回 null。
    /// </summary>
    private double[]? CanvasToZone(System.Windows.Point p1, System.Windows.Point p2)
    {
        // 画布坐标 → 屏幕坐标（DIPs）
        double sl = Math.Min(p1.X, p2.X) + _vsLeft;
        double st = Math.Min(p1.Y, p2.Y) + _vsTop;
        double sr = Math.Max(p1.X, p2.X) + _vsLeft;
        double sb = Math.Max(p1.Y, p2.Y) + _vsTop;

        // 裁剪到目标窗口范围
        double wl = _targetDip.Left, wt = _targetDip.Top;
        double wr = _targetDip.Right, wb = _targetDip.Bottom;
        double il = Math.Max(sl, wl), it = Math.Max(st, wt);
        double ir = Math.Min(sr, wr), ib = Math.Min(sb, wb);
        if (ir <= il || ib <= it) return null;

        double x = (il - wl) / _targetDip.Width;
        double y = (it - wt) / _targetDip.Height;
        double w = (ir - il) / _targetDip.Width;
        double h = (ib - it) / _targetDip.Height;
        return [Math.Clamp(x, 0, 1), Math.Clamp(y, 0, 1), Math.Clamp(w, 0, 1), Math.Clamp(h, 0, 1)];
    }

    /// <summary>目标窗口的屏幕矩形（Win32 物理像素 → DIPs）。窗口失效时抛异常由调用方提示。</summary>
    private Rect WindowRectOf(nint hwnd)
    {
        if (!GetWindowRect(hwnd, out var rect) || rect.Right <= rect.Left || rect.Bottom <= rect.Top)
        {
            throw new InvalidOperationException("目标窗口已失效，请重新选择窗口后再划分区域。");
        }

        // 当前进程的 DPI 缩放（WPF 窗口坐标是 DIPs，Win32 矩形是物理像素）
        double scale = 1.0;
        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget is { } ct)
        {
            scale = ct.TransformToDevice.M11;
        }
        return new Rect(rect.Left / scale, rect.Top / scale,
            (rect.Right - rect.Left) / scale, (rect.Bottom - rect.Top) / scale);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint hWnd, out RECT lpRect);
}
