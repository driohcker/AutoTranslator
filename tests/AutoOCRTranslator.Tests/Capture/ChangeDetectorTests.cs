using System.Drawing;
using AutoOCRTranslator.Capture;
using Xunit;

namespace AutoOCRTranslator.Tests.Capture;

/// <summary>dHash 变化检测：相同画面不触发、内容变化触发、Reset 后首帧触发。</summary>
public class ChangeDetectorTests
{
    /// <summary>
    /// 全白图，或左半黑右半白的强对比图（dHash 差异 8 bits &gt; 阈值 4）。
    /// 注意：纯色图 dHash 恒为 0，必须用带边界的图案才可区分。
    /// </summary>
    private static Bitmap Pattern(bool leftHalfBlack)
    {
        var bmp = new Bitmap(100, 50);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.White);
        if (leftHalfBlack) g.FillRectangle(Brushes.Black, 0, 0, 50, 50);
        return bmp;
    }

    [Fact]
    public void SameImage_SecondCheck_ReturnsFalse()
    {
        using var image = Pattern(leftHalfBlack: true);
        var detector = new ChangeDetector();

        // 首帧总是「变化」（无历史基准）
        Assert.True(detector.Check([image]));
        Assert.False(detector.Check([image]));
    }

    [Fact]
    public void ContentChanged_ReturnsTrue()
    {
        using var plain = Pattern(leftHalfBlack: false);
        using var striped = Pattern(leftHalfBlack: true);
        var detector = new ChangeDetector();

        detector.Check([plain]);
        Assert.True(detector.Check([striped]));
    }

    [Fact]
    public void PartialChange_InOneZone_ReturnsTrue()
    {
        using var plain = Pattern(leftHalfBlack: false);
        using var striped = Pattern(leftHalfBlack: true);
        var detector = new ChangeDetector();
        detector.Check([plain, plain]);

        // 第二个区域变化即触发；再次相同画面不再触发
        Assert.True(detector.Check([plain, striped]));
        Assert.False(detector.Check([plain, striped]));
    }

    [Fact]
    public void Reset_ForcesNextCheckTrue()
    {
        using var image = Pattern(leftHalfBlack: true);
        var detector = new ChangeDetector();
        detector.Check([image]);
        Assert.False(detector.Check([image]));

        detector.Reset();
        Assert.True(detector.Check([image]));
    }

    [Fact]
    public void NullZone_IsSkipped()
    {
        using var image = Pattern(leftHalfBlack: true);
        var detector = new ChangeDetector();
        detector.Check([image, null!]);
        Assert.False(detector.Check([image, null!]));
    }
}
