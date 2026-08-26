using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace AutoOCRTranslator.Utils;

/// <summary>System.Drawing.Bitmap → WPF BitmapSource 转换（冻结拷贝，安全释放 HBITMAP）。</summary>
public static class ImageConvert
{
    public static BitmapSource ToBitmapSource(Bitmap bitmap)
    {
        nint hBitmap = bitmap.GetHbitmap();
        try
        {
            var source = Imaging.CreateBitmapSourceFromHBitmap(
                hBitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze(); // 强制立即拷贝像素，之后可安全删除 HBITMAP
            return source;
        }
        finally
        {
            DeleteObject(hBitmap);
        }
    }

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(nint hObject);
}
