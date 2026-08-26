using System.Drawing;
using AutoOCRTranslator.Utils;
using Xunit;

namespace AutoOCRTranslator.Tests.Utils;

/// <summary>ROI 预处理：裁剪尺寸/偏移、越界 clamp、等比缩放、预设解析。</summary>
public class ImageRoiTests
{
    private static Bitmap NewImage(int w, int h) => new(w, h);

    [Fact]
    public void CropRoi_FullRoi_ReturnsWholeImage()
    {
        using var image = NewImage(100, 80);
        var (cropped, offset) = ImageRoi.CropRoi(image, [0, 0, 1, 1]);

        Assert.Equal(100, cropped.Width);
        Assert.Equal(80, cropped.Height);
        Assert.Equal((0, 0), offset);
    }

    [Fact]
    public void CropRoi_HalfCenter_CorrectSizeAndOffset()
    {
        using var image = NewImage(100, 80);
        var (cropped, offset) = ImageRoi.CropRoi(image, [0.25, 0.25, 0.5, 0.5]);

        Assert.Equal(50, cropped.Width);
        Assert.Equal(40, cropped.Height);
        Assert.Equal((25, 20), offset);
    }

    [Fact]
    public void CropRoi_OutOfBounds_IsClamped()
    {
        using var image = NewImage(100, 80);
        var (cropped, offset) = ImageRoi.CropRoi(image, [0.5, 0.5, 0.8, 0.8]);

        Assert.Equal(50, cropped.Width);   // right = min(50+80, 100) = 100
        Assert.Equal(40, cropped.Height);  // bottom = min(40+64, 80) = 80
        Assert.Equal((50, 40), offset);
    }

    [Fact]
    public void CropRoi_InvalidRatios_ReturnsWholeImage()
    {
        using var image = NewImage(100, 80);
        var (cropped, offset) = ImageRoi.CropRoi(image, [0, 0, 0, 0]); // 零面积 → 回退全图

        Assert.Equal(100, cropped.Width);
        Assert.Equal((0, 0), offset);
    }

    [Fact]
    public void ResizeForOcr_TooWide_ScalesDown()
    {
        using var image = NewImage(100, 50);
        var (resized, ratio) = ImageRoi.ResizeForOcr(image, 50);

        Assert.Equal(50, resized.Width);
        Assert.Equal(25, resized.Height);
        Assert.Equal(0.5, ratio, precision: 6);
    }

    [Fact]
    public void ResizeForOcr_NarrowEnough_KeepsOriginal()
    {
        using var image = NewImage(100, 50);
        var (resized, ratio) = ImageRoi.ResizeForOcr(image, 200);

        Assert.Same(image, resized); // 未缩放返回原引用
        Assert.Equal(1.0, ratio);
    }

    [Fact]
    public void GetRoiFromPreset_Custom_PrefersCustomValue()
    {
        double[] roi = ImageRoi.GetRoiFromPreset("custom", [0.1, 0.2, 0.3, 0.4]);
        Assert.Equal([0.1, 0.2, 0.3, 0.4], roi);
    }

    [Fact]
    public void GetRoiFromPreset_Unknown_FallsBackToSubtitle()
    {
        double[] roi = ImageRoi.GetRoiFromPreset("bogus");
        Assert.Equal(ImageRoi.RoiPresets["subtitle"], roi);
    }

    [Fact]
    public void PrepareSingleZone_AppliesCropAndScale()
    {
        using var image = NewImage(200, 100);
        var (prepared, offset, ratio) = ImageRoi.PrepareSingleZone(image, "bottom");

        // bottom = [0, 0.7, 1, 0.3] → 裁剪 200x30（top=70），MaxWidth 640 不缩放
        Assert.Equal(200, prepared.Width);
        Assert.Equal(30, prepared.Height);
        Assert.Equal((0, 70), offset);
        Assert.Equal(1.0, ratio);
    }

    [Fact]
    public void PrepareMultiZones_SkipsInvalidZones()
    {
        using var image = NewImage(100, 80);
        var prepared = ImageRoi.PrepareMultiZones(image, [[0, 0, 0.5, 0.5], [9], [0.5, 0.5, 0.5, 0.5]], 640);

        Assert.Equal(2, prepared.Count); // 长度不为 4 的区域被跳过
    }
}
