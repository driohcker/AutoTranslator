using System.Runtime.InteropServices;
using System.Text;

namespace AutoOCRTranslator.Utils;

/// <summary>目标窗口信息。</summary>
public sealed record WindowInfo(nint Hwnd, string Title);

/// <summary>枚举系统可见顶层窗口（对应 Python 版"选择窗口"功能）。</summary>
public static class WindowEnumerator
{
    /// <summary>返回所有带标题的可见顶层窗口。</summary>
    public static IReadOnlyList<WindowInfo> GetVisibleWindows()
    {
        var results = new List<WindowInfo>();
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;
            if (GetWindow(hwnd, GW_OWNER) != nint.Zero) return true; // 跳过 owned 子窗口（对话框等）
            int len = GetWindowTextLength(hwnd);
            if (len == 0) return true;
            var sb = new StringBuilder(len + 1);
            GetWindowText(hwnd, sb, sb.Capacity);
            results.Add(new WindowInfo(hwnd, sb.ToString()));
            return true;
        }, nint.Zero);
        return results;
    }

    private const uint GW_OWNER = 4;

    private delegate bool EnumWindowsProc(nint hwnd, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint hWnd);

    [DllImport("user32.dll")]
    private static extern nint GetWindow(nint hWnd, uint uCmd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(nint hWnd);
}
