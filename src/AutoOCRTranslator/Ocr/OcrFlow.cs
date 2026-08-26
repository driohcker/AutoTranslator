using System.Drawing;
using AutoOCRTranslator.Models;

namespace AutoOCRTranslator.Ocr;

/// <summary>
/// OCR 识别流程（对应 Python 版 ocr_task.run_ocr_flow）：
/// 识别 → 语言过滤 → 缓存查询 → 坐标还原。只含本地快步骤，不联网。
/// </summary>
public static class OcrFlow
{
    /// <summary>
    /// 同步执行 OCR + 缓存查询。
    /// </summary>
    /// <param name="image">待识别图像（已缩放）。</param>
    /// <param name="scaleRatio">图像相对全窗的缩放比例，用于坐标还原。</param>
    /// <param name="engine">OCR 引擎。</param>
    /// <param name="offset">图像在全窗中的偏移 (x, y)，用于 ROI 裁剪场景。</param>
    /// <param name="sourceLang">源语言。</param>
    /// <param name="targetLang">目标语言。</param>
    /// <param name="filterSourceLang">是否过滤非源语言文本。</param>
    /// <param name="strictSourceLang">是否启用严格源语言过滤。</param>
    /// <param name="cacheGet">缓存查询委托（文本 → 译文），未命中返回 null。</param>
    /// <returns>OcrItem 列表，Translated 为缓存命中时的译文，未命中为 null。</returns>
    public static List<OcrItem> RunOcrFlow(
        Bitmap image,
        double scaleRatio,
        IOcrEngine engine,
        (int X, int Y) offset,
        string sourceLang,
        string targetLang,
        bool filterSourceLang,
        bool strictSourceLang,
        Func<string, string?>? cacheGet = null)
    {
        IReadOnlyList<OcrLine> results = engine.Recognize(image);
        if (results.Count == 0) return [];

        var candidates = new List<OcrItem>(results.Count);
        foreach (OcrLine line in results)
        {
            // 过滤非目标语言文本（如 URL、英文按钮）
            if (filterSourceLang && !LanguageFilter.ShouldTranslate(line.Text, sourceLang, strict: strictSourceLang))
            {
                continue;
            }

            string? translated = cacheGet?.Invoke(line.Text);
            candidates.Add(new OcrItem
            {
                Original = line.Text,
                Translated = translated,
                Box = line.BoxPoints,
                Score = line.Score,
            });
        }

        if (candidates.Count == 0) return [];

        // 坐标还原：ROI 缩放 + 偏移映射回全窗坐标
        var items = new List<OcrItem>(candidates.Count);
        foreach (OcrItem candidate in candidates)
        {
            IReadOnlyList<Point> box = candidate.Box;
            if (scaleRatio < 1.0 || offset != (0, 0))
            {
                box = candidate.Box
                    .Select(p => new Point((int)(p.X / scaleRatio) + offset.X, (int)(p.Y / scaleRatio) + offset.Y))
                    .ToList();
            }
            items.Add(candidate with { Box = box });
        }
        return items;
    }
}
