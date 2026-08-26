using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using AutoOCRTranslator.Utils;
using RapidOcrNet;
using SkiaSharp;

namespace AutoOCRTranslator.Ocr;

/// <summary>
/// RapidOcrNet（PP-OCR ONNX）引擎封装。
/// 检测/方向分类用包内 v5 模型，识别用 v4 japan 模型（对应 Python 版 rapidocr 日语配置）。
/// 模型懒加载：首次 Recognize 时才初始化（约 1-2s，不阻塞启动）。
/// </summary>
public sealed class RapidOcrEngine : IOcrEngine
{
    private readonly RapidOcr _ocr = new();
    private readonly RapidOcrOptions _options;
    private bool _initialized;

    public RapidOcrEngine(double dropScore)
    {
        // PythonCompat 与 Python rapidocr 预处理一致（无边距、短边自适应 736）
        _options = RapidOcrOptions.PythonCompat with { TextScore = (float)dropScore };
    }

    public IReadOnlyList<OcrLine> Recognize(Bitmap image)
    {
        if (!_initialized)
        {
            InitModels();
        }

        using var skBitmap = ToSkBitmap(image);
        OcrResult result = _ocr.Detect(skBitmap, _options);

        var textBlocks = result.TextBlocks;
        var lines = new List<OcrLine>(textBlocks.Count());
        foreach (var block in textBlocks)
        {
            double score = block.CharScores is { Length: > 0 } ? block.CharScores.Average() : 0;
            var points = block.BoxPoints
                .Select(p => new Point((int)MathF.Round(p.X), (int)MathF.Round(p.Y)))
                .ToList();
            lines.Add(new OcrLine(block.Text, score, points));
        }
        return lines;
    }

    private void InitModels()
    {
        string modelsDir = Path.Combine(AppDirs.BaseDir, "models");
        _ocr.InitModels(
            Path.Combine(modelsDir, "v5", "ch_PP-OCRv5_mobile_det.onnx"),
            Path.Combine(modelsDir, "v5", "ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx"),
            Path.Combine(modelsDir, "japan_v4", "japan_PP-OCRv4_rec_mobile.onnx"),
            Path.Combine(modelsDir, "japan_v4", "japan_dict.txt"));
        _initialized = true;
        Serilog.Log.Information("OCR 模型加载完成（det=v5, cls=v5, rec=japan_v4）");
    }

    /// <summary>System.Drawing.Bitmap → SKBitmap（BGRA 逐行拷贝，避免 PNG 编码往返）。</summary>
    private static SKBitmap ToSkBitmap(Bitmap bitmap)
    {
        var sk = new SKBitmap(bitmap.Width, bitmap.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        BitmapData data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height),
            ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int copyBytes = Math.Min(data.Stride, sk.RowBytes);
            byte[] buffer = new byte[copyBytes];
            for (int row = 0; row < bitmap.Height; row++)
            {
                nint src = data.Scan0 + row * data.Stride;
                nint dst = sk.GetPixels() + row * sk.RowBytes;
                Marshal.Copy(src, buffer, 0, copyBytes);
                Marshal.Copy(buffer, 0, dst, copyBytes);
            }
            return sk;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }
}
