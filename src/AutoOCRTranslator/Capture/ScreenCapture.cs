using System.Drawing;
using System.Runtime.InteropServices;

namespace AutoOCRTranslator.Capture;

/// <summary>
/// 截图源抽象：WindowCapture（目标窗口）与 ScreenCapture（全屏）的共同接口。
/// CaptureLoop 按 CaptureMode 选择实现；zone 存储为相对 target rect 的归一化 [0,1]，
/// 与具体实现无关。
/// </summary>
public interface ICaptureSource
{
    void SetTarget(nint hwnd);
    bool IsValid();
    Bitmap? Capture();
}

/// <summary>
/// 全屏截图器：BitBlt 桌面 DC（GetDC(NULL)）按虚拟屏幕物理像素矩形截图，覆盖所有显示器。
/// 对应全屏模式（CaptureMode.FullScreen），target rect = 虚拟屏幕矩形（固定，不可移动）。
/// 与 OverlayWindow 全屏模式共用同一组 GetSystemMetrics 物理像素数值，保证 OCR 像素坐标
/// 与覆盖层画布坐标一致。
/// </summary>
public sealed class ScreenCapture : ICaptureSource
{
    private readonly int _left, _top, _width, _height;

    public ScreenCapture()
    {
        _left = GetSystemMetrics(SM_XVIRTUALSCREEN);
        _top = GetSystemMetrics(SM_YVIRTUALSCREEN);
        _width = GetSystemMetrics(SM_CXVIRTUALSCREEN);
        _height = GetSystemMetrics(SM_CYVIRTUALSCREEN);
    }

    /// <summary>全屏模式无目标窗口，SetTarget 为空操作。</summary>
    public void SetTarget(nint hwnd) { }

    public bool IsValid() => _width > 0 && _height > 0;

    /// <summary>截取整个虚拟屏幕画面，失败返回 null。</summary>
    public Bitmap? Capture()
    {
        if (!IsValid()) return null;
        try
        {
            nint screenDc = GetDC(nint.Zero);
            nint memDc = CreateCompatibleDC(screenDc);
            nint hBitmap = CreateCompatibleBitmap(screenDc, _width, _height);
            try
            {
                nint oldBitmap = SelectObject(memDc, hBitmap);
                BitBlt(memDc, 0, 0, _width, _height, screenDc, _left, _top, SRCCOPY);
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
        catch (Exception e)
        {
            Serilog.Log.Warning("全屏截图失败: {Error}", e.Message);
            return null;
        }
    }

    private const uint SRCCOPY = 0x00CC0020;
    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    private static extern nint GetDC(nint hWnd);

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
