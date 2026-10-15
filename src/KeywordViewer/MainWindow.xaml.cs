using System.ComponentModel;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Windows.Input;
using KeywordViewer.Core;

namespace KeywordViewer;
public partial class MainWindow : Window
{
    private readonly ViewerStore store;
    private ViewerPreferences preferences;
    private readonly HashSet<string> selected = new(StringComparer.OrdinalIgnoreCase);
    private FileIndex? index;
    private SearchEngine? engine;
    private EverythingInfo? everything;
    private CancellationTokenSource? scanCancellation, matchCancellation;
    private readonly DispatcherTimer keywordTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly DispatcherTimer matchTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private bool ready, closing;
    private int matchingVersion;
    private int keywordPage = 1;
    private List<KeywordChoice> filteredKeywords = [];
    private readonly ObservableCollection<KeywordBatch> keywordBatches = [];
    private bool loadingWaterfall;
    private bool resetWaterfall;
    private bool dispatchingEverything;
    private string? pendingEverythingQuery;
    private bool updatingLengthOptions;
    public MainWindow(ViewerStore store, string? root)
    {
        this.store = store;
        try { preferences = store.LoadPreferences(); } catch (Exception ex) { preferences = new(); SafeLog("Preferences load failed: " + ex.Message); }
        InitializeComponent();
        Width = double.IsFinite(preferences.WindowWidth) ? Math.Clamp(preferences.WindowWidth, MinWidth, Math.Max(MinWidth, SystemParameters.WorkArea.Width)) : 780;
        Height = double.IsFinite(preferences.WindowHeight) ? Math.Clamp(preferences.WindowHeight, MinHeight, Math.Max(MinHeight, SystemParameters.WorkArea.Height)) : 900;
        SortField.SelectedIndex = Math.Clamp((int)preferences.KeywordOrder, 0, 2);
        SortDirection.SelectedIndex = preferences.KeywordDescending ? 1 : 0;
        LengthFilter.DisplayMemberPath = "Label"; LengthFilter.SelectedValuePath = "Value";
        LengthFilter.ItemsSource = new[] { new LengthOption(0, "全部字数") }; LengthFilter.SelectedIndex = 0;
        InfiniteKeywordList.ItemsSource = keywordBatches;
        RootBox.Text = root ?? preferences.Root;
        keywordTimer.Tick += (_, _) => { keywordTimer.Stop(); PopulateKeywords(); };
        matchTimer.Tick += async (_, _) => { matchTimer.Stop(); await UpdateMatchesAsync(); };
    }
    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        everything = EverythingBridge.Discover(preferences.EverythingPath);
        UpdateEverythingStatus();
        ready = true;
        RefreshButton.IsEnabled = false;
        try
        {
            StatusText.Text = "正在加载本地缓存…";
            var cacheRoot = RootBox.Text;
            var cached = await Task.Run(() => store.LoadIndex(cacheRoot));
            if (cached is not null)
            {
                await ApplyIndexAsync(cached);
                StatusText.Text = $"已加载缓存 · {cached.Files.Count:N0} 个文件；新增或删除文件后，请刷新索引";
                SafeLog($"Cache loaded: files={cached.Files.Count}, groups={cached.GroupCount}");
            }
            else await ScanAsync();
        }
        catch (Exception ex) { SafeLog("Cache load failed: " + ex.Message); StatusText.Text = "缓存不可用，将重新扫描。"; await ScanAsync(); }
        finally { if (!closing) RefreshButton.IsEnabled = true; }
    }
    private async Task ApplyIndexAsync(FileIndex value)
    {
        var builtEngine = await Task.Run(() => new SearchEngine(value));
        if (closing) return;
        index = value; engine = builtEngine;
        selected.RemoveWhere(word => !value.Keywords.Any(x => string.Equals(x.Word, word, StringComparison.OrdinalIgnoreCase)));
        IndexSummary.Text = $"{value.Files.Count:N0} 个文件 · {value.GroupCount:N0} 个资料组 · 更新于 {value.BuiltUtc.ToLocalTime():MM-dd HH:mm} · 每组每词只计一次";
        IndexSummary.ToolTip = value.Root;
        IssuesButton.IsEnabled = true;
        PopulateKeywords();
        await UpdateMatchesAsync();
    }
    private async Task ScanAsync()
    {
        if (scanCancellation is not null || closing) return;
        string root;
        try { root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(RootBox.Text.Trim())); }
        catch (Exception ex) { StatusText.Text = "目录格式不正确：" + ex.Message; return; }
        var source = new CancellationTokenSource(); scanCancellation = source;
        RefreshButton.IsEnabled = false; RootBox.IsEnabled = false; CancelButton.IsEnabled = true; ScanBar.Visibility = Visibility.Visible;
        StatusText.Text = "正在扫描文件名称并提取关键词…";
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var progress = new Progress<ScanProgress>(p => { if (!closing && scanCancellation == source && !source.IsCancellationRequested) StatusText.Text = $"正在扫描：{p.Files:N0} 个文件 · {p.Directories:N0} 个目录 · 跳过 {p.Skipped:N0} 项"; });
        try
        {
            var result = await Task.Run(() => new IndexScanner(Path.Combine(AppContext.BaseDirectory, "Resources")).Scan(root, source.Token, progress));
            StatusText.Text = "扫描完成，正在保存缓存…";
            await Task.Run(() => store.SaveIndex(result, source.Token));
            await ApplyIndexAsync(result);
            preferences.Root = root; SavePreferences();
            StatusText.Text = $"扫描完成：{result.Files.Count:N0} 个文件 · 跳过 {result.Issues.Count:N0} 项 · 用时 {watch.Elapsed.TotalSeconds:F1} 秒";
            SafeLog($"Scan completed: files={result.Files.Count}, groups={result.GroupCount}, keywords={result.Keywords.Count}, skipped={result.Issues.Count}, seconds={watch.Elapsed.TotalSeconds:F1}");
        }
        catch (OperationCanceledException) { if (!closing) StatusText.Text = "已取消扫描，保留上次完整索引；可点击刷新重试。"; SafeLog("Scan cancelled; prior cache retained"); }
        catch (Exception ex) { if (!closing) StatusText.Text = "扫描未完成，保留原索引：" + ex.Message; SafeLog("Scan failed: " + ex.Message); }
        finally
        {
            scanCancellation = null; source.Dispose();
            if (!closing) { RefreshButton.IsEnabled = true; RootBox.IsEnabled = true; CancelButton.IsEnabled = false; ScanBar.Visibility = Visibility.Collapsed; }
        }
    }
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await ScanAsync();
    private void Cancel_Click(object sender, RoutedEventArgs e) { scanCancellation?.Cancel(); CancelButton.IsEnabled = false; StatusText.Text = "正在取消扫描…"; }
    private void KeywordSearch_Changed(object sender, RoutedEventArgs e) { if (!ready) return; keywordPage = 1; resetWaterfall = true; keywordTimer.Stop(); keywordTimer.Start(); }
    private void ViewOptions_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!ready || updatingLengthOptions) return;
        preferences.KeywordOrder = (KeywordOrder)Math.Max(0, SortField.SelectedIndex);
        preferences.KeywordDescending = SortDirection.SelectedIndex == 1;
        preferences.KeywordLength = LengthFilter.SelectedValue is int length ? length : 0;
        SavePreferences(); KeywordSearch_Changed(sender, e);
    }
    private void PreviousPage_Click(object sender, RoutedEventArgs e) => NavigatePage(keywordPage - 1);
    private void NextPage_Click(object sender, RoutedEventArgs e) => NavigatePage(keywordPage + 1);
    private void NavigatePage(int page)
    {
        keywordPage = Math.Clamp(page, 1, Math.Max(1, (filteredKeywords.Count + 99) / 100));
        EnsureLoadedPage(keywordPage); UpdatePageSummary();
        if (keywordBatches.Count > 0) InfiniteKeywordList.ScrollIntoView(keywordBatches[keywordPage - 1]);
    }
    private void EnsureLoadedPage(int page)
    {
        while (keywordBatches.Count < page && keywordBatches.Count * 100 < filteredKeywords.Count)
            keywordBatches.Add(new(filteredKeywords.Skip(keywordBatches.Count * 100).Take(100).ToList()));
    }
    private void UpdatePageSummary()
    {
        var pages = Math.Max(1, (filteredKeywords.Count + 99) / 100);
        PreviousPage.IsEnabled = keywordPage > 1; NextPage.IsEnabled = keywordPage < pages;
        PageSummary.Text = $" {keywordPage} / {pages} 页 · 已加载 {keywordBatches.Count} 页 ";
        KeywordSummary.Text = $"已加载 {Math.Min(keywordBatches.Count * 100, filteredKeywords.Count):N0} / {filteredKeywords.Count:N0} 个 · 门槛 {preferences.Rules.MinimumGroups} 组";
    }
    private void Infinite_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (loadingWaterfall || e.VerticalChange <= 0 || e.ExtentHeight - e.VerticalOffset - e.ViewportHeight > 200) return;
        loadingWaterfall = true;
        EnsureLoadedPage(keywordBatches.Count + 1);
        keywordPage = Math.Max(1, keywordBatches.Count); UpdatePageSummary();
        // Keep the current position when the extent grows, rather than carrying an End command into all new pages.
        if (e.OriginalSource is ScrollViewer viewer) viewer.ScrollToVerticalOffset(e.VerticalOffset);
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => loadingWaterfall = false));
    }
    private void PopulateKeywords()
    {
        if (index is null) return;
        var filter = KeywordSearch.Text.Trim();
        var lengths = KeywordView.AvailableLengths(index.Keywords, preferences, filter);
        updatingLengthOptions = true;
        try
        {
            if (preferences.KeywordLength != 0 && !lengths.Contains(preferences.KeywordLength)) { preferences.KeywordLength = 0; SavePreferences(); }
            var values = new[] { 0 }.Concat(lengths).ToList();
            if (LengthFilter.ItemsSource is not IEnumerable<LengthOption> existing || !existing.Select(x => x.Value).SequenceEqual(values))
                LengthFilter.ItemsSource = values.Select(n => new LengthOption(n, n == 0 ? "全部字数" : $"{n} 字")).ToList();
            LengthFilter.SelectedValue = preferences.KeywordLength;
        }
        finally { updatingLengthOptions = false; }
        var candidates = KeywordView.Apply(index.Keywords, preferences, filter).Select((k, i) => new KeywordChoice(k.Word, k.Groups, i + 1, selected.Contains(k.Word))).ToList();
        filteredKeywords = candidates;
        var available = candidates.Count;
        var pages = Math.Max(1, (available + 99) / 100);
        keywordPage = Math.Clamp(keywordPage, 1, pages);
        var wasLoaded = resetWaterfall ? 1 : keywordBatches.Count;
        resetWaterfall = false;
        keywordBatches.Clear();
        EnsureLoadedPage(Math.Max(keywordPage, Math.Max(1, wasLoaded)));
        UpdatePageSummary();
        KeywordSummary.ToolTip = $"原始高频词 {index.Keywords.Count:N0} 个；当前规则：至少 {preferences.Rules.MinimumGroups} 个资料组，词长 {preferences.Rules.MinimumLength}～{preferences.Rules.MaximumLength}，{(preferences.Rules.ChineseOnly ? "仅中文" : "含英文")}。右键关键词可忽略。";
        EmptyHint.Visibility = available == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyHint.Text = filter.Length > 0 ? "未找到符合规则的关键词，可调整关键词规则" : "没有符合当前规则的关键词，可降低资料组门槛";
        SelectionSummary.Text = selected.Count == 0 ? "尚未选择关键词" : "已选：" + string.Join(" ＋ ", selected);
        SelectionSummary.ToolTip = SelectionSummary.Text;
    }
    private async void Keyword_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string word } || index is null) return;
        if (MultiSelect.IsChecked == true || Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { if (!selected.Add(word)) selected.Remove(word); }
        else { selected.Clear(); selected.Add(word); }
        PopulateKeywords();
        await UpdateMatchesAsync();
        await ShowEverythingAsync();
    }
    private async void MultiSelect_Changed(object sender, RoutedEventArgs e)
    {
        if (!ready || MultiSelect.IsChecked == true || selected.Count <= 1) return;
        var keep = selected.First(); selected.Clear(); selected.Add(keep); PopulateKeywords(); await UpdateMatchesAsync();
    }
    private async void Clear_Click(object sender, RoutedEventArgs e) { selected.Clear(); ExtraSearch.Text = ""; PopulateKeywords(); await UpdateMatchesAsync(); }
    private void ExtraSearch_Changed(object sender, TextChangedEventArgs e) { if (!ready) return; matchTimer.Stop(); matchTimer.Start(); }
    private async Task UpdateMatchesAsync()
    {
        if (engine is null || index is null || closing) return;
        matchCancellation?.Cancel();
        var source = new CancellationTokenSource(); matchCancellation = source;
        var version = ++matchingVersion;
        var current = engine;
        var words = selected.ToArray(); var extra = ExtraSearch.Text;
        SelectionSummary.Text = words.Length == 0 ? "尚未选择关键词" : "已选：" + string.Join(" ＋ ", words);
        try
        {
            EverythingBridge.BuildQuery(index.Root, words, extra);
            var result = await Task.Run(() => current.Search(new(extra, words, "", FileSort.Name, false, 1, 1), source.Token));
            if (closing || version != matchingVersion) return;
            MatchSummary.Text = result.Total == 0 ? "本地索引匹配 0 个文件；可减少关键词" : $"本地索引匹配 {result.Total:N0} 个文件 · 多选时同时匹配";
            MatchSummary.ToolTip = "这是本面板索引中的数量；Everything 当前类型筛选或索引范围可能影响它显示的结果。";
            OpenEverythingButton.IsEnabled = everything is not null;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!closing && version == matchingVersion) { MatchSummary.Text = ex.Message; OpenEverythingButton.IsEnabled = false; } }
        finally { if (matchCancellation == source) matchCancellation = null; source.Dispose(); }
    }
    private async void OpenEverything_Click(object sender, RoutedEventArgs e) => await ShowEverythingAsync();
    private async Task ShowEverythingAsync()
    {
        if (index is null) return;
        everything = EverythingBridge.Discover(everything?.Path ?? preferences.EverythingPath);
        UpdateEverythingStatus();
        if (everything is null) { StatusText.Text = "未找到 Everything，请点击“选择 Everything”指定已有程序。"; return; }
        if (!Directory.Exists(index.Root)) { StatusText.Text = "扫描目录或磁盘当前不可用，恢复连接后重试。"; return; }
        try { pendingEverythingQuery = EverythingBridge.BuildQuery(index.Root, selected, ExtraSearch.Text); }
        catch (Exception ex) { StatusText.Text = ex.Message; return; }
        if (dispatchingEverything) { StatusText.Text = "Everything 正在启动，将显示最后一次选择…"; return; }
        dispatchingEverything = true;
        try
        {
            StatusText.Text = "正在启动或连接 Everything…";
            while (pendingEverythingQuery is string query && !closing)
            {
                pendingEverythingQuery = null;
                await EverythingBridge.ShowAsync(everything.Path, query);
            }
            if (closing) return;
            StatusText.Text = "已将搜索交给 Everything；可在其中排序、按类型筛选和打开文件。";
            SafeLog($"Everything search dispatched: terms={selected.Count}, extra={ExtraSearch.Text.Length > 0}");
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223) { StatusText.Text = "Everything 启动已取消；再次点击关键词可重新尝试自动启动。"; }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 740) { StatusText.Text = "Everything 主程序需要管理员权限。请先手动打开原 Everything，确认一次后再点击关键词。"; }
        catch (Exception ex) { StatusText.Text = "Everything 搜索未发送：" + ex.Message; SafeLog("Dispatch failed: " + ex.Message); }
        finally { dispatchingEverything = false; pendingEverythingQuery = null; }
    }
    private void UpdateEverythingStatus()
    {
        EverythingStatus.Text = everything is null ? "未找到 Everything · 请手动选择已有程序" : "已连接 Everything " + everything.Version;
        EverythingStatus.ToolTip = everything?.Path ?? "不安装或修改 Everything";
        OpenEverythingButton.IsEnabled = everything is not null && index is not null;
    }
    private void ChooseEverything_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "选择已安装的 Everything 程序", Filter = "Everything 程序 (*.exe)|*.exe", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        EverythingInfo found;
        try { found = EverythingBridge.SelectProgram(dialog.FileName); }
        catch (Exception ex) { StatusText.Text = ex.Message; return; }
        everything = found; preferences.EverythingPath = found.Path; SavePreferences(); UpdateEverythingStatus();
    }
    private async void BlockKeyword_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem item && item.Parent is ContextMenu menu && menu.PlacementTarget is Button { Tag: string word }) await AddBlockedWordsAsync(word, false);
    }
    private async void BlockChip_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is Button { Tag: string word }) await AddBlockedWordsAsync(word, false);
    }
    private async Task AddBlockedWordsAsync(string text, bool contains)
    {
        var words = text.Split(['\r', '\n', ',', '，', ';', '；'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0) { StatusText.Text = "请输入要屏蔽的词，每行一个。"; return; }
        foreach (var word in words)
        {
            if (contains) { if (!preferences.Rules.ExcludedContains.Contains(word, StringComparer.OrdinalIgnoreCase)) preferences.Rules.ExcludedContains.Add(word); }
            else preferences.Rules.ExcludedWords.Add(word);
        }
        selected.RemoveWhere(word => !preferences.Rules.Accepts(index?.Keywords.FirstOrDefault(k => k.Word.Equals(word, StringComparison.OrdinalIgnoreCase)) ?? new(word, 0)));
        SavePreferences(); PopulateKeywords(); await UpdateMatchesAsync();
        StatusText.Text = $"已添加 {words.Length} 个屏蔽词；可在忽略与恢复中恢复。";
    }
    private async void Ignore_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem item || item.Parent is not ContextMenu menu || menu.PlacementTarget is not Button { Tag: string word }) return;
        preferences.Ignored.Add(word); selected.Remove(word); SavePreferences(); PopulateKeywords(); await UpdateMatchesAsync();
    }
    private void Ignored_Click(object sender, RoutedEventArgs e)
    {
        List<BlockEntry> Entries() => preferences.Ignored.Select(x => new BlockEntry(x, "手动忽略")).Concat(preferences.Rules.ExcludedWords.Select(x => new BlockEntry(x, "屏蔽词"))).Concat(preferences.Rules.ExcludedContains.Select(x => new BlockEntry(x, "包含屏蔽"))).OrderBy(x => x.Word).ToList();
        var list = new ListBox { SelectionMode = SelectionMode.Extended, Margin = new Thickness(12), ItemsSource = Entries(), DisplayMemberPath = "Label" };
        var restore = new Button { Content = "恢复选中的关键词", Margin = new Thickness(12) };
        var all = new Button { Content = "恢复全部", Margin = new Thickness(12) };
        var hint = new TextBlock { Text = "× 屏蔽词、手动忽略及包含屏蔽都在这里。选择后恢复；按 Ctrl 可多选。恢复全部也会移除默认屏蔽词。", Margin = new Thickness(12), TextWrapping = TextWrapping.Wrap };
        var stack = new DockPanel(); DockPanel.SetDock(hint, Dock.Top); DockPanel.SetDock(restore, Dock.Bottom); DockPanel.SetDock(all, Dock.Bottom); stack.Children.Add(hint); stack.Children.Add(restore); stack.Children.Add(all); stack.Children.Add(list);
        var dialog = new Window { Title = "忽略与恢复", Width = 430, Height = 500, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = stack };
        restore.Click += (_, _) => { foreach (var entry in list.SelectedItems.Cast<BlockEntry>().ToArray()) { if (entry.Kind == "手动忽略") preferences.Ignored.Remove(entry.Word); else if (entry.Kind == "屏蔽词") preferences.Rules.ExcludedWords.Remove(entry.Word); else preferences.Rules.ExcludedContains.RemoveAll(x => x.Equals(entry.Word, StringComparison.OrdinalIgnoreCase)); } SavePreferences(); list.ItemsSource = Entries(); PopulateKeywords(); };
        all.Click += (_, _) => { preferences.Ignored.Clear(); preferences.Rules.ExcludedWords.Clear(); preferences.Rules.ExcludedContains.Clear(); SavePreferences(); list.ItemsSource = Entries(); PopulateKeywords(); };
        dialog.ShowDialog();
    }
    private void Issues_Click(object sender, RoutedEventArgs e)
    {
        if (index is null) return;
        var content = index.Issues.Count == 0 ? "扫描完成，没有跳过项。" : string.Join(Environment.NewLine + Environment.NewLine, index.Issues.Select(x => x.Path + Environment.NewLine + x.Reason));
        new Window { Title = "扫描记录", Width = 650, Height = 420, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = new TextBox { Text = content, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(12) } }.ShowDialog();
    }
    private async void Rules_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new RulesWindow(preferences.Rules) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Result is null) return;
        preferences.Rules = dialog.Result;
        selected.RemoveWhere(word => !preferences.Rules.Accepts(index?.Keywords.FirstOrDefault(k => string.Equals(k.Word, word, StringComparison.OrdinalIgnoreCase)) ?? new(word, 0)));
        SavePreferences(); PopulateKeywords(); await UpdateMatchesAsync();
        StatusText.Text = "关键词规则已保存，排行已更新；无需重新扫描。";
    }
    private void SavePreferences() { try { store.SavePreferences(preferences); } catch (Exception ex) { StatusText.Text = "偏好未能保存：" + ex.Message; } }
    private void SafeLog(string message) { try { store.Log(message); } catch { } }
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        closing = true;
        try { var current = store.LoadPreferences(); current.WindowWidth = RestoreBounds.Width; current.WindowHeight = RestoreBounds.Height; store.SavePreferences(current); }
        catch (Exception ex) { SafeLog("Window size save failed: " + ex.Message); }
        keywordTimer.Stop(); matchTimer.Stop(); scanCancellation?.Cancel(); matchCancellation?.Cancel();
    }
    private sealed record KeywordChoice(string Word, int Groups, int Rank, bool Selected);
    private sealed record KeywordBatch(List<KeywordChoice> Items);
    private sealed record LengthOption(int Value, string Label);
    private sealed record BlockEntry(string Word, string Kind) { public string Label => $"{Word}  ·  {Kind}"; }
}
