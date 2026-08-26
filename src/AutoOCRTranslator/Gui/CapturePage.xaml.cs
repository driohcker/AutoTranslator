using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AutoOCRTranslator.Models;

namespace AutoOCRTranslator.Gui;

/// <summary>
/// 识别页：画面预览与识别结果对照（纯展示）。
/// 截图 / OCR / 翻译 / 覆盖层由 CaptureSession 统一驱动，本页仅订阅预览与结果事件。
/// </summary>
public partial class CapturePage : Page
{
    private CaptureSession? _session;

    public CapturePage()
    {
        InitializeComponent();
    }

    /// <summary>由主窗口注入会话；订阅预览帧与结果列表事件。</summary>
    public void Initialize(CaptureSession session)
    {
        _session = session;
        session.FramePreviewed += OnFramePreviewed;
        session.ItemsUpdated += OnItemsUpdated;
    }

    private void OnFramePreviewed(BitmapSource preview)
    {
        PreviewImage.Source = preview;
        PreviewPlaceholder.Visibility = Visibility.Collapsed;
    }

    private void OnItemsUpdated(IReadOnlyList<OcrItem> items)
    {
        OcrResultList.ItemsSource = items
            .Select(i => new OcrDisplayItem(i.Original, i.Translated))
            .ToList();
    }
}
