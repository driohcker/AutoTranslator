using System.Drawing;
using System.Drawing.Drawing2D;

namespace AutoOCRTranslator.Utils;

/// <summary>
/// ROI 预处理工具（对应 Python 版 image_roi.py）：
/// 把截图按配置裁剪、缩放到适合 OCR 的尺寸。
/// </summary>
public static class ImageRoi
{
    /// <summary>OCR 区域预设：key → [x_ratio, y_ratio, w_ratio, h_ratio]（相对坐标 0~1）。</summary>
    public static readonly IReadOnlyDictionary<string, double[]> RoiPresets =
        new Dictionary<string, double[]>
        {
            ["subtitle"] = [0.1, 0.75, 0.8, 0.2],  // 底部居中字幕区
            ["bottom"] = [0.0, 0.7, 1.0, 0.3],     // 底部全宽
            ["full"] = [0.0, 0.0, 1.0, 1.0],       // 全屏
        };

    /// <summary>不同 ROI 预设对应的 OCR 参数：面积越大，分辨率/阈值越宽松。</summary>
    public sealed record OcrParams(
        int MaxWidth,
        int DetLimitSideLen,
        double DropScore,
        double DetDbThresh,
        double DetDbBoxThresh,
        int MinHeight);

    public static readonly IReadOnlyDictionary<string, OcrParams> RoiOcrParams =
        new Dictionary<string, OcrParams>
        {
            ["subtitle"] = new(480, 480, 0.6, 0.5, 0.5, 20),
            ["bottom"] = new(640, 640, 0.5, 0.4, 0.4, 20),
            ["full"] = new(1280, 1280, 0.45, 0.3, 0.3, 15),
            ["custom_zones"] = new(1280, 1280, 0.45, 0.3, 0.3, 15),
        };

    /// <summary>根据预设名称获取 ROI（custom 预设使用自定义值）。</summary>
    public static double[] GetRoiFromPreset(string preset, double[]? customRoi = null)
    {
        if (preset == "custom" && customRoi is { Length: 4 })
        {
            return customRoi;
        }
        return RoiPresets.GetValueOrDefault(preset, RoiPresets["subtitle"]);
    }

    /// <summary>按相对坐标裁剪 ROI，返回 (裁剪图, 在原图中的偏移)。</summary>
    public static (Bitmap Image, (int X, int Y) Offset) CropRoi(Bitmap image, double[] roiRatios)
    {
        if (roiRatios.Length != 4)
        {
            return (new Bitmap(image), (0, 0));
        }

        int left = (int)(image.Width * roiRatios[0]);
        int top = (int)(image.Height * roiRatios[1]);
        int right = Math.Min(left + (int)(image.Width * roiRatios[2]), image.Width);
        int bottom = Math.Min(top + (int)(image.Height * roiRatios[3]), image.Height);

        if (right <= left || bottom <= top)
        {
            return (new Bitmap(image), (0, 0));
        }

        return (image.Clone(new Rectangle(left, top, right - left, bottom - top), image.PixelFormat), (left, top));
    }

    /// <summary>宽度超过限制时等比缩放以加速 OCR，返回 (缩放图, 缩放比例)。</summary>
    public static (Bitmap Image, double ScaleRatio) ResizeForOcr(Bitmap image, int maxWidth)
    {
        if (image.Width <= maxWidth)
        {
            return (image, 1.0);
        }

        double ratio = (double)maxWidth / image.Width;
        var resized = new Bitmap(maxWidth, (int)(image.Height * ratio), image.PixelFormat);
        using var g = Graphics.FromImage(resized);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic; // 对应 LANCZOS
        g.DrawImage(image, 0, 0, maxWidth, resized.Height);
        return (resized, ratio);
    }

    /// <summary>准备单区域 OCR 输入：(缩放图, 偏移, 缩放比例)。</summary>
    public static (Bitmap Image, (int X, int Y) Offset, double ScaleRatio) PrepareSingleZone(
        Bitmap image, string preset, double[]? customRoi = null, int? maxWidth = null)
    {
        double[] roi = GetRoiFromPreset(preset, customRoi);
        var (cropped, offset) = CropRoi(image, roi);

        int mw = maxWidth ?? RoiOcrParams.GetValueOrDefault(preset, RoiOcrParams["subtitle"]).MaxWidth;
        var (resized, scaleRatio) = ResizeForOcr(cropped, mw);
        return (resized, offset, scaleRatio);
    }

    /// <summary>准备多区域 OCR 输入，跳过无效区域。</summary>
    public static List<(Bitmap Image, (int X, int Y) Offset, double ScaleRatio)> PrepareMultiZones(
        Bitmap image, IReadOnlyList<double[]> zones, int maxWidth)
    {
        var prepared = new List<(Bitmap, (int, int), double)>();
        foreach (double[] zone in zones)
        {
            if (zone.Length != 4) continue;
            var (cropped, offset) = CropRoi(image, zone);
            if (cropped.Width <= 0 || cropped.Height <= 0)
            {
                cropped.Dispose();
                continue;
            }
            var (resized, scaleRatio) = ResizeForOcr(cropped, maxWidth);
            prepared.Add((resized, offset, scaleRatio));
        }
        return prepared;
    }
}
