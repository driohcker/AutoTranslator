using System.Drawing;
using System.Runtime.InteropServices;

namespace AutoOCRTranslator.Capture;

/// <summary>
/// Windows 窗口截图器（对应 Python 版 window_capture.py）。
/// 优先 PrintWindow + PW_RENDERFULLCONTENT 抓取窗口自身内容——目标窗口被遮挡
/// （应用窗口压住字幕区）也能截到画面；失败或结果纯色时回退 BitBlt 屏幕拷贝。
/// </summary>
public sealed class WindowCapture : ICaptureSource
{
    private nint _hwnd;

    public void SetTarget(nint hwnd)
    {
        if (!IsWindow(hwnd))
        {
            throw new InvalidOperationException($"无效的窗口句柄: {hwnd}");
        }
        _hwnd = hwnd;
    }

    public bool IsValid() => _hwnd != nint.Zero && IsWindow(_hwnd);

    /// <summary>目标窗口客户区在屏幕上的位置与尺寸，窗口无效返回 null。</summary>
    public (int Left, int Top, int Width, int Height)? GetClientRect()
    {
        if (!IsValid()) return null;
        if (!GetClientRect(_hwnd, out RECT client)) return null;

        var ptLeft = new POINT { X = client.Left, Y = client.Top };
        ClientToScreen(_hwnd, ref ptLeft);
        var ptRight = new POINT { X = client.Right, Y = client.Bottom };
        ClientToScreen(_hwnd, ref ptRight);

        return (ptLeft.X, ptLeft.Y, ptRight.X - ptLeft.X, ptRight.Y - ptLeft.Y);
    }

    /// <summary>截取目标窗口客户区画面，失败返回 null。</summary>
    public Bitmap? Capture()
    {
        if (!IsValid()) return null;
        var rect = GetClientRect();
        if (rect is null || rect.Value.Width <= 0 || rect.Value.Height <= 0) return null;
        try
        {
            // PrintWindow 抓窗口自身内容：被遮挡/部分离屏也能截到
            Bitmap? printed = PrintWindowCapture(_hwnd, rect.Value.Width, rect.Value.Height);
            if (printed is not null && !IsSolidColor(printed)) return printed;
            printed?.Dispose();
            // 回退：BitBlt 屏幕拷贝（PrintWindow 不支持的窗口/最小化窗口）
            return BitBltCapture(rect.Value.Left, rect.Value.Top, rect.Value.Width, rect.Value.Height);
        }
        catch (Exception e)
        {
            Serilog.Log.Warning("截图失败: {Error}", e.Message);
            return null;
        }
    }

    /// <summary>PrintWindow 渲染到客户区大小的 DC；PW_RENDERFULLCONTENT 抓 DirectComposition 内容（Win8.1+）。</summary>
    private static Bitmap? PrintWindowCapture(nint hwnd, int width, int height)
    {
        nint hwndDc = GetWindowDC(hwnd);
        nint memDc = CreateCompatibleDC(hwndDc);
        nint hBitmap = CreateCompatibleBitmap(hwndDc, width, height);
        try
        {
            SelectObject(memDc, hBitmap);
            bool ok = PrintWindow(hwnd, memDc, PW_RENDERFULLCONTENT);
            if (!ok) return null;
            return Image.FromHbitmap(hBitmap);
        }
        catch
        {
            return null;
        }
        finally
        {
            DeleteObject(hBitmap);
            DeleteDC(memDc);
            ReleaseDC(hwnd, hwndDc);
        }
    }

    /// <summary>整幅图只有一种颜色（PrintWindow 黑屏/空白），视为失败需要回退。</summary>
    private static bool IsSolidColor(Bitmap bmp)
    {
        var first = bmp.GetPixel(0, 0);
        for (int y = 0; y < bmp.Height; y += Math.Max(1, bmp.Height / 8))
        {
            for (int x = 0; x < bmp.Width; x += Math.Max(1, bmp.Width / 8))
            {
                Color c = bmp.GetPixel(x, y);
                if (Math.Abs(c.R - first.R) > 12 || Math.Abs(c.G - first.G) > 12 || Math.Abs(c.B - first.B) > 12)
                {
                    return false;
                }
            }
        }
        return true;
    }

    private static Bitmap BitBltCapture(int left, int top, int width, int height)
    {
        nint screenDc = GetDC(nint.Zero);      // 屏幕 DC：能截到当前可见画面
        nint memDc = CreateCompatibleDC(screenDc);
        nint hBitmap = CreateCompatibleBitmap(screenDc, width, height);
        try
        {
            nint oldBitmap = SelectObject(memDc, hBitmap);
            BitBlt(memDc, 0, 0, width, height, screenDc, left, top, SRCCOPY);
            SelectObject(memDc, oldBitmap);
            // FromHbitmap 拷贝像素，后续删除 HBITMAP 不影响返回的 Bitmap
            return Image.FromHbitmap(hBitmap);
        }
        finally
        {
            DeleteObject(hBitmap);
            DeleteDC(memDc);
            ReleaseDC(nint.Zero, screenDc);
        }
    }

    private const uint SRCCOPY = 0x00CC0020;
    private const uint PW_RENDERFULLCONTENT = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X, Y;
    }

    [DllImport("user32.dll")]
    private static extern bool IsWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(nint hWnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(nint hWnd, ref POINT point);

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(nint hWnd, nint hDc, uint nFlags);

    [DllImport("user32.dll")]
    private static extern nint GetDC(nint hWnd);

    [DllImport("user32.dll")]
    private static extern nint GetWindowDC(nint hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint hWnd, nint hDc);

    [DllImport("gdi32.dll")]
    private static extern nint CreateCompatibleDC(nint hDc);

    [DllImport("gdi32.dll")]
    private static extern nint CreateCompatibleBitmap(nint hDc, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern nint SelectObject(nint hDc, nint hObject);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(nint hDestDc, int x, int y, int width, int height,
        nint hSrcDc, int srcX, int srcY, uint rop);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(nint hObject);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(nint hDc);
}
