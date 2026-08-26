using System.Drawing;

namespace AutoOCRTranslator.Ocr;

/// <summary>OCR 引擎识别出的一行文本（坐标在输入图像像素空间内）。</summary>
public sealed record OcrLine(string Text, double Score, IReadOnlyList<Point> BoxPoints);

/// <summary>OCR 引擎抽象（本地识别，不联网）。</summary>
public interface IOcrEngine
{
    /// <summary>识别单张图像，返回检测到的文本行列表。</summary>
    IReadOnlyList<OcrLine> Recognize(Bitmap image);
}
