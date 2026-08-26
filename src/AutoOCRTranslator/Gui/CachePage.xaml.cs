using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using AutoOCRTranslator.Cache;
using AutoOCRTranslator.Settings;
using AutoOCRTranslator.Translate;
using Serilog;
using Wpf.Ui.Controls;
using ControlAppearance = Wpf.Ui.Controls.ControlAppearance;
using ISnackbarService = Wpf.Ui.ISnackbarService;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;
using MessageBoxResult = System.Windows.MessageBoxResult;

namespace AutoOCRTranslator.Gui;

/// <summary>
/// 翻译缓存管理页：列举/搜索/删除/导出/导入缓存条目。
/// 导出 JSON 便于在玩家间共享已翻译内容；导入时默认跳过已存在的相同键。
/// </summary>
public sealed partial class CachePage : Page
{
    private TranslationCache? _cache;
    private AppSettings? _settings;
    private string _configPath = "";
    private ISnackbarService? _snackbar;
    private readonly ObservableCollection<CacheRow> _rows = [];
    private int _offset;
    private int _total;
    private bool _initialized;
    private bool _suppressProfileChange;
    private const int PageSize = 10;

    public CachePage()
    {
        InitializeComponent();
        EntriesGrid.ItemsSource = _rows;
    }

    public void Initialize(TranslationPipeline pipeline, AppSettings settings, string configPath, ISnackbarService snackbar)
    {
        _cache = pipeline.Cache;
        _settings = settings;
        _configPath = configPath;
        _snackbar = snackbar;
        _initialized = true;
        if (_cache is null)
        {
            DisabledOverlay.Visibility = Visibility.Visible;
            return;
        }
        // 同步活动方案：settings 里记录的方案若已被删则回落 default
        if (!_cache.SetActive(settings.Cache.ActiveProfile))
        {
            _cache.SetActive("default");
            settings.Cache.ActiveProfile = "default";
            PersistSettings();
        }
        LoadProfiles();
        LoadEntries();
    }

    /// <summary>把当前 _settings 写回 settings.yaml。</summary>
    private void PersistSettings()
    {
        if (_settings is null || string.IsNullOrEmpty(_configPath)) return;
        try { SettingsLoader.Save(_configPath, _settings); }
        catch (Exception ex) { Log.Error(ex, "保存缓存设置失败"); }
    }

    private void LoadEntries()
    {
        if (_cache is null) return;
        string src = SourceLangFilter.SelectedValue as string ?? "";
        string tgt = TargetLangFilter.SelectedValue as string ?? "";
        string search = SearchBox.Text?.Trim() ?? "";
        var (entries, total) = _cache.ListEntries(
            string.IsNullOrEmpty(src) ? null : src,
            string.IsNullOrEmpty(tgt) ? null : tgt,
            string.IsNullOrEmpty(search) ? null : search,
            _offset, PageSize);
        _rows.Clear();
        foreach (CacheEntry e in entries) _rows.Add(new CacheRow(e));
        _total = total;
        int pages = (_total + PageSize - 1) / PageSize;
        int page = _offset / PageSize + 1;
        PageInfo.Text = pages == 0 ? "0 / 0" : $"{page} / {pages}";
        PrevButton.IsEnabled = _offset > 0;
        NextButton.IsEnabled = _offset + PageSize < _total;

        var (count, _) = _cache.Stats();
        TotalCountValue.Text = count.ToString();
    }

    private void Notify(string title, string message, ControlAppearance appearance = ControlAppearance.Info)
        => _snackbar?.Show(title, message, appearance, null, TimeSpan.FromSeconds(2));

    // ---------- 方案管理 ----------

    /// <summary>填充方案下拉，并选中当前活动方案。</summary>
    private void LoadProfiles()
    {
        if (_cache is null) return;
        _suppressProfileChange = true;
        try
        {
            ProfileCombo.Items.Clear();
            foreach (ProfileInfo p in _cache.ListProfiles())
            {
                var item = new ComboBoxItem { Content = p.Name, Tag = p.Id };
                ProfileCombo.Items.Add(item);
                if (p.Id == _cache.ActiveProfile) ProfileCombo.SelectedItem = item;
            }
            RenameProfileButton.IsEnabled = _cache.ActiveProfile != "default";
            DeleteProfileButton.IsEnabled = _cache.ActiveProfile != "default";
        }
        finally { _suppressProfileChange = false; }
    }

    private void ProfileCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressProfileChange || !_initialized || _cache is null) return;
        string? id = ProfileCombo.SelectedValue as string;
        if (string.IsNullOrEmpty(id) || id == _cache.ActiveProfile) return;
        if (!_cache.SetActive(id)) { Notify("切换失败", "方案不存在"); LoadProfiles(); return; }
        _settings!.Cache.ActiveProfile = id;
        PersistSettings();
        _offset = 0;
        LoadProfiles();
        LoadEntries();
        Notify("已切换方案", $"当前方案：{(_cache.ActiveProfile)}", ControlAppearance.Success);
    }

    private void NewProfileButton_Click(object sender, RoutedEventArgs e)
    {
        if (_cache is null) return;
        var dlg = new InputDialog("新建方案", "请输入方案名称（用于区分不同游戏）：", "");
        if (dlg.ShowDialog() != true) return;
        string name = dlg.ResultText;
        if (string.IsNullOrWhiteSpace(name)) { Notify("未创建", "方案名称不能为空"); return; }
        ProfileInfo info = _cache.AddProfile(name);
        _cache.SetActive(info.Id);
        _settings!.Cache.ActiveProfile = info.Id;
        PersistSettings();
        _offset = 0;
        LoadProfiles();
        LoadEntries();
        Notify("已创建并切换", $"方案「{info.Name}」已创建", ControlAppearance.Success);
    }

    private void RenameProfileButton_Click(object sender, RoutedEventArgs e)
    {
        if (_cache is null) return;
        string id = _cache.ActiveProfile;
        if (id == "default") { Notify("不可重命名", "默认方案无法重命名"); return; }
        string oldName = (ProfileCombo.SelectedItem as ComboBoxItem)?.Content as string ?? "";
        var dlg = new InputDialog("重命名方案", "请输入新的方案名称：", oldName);
        if (dlg.ShowDialog() != true) return;
        string name = dlg.ResultText;
        if (string.IsNullOrWhiteSpace(name)) { Notify("未重命名", "方案名称不能为空"); return; }
        if (_cache.RenameProfile(id, name))
        {
            LoadProfiles();
            Notify("已重命名", $"方案已重命名为「{name}」", ControlAppearance.Success);
        }
    }

    private void DeleteProfileButton_Click(object sender, RoutedEventArgs e)
    {
        if (_cache is null) return;
        string id = _cache.ActiveProfile;
        if (id == "default") { Notify("不可删除", "默认方案无法删除"); return; }
        string name = (ProfileCombo.SelectedItem as ComboBoxItem)?.Content as string ?? id;
        if (MessageBox.Show($"确认删除方案「{name}」？\n该方案下的全部缓存条目将一并删除，此操作不可恢复。",
                "确认删除", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        if (!_cache.DeleteProfile(id)) { Notify("删除失败", "方案不存在或无法删除"); return; }
        // 删除的是当前活动方案（被 DeleteProfile 拦截，但安全起见回落 default）
        _cache.SetActive("default");
        _settings!.Cache.ActiveProfile = "default";
        PersistSettings();
        _offset = 0;
        LoadProfiles();
        LoadEntries();
        Notify("已删除", $"方案「{name}」已删除，已切换回默认方案", ControlAppearance.Success);
    }

    private void Filter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized) return;
        _offset = 0;
        LoadEntries();
    }

    private void Search_Changed(object sender, TextChangedEventArgs e)
    {
        if (!_initialized) return;
        _offset = 0;
        LoadEntries();
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e) => LoadEntries();

    private void PrevButton_Click(object sender, RoutedEventArgs e)
    {
        _offset = Math.Max(0, _offset - PageSize);
        LoadEntries();
    }

    private void NextButton_Click(object sender, RoutedEventArgs e)
    {
        if (_offset + PageSize < _total) { _offset += PageSize; LoadEntries(); }
    }

    private void DeleteSelectedButton_Click(object sender, RoutedEventArgs e)
    {
        if (_cache is null) return;
        var ids = _rows.Where(r => r.IsSelected).Select(r => r.Id).ToList();
        if (ids.Count == 0) { Notify("提示", "未选中任何条目"); return; }
        if (MessageBox.Show($"确认删除选中的 {ids.Count} 条缓存？", "确认", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        int deleted = _cache.DeleteByIds(ids);
        Notify("已删除", $"{deleted} 条缓存已删除", ControlAppearance.Success);
        if (_offset > 0 && _offset >= _total - deleted) _offset = Math.Max(0, _offset - PageSize);
        LoadEntries();
    }

    private void ClearAllButton_Click(object sender, RoutedEventArgs e)
    {
        if (_cache is null) return;
        if (MessageBox.Show("确认清空全部翻译缓存？此操作不可恢复。", "确认清空",
            MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _cache.Clear();
        _offset = 0;
        Notify("已清空", "全部翻译缓存已清除", ControlAppearance.Success);
        LoadEntries();
    }

    private void ExportButton_Click(object sender, RoutedEventArgs e) => Export(false);
    private void ExportFilteredButton_Click(object sender, RoutedEventArgs e) => Export(true);

    private void Export(bool filtered)
    {
        if (_cache is null) return;
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "JSON 文件 (*.json)|*.json",
            FileName = "translations.json"
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            string src = filtered ? (SourceLangFilter.SelectedValue as string ?? "") : "";
            string tgt = filtered ? (TargetLangFilter.SelectedValue as string ?? "") : "";
            int n = _cache.ExportToJson(dlg.FileName,
                string.IsNullOrEmpty(src) ? null : src,
                string.IsNullOrEmpty(tgt) ? null : tgt);
            Notify("导出完成", $"已导出 {n} 条到 {Path.GetFileName(dlg.FileName)}", ControlAppearance.Success);
        }
        catch (Exception ex) { Notify("导出失败", ex.Message, ControlAppearance.Caution); }
    }

    private void ImportButton_Click(object sender, RoutedEventArgs e)
    {
        if (_cache is null) return;
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "JSON 文件 (*.json)|*.json" };
        if (dlg.ShowDialog() != true) return;
        bool overwrite = MessageBox.Show(
            "遇到相同条目时是否覆盖？\n· 是 = 覆盖已有译文\n· 否 = 跳过，保留自己已翻译的译文",
            "导入选项", MessageBoxButton.YesNo) == MessageBoxResult.Yes;
        try
        {
            int n = _cache.ImportFromJson(dlg.FileName, overwrite);
            Notify("导入完成", $"已导入 {n} 条", ControlAppearance.Success);
            LoadEntries();
        }
        catch (Exception ex) { Notify("导入失败", ex.Message, ControlAppearance.Caution); }
    }
}

/// <summary>DataGrid 行视图：包装 CacheEntry + 选择状态。</summary>
public sealed class CacheRow
{
    public long Id { get; set; }
    public bool IsSelected { get; set; }
    public string SourceText { get; set; } = "";
    public string Translation { get; set; } = "";
    public string Direction => $"{SourceLang} → {TargetLang}";
    public string SourceLang { get; set; } = "";
    public string TargetLang { get; set; } = "";
    public long HitCount { get; set; }
    public string LastAccessed { get; set; } = "";

    public CacheRow(CacheEntry e)
    {
        Id = e.Id;
        SourceText = e.SourceText;
        Translation = e.Translation;
        SourceLang = e.SourceLang;
        TargetLang = e.TargetLang;
        HitCount = e.HitCount;
        LastAccessed = e.LastAccessed;
    }
}

/// <summary>简易文本输入对话框（用于新建/重命名方案）。返回是否确认与输入文本。</summary>
internal sealed class InputDialog : System.Windows.Window
{
    private readonly System.Windows.Controls.TextBox _box;
    public string ResultText { get; private set; } = "";

    public InputDialog(string title, string prompt, string defaultValue = "")
    {
        Title = title;
        Width = 440;
        MinWidth = 360;
        MinHeight = 180;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.CanMinimize;
        ShowInTaskbar = false;

        var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(24) };
        var promptBlock = new System.Windows.Controls.TextBlock
        {
            Text = prompt,
            Margin = new Thickness(0, 0, 0, 12),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13
        };
        _box = new System.Windows.Controls.TextBox
        {
            Text = defaultValue,
            Padding = new Thickness(8, 6, 8, 6),
            FontSize = 14,
            MinHeight = 32,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        var btnPanel = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 20, 0, 0)
        };
        var okBtn = new Wpf.Ui.Controls.Button
        {
            Content = "确定",
            Appearance = Wpf.Ui.Controls.ControlAppearance.Primary,
            Padding = new Thickness(24, 8, 24, 8),
            MinWidth = 104,
            IsDefault = true
        };
        okBtn.Click += (_, _) => { ResultText = _box.Text.Trim(); DialogResult = true; Close(); };
        var cancelBtn = new Wpf.Ui.Controls.Button
        {
            Content = "取消",
            Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary,
            Padding = new Thickness(24, 8, 24, 8),
            MinWidth = 104,
            IsCancel = true,
            Margin = new Thickness(12, 0, 0, 0)
        };
        btnPanel.Children.Add(okBtn);
        btnPanel.Children.Add(cancelBtn);
        panel.Children.Add(promptBlock);
        panel.Children.Add(_box);
        panel.Children.Add(btnPanel);
        Content = panel;

        Loaded += (_, _) =>
        {
            _box.SelectAll();
            _box.Focus();
        };
    }
}
