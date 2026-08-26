using System.Drawing;
using System.Drawing.Imaging;
using System.Numerics;
using System.Runtime.InteropServices;

namespace AutoOCRTranslator.Capture;

/// <summary>
/// 基于 dHash 的区域级变化检测器（对应 Python 版 change_detector.py）。
/// 对每个已缩放 ROI 图计算 64 位感知哈希，任一区域与上次差异超过阈值
/// 即判定画面变化。单区域耗时 &lt;1ms，是「便宜优先」管道的第一个闸门。
/// </summary>
public sealed class ChangeDetector
{
    private readonly int _threshold;
    private Dictionary<int, ulong> _hashes = [];

    public ChangeDetector(int threshold = 4)
    {
        _threshold = Math.Max(1, threshold);
    }

    /// <summary>检测 images（与区域索引一一对应）相对上次是否有变化。</summary>
    public bool Check(IReadOnlyList<Bitmap> images)
    {
        bool changed = false;
        var newHashes = new Dictionary<int, ulong>(images.Count);
        for (int idx = 0; idx < images.Count; idx++)
        {
            Bitmap? image = images[idx];
            if (image is null) continue;

            ulong h = DHash(image);
            newHashes[idx] = h;
            if (!_hashes.TryGetValue(idx, out ulong prev)
                || BitOperations.PopCount(prev ^ h) > _threshold)
            {
                changed = true;
            }
        }

        // 无论是否有变化都更新哈希，保证下一帧以最新画面为基准
        _hashes = newHashes;
        return changed;
    }

    /// <summary>清空历史哈希（窗口尺寸/区域配置变化后调用）。</summary>
    public void Reset() => _hashes.Clear();

    /// <summary>计算 9x9 灰度图的 8x8 差分哈希（64 位）。</summary>
    private static ulong DHash(Bitmap image)
    {
        using var small = new Bitmap(9, 9, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(small))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;
            g.DrawImage(image, 0, 0, 9, 9);
        }

        var rect = new Rectangle(0, 0, 9, 9);
        BitmapData data = small.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            ulong h = 0;
            for (int row = 0; row < 8; row++)
            {
                for (int col = 0; col < 8; col++)
                {
                    byte left = GrayAt(data, row, col);
                    byte right = GrayAt(data, row, col + 1);
                    h = (h << 1) | (left < right ? 1UL : 0UL);
                }
            }
            return h;
        }
        finally
        {
            small.UnlockBits(data);
        }
    }

    /// <summary>读取 24bpp 像素并转灰度（BT.601 加权）。</summary>
    private static byte GrayAt(BitmapData data, int row, int col)
    {
        nint offset = data.Scan0 + row * data.Stride + col * 3;
        byte b = Marshal.ReadByte(offset);
        byte g = Marshal.ReadByte(offset, 1);
        byte r = Marshal.ReadByte(offset, 2);
        return (byte)((r * 77 + g * 150 + b * 29) >> 8);
    }
}
