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
    private ViewerStore store;
    private readonly ViewerStore baseStore;
    private readonly LibraryManager libraries;
    private ViewerPreferences preferences;
    private readonly HashSet<string> selected = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> pendingWords = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string[]> navigationLevels = [];
    private int CurrentMinimumGroups => selected.Count == 0 ? preferences.Rules.MinimumGroups : Math.Clamp(preferences.DrilldownMinimumGroups, 2, 1000000);
    private string[] SearchWords => selected.Concat(pendingWords).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    private FileIndex? index;
    private FileIndex? fullIndex;
    private FileFormatOption format = new("all", "全部格式", []);
    private CancellationTokenSource? formatCancellation;
    private int formatVersion;
    private bool configuringFormat, switchingLibrary;
    private Task? activeScan;
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
        baseStore = store;
        libraries = new LibraryManager(baseStore, root);
        this.store = libraries.StoreFor(libraries.Active);
        preferences = this.store.LoadPreferences();
        InitializeComponent();
        VersionText.Text = $"v{typeof(App).Assembly.GetName().Version?.ToString(3)} · 点击逐层检索 · Ctrl 多选后进入下一层";
        Width = double.IsFinite(preferences.WindowWidth) ? Math.Clamp(preferences.WindowWidth, MinWidth, Math.Max(MinWidth, SystemParameters.WorkArea.Width)) : 780;
        Height = double.IsFinite(preferences.WindowHeight) ? Math.Clamp(preferences.WindowHeight, MinHeight, Math.Max(MinHeight, SystemParameters.WorkArea.Height)) : 900;
        SortField.SelectedIndex = Math.Clamp((int)preferences.KeywordOrder, 0, 2);
        SortDirection.SelectedIndex = preferences.KeywordDescending ? 1 : 0;
        LengthFilter.DisplayMemberPath = "Label"; LengthFilter.SelectedValuePath = "Value";
        LengthFilter.ItemsSource = new[] { new LengthOption(0, "全部字数") }; LengthFilter.SelectedIndex = 0;
        InfiniteKeywordList.ItemsSource = keywordBatches;
        RootBox.Text = libraries.Active.Root;
        SyncLibraryBox();
        keywordTimer.Tick += (_, _) => { keywordTimer.Stop(); PopulateKeywords(); };
        matchTimer.Tick += async (_, _) => { matchTimer.Stop(); await UpdateMatchesAsync(); };
    }
    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        everything = EverythingBridge.Discover(preferences.EverythingPath);
        UpdateEverythingStatus();
        ready = true;
        await LoadCurrentLibraryAsync();
    }
    private async Task ApplyIndexAsync(FileIndex value)
    {
        var upgraded = await Task.Run(() => FileFormats.EnsureKeywords(value, Path.Combine(AppContext.BaseDirectory, "Resources"), CancellationToken.None));
        if (closing) return;
        if (!ReferenceEquals(upgraded, value)) await Task.Run(() => store.SaveIndex(upgraded));
        fullIndex = upgraded;
        configuringFormat = true;
        try
        {
            var options = FileFormats.Options(upgraded); FormatFilter.ItemsSource = options;
            if (!options.Any(o => o.Key == preferences.FileFormat)) preferences.FileFormat = "all";
            FormatFilter.SelectedValue = preferences.FileFormat;
        }
        finally { configuringFormat = false; }
        IndexSummary.Text = $"{value.Files.Count:N0} 个文件 · {value.GroupCount:N0} 个资料组 · 更新于 {value.BuiltUtc.ToLocalTime():MM-dd HH:mm} · 每组每词只计一次";
        IndexSummary.ToolTip = value.Root;
        IssuesButton.IsEnabled = true;
        await ApplyFormatAsync();
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
    private Task StartScanAsync() => activeScan = ScanAsync();
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await StartScanAsync();
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
        KeywordSummary.Text = $"已加载 {Math.Min(keywordBatches.Count * 100, filteredKeywords.Count):N0} / {filteredKeywords.Count:N0} 个 · 门槛 {CurrentMinimumGroups} 组";
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
        var lengths = KeywordView.AvailableLengths(index.Keywords, preferences, filter, CurrentMinimumGroups);
        updatingLengthOptions = true;
        try
        {
            // Preserve the user's length filter even when this narrower scope has no word of that length.
            var values = new[] { 0 }.Concat(lengths).ToList();
            if (preferences.KeywordLength != 0 && !values.Contains(preferences.KeywordLength)) values.Add(preferences.KeywordLength);
            var options = values.Select(n => new LengthOption(n, n == 0 ? "全部字数" : lengths.Contains(n) ? $"{n} 字" : $"{n} 字（无匹配）")).ToList();
            if (LengthFilter.ItemsSource is not IEnumerable<LengthOption> existing || !existing.SequenceEqual(options))
                LengthFilter.ItemsSource = options;
            LengthFilter.SelectedValue = preferences.KeywordLength;
        }
        finally { updatingLengthOptions = false; }
        var candidates = KeywordView.Apply(index.Keywords, preferences, filter, CurrentMinimumGroups).Select((k, i) => new KeywordChoice(k.Word, k.Groups, i + 1, pendingWords.Contains(k.Word))).ToList();
        filteredKeywords = candidates;
        var available = candidates.Count;
        var pages = Math.Max(1, (available + 99) / 100);
        keywordPage = Math.Clamp(keywordPage, 1, pages);
        var wasLoaded = resetWaterfall ? 1 : keywordBatches.Count;
        resetWaterfall = false;
        keywordBatches.Clear();
        EnsureLoadedPage(Math.Max(keywordPage, Math.Max(1, wasLoaded)));
        UpdatePageSummary();
        KeywordSummary.ToolTip = $"原始高频词 {index.Keywords.Count:N0} 个；当前规则：至少 {CurrentMinimumGroups} 个资料组，词长 {preferences.Rules.MinimumLength}～{preferences.Rules.MaximumLength}，{(preferences.Rules.ChineseOnly ? "仅中文" : "含英文")}。右键关键词可忽略。";
        EmptyHint.Visibility = available == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyHint.Text = selected.Count > 0 ? "没有更多符合筛选规则的关键词；可返回上层或调整规则" : filter.Length > 0 ? "未找到符合规则的关键词，可调整关键词规则" : "没有符合当前规则的关键词，可降低资料组门槛";
        SelectionSummary.Text = SearchWords.Length == 0 ? "点击关键词开始逐层检索" : "当前条件：" + string.Join(" ＋ ", SearchWords);
        SelectionSummary.ToolTip = SelectionSummary.Text;
        SelectedWords.ItemsSource = SearchWords;
        EnterNextLevel.IsEnabled = pendingWords.Count > 0 && InfiniteKeywordList.IsEnabled;
        UpdateBreadcrumbs();
    }
    private async void Keyword_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string word } || index is null || !InfiniteKeywordList.IsEnabled) return;
        if (MultiSelect.IsChecked == true || Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            if (!pendingWords.Add(word)) pendingWords.Remove(word);
            PopulateKeywords(); await UpdateMatchesAsync();
        }
        else await EnterLevelAsync([word]);
    }
    private void MultiSelect_Changed(object sender, RoutedEventArgs e) { }
    private async void EnterNextLevel_Click(object sender, RoutedEventArgs e) => await EnterLevelAsync(pendingWords.ToArray());
    private async Task EnterLevelAsync(string[] words)
    {
        var added = words.Where(w => !selected.Contains(w)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (added.Length == 0) return;
        navigationLevels.Add(added); selected.UnionWith(added);
        await RefreshNavigationAsync();
    }
    private async Task ReturnToLevelAsync(int depth)
    {
        depth = Math.Clamp(depth, 0, navigationLevels.Count);
        navigationLevels.RemoveRange(depth, navigationLevels.Count - depth);
        selected.Clear(); selected.UnionWith(navigationLevels.SelectMany(x => x));
        await RefreshNavigationAsync();
    }
    private async Task RefreshNavigationAsync()
    {
        pendingWords.Clear(); keywordTimer.Stop(); KeywordSearch.Text = ""; keywordTimer.Stop();
        keywordPage = 1; resetWaterfall = true;
        await ApplyFormatAsync();
    }
    private void UpdateBreadcrumbs()
    {
        NavigationPath.Children.Clear();
        Add("全部资料", 0);
        for (int i = 0; i < navigationLevels.Count; i++)
        {
            NavigationPath.Children.Add(new TextBlock { Text = " › ", VerticalAlignment = VerticalAlignment.Center });
            Add(string.Join(" ＋ ", navigationLevels[i]), i + 1);
        }
        void Add(string title, int depth)
        {
            var button = new Button { Content = title, Padding = new Thickness(6, 3, 6, 3), Margin = new Thickness(2),
                ToolTip = "返回此层，清除之后的条件", IsEnabled = depth != navigationLevels.Count || pendingWords.Count > 0 };
            button.Click += async (_, _) => await ReturnToLevelAsync(depth);
            NavigationPath.Children.Add(button);
        }
    }
    private async void Clear_Click(object sender, RoutedEventArgs e) { ExtraSearch.Text = ""; await ReturnToLevelAsync(0); }
    private async void RemoveSelected_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string word }) return;
        if (pendingWords.Remove(word)) { PopulateKeywords(); await UpdateMatchesAsync(); return; }
        var depth = navigationLevels.FindIndex(level => level.Contains(word, StringComparer.OrdinalIgnoreCase));
        if (depth >= 0) await ReturnToLevelAsync(depth);
    }
    private async void ResetFilters_Click(object sender, RoutedEventArgs e)
    {
        ready = false; selected.Clear(); pendingWords.Clear(); navigationLevels.Clear(); KeywordSearch.Text = ""; ExtraSearch.Text = ""; MultiSelect.IsChecked = false;
        preferences.KeywordLength = 0; preferences.FileFormat = "all"; preferences.KeywordOrder = KeywordOrder.Frequency; preferences.KeywordDescending = true;
        LengthFilter.SelectedValue = 0; SortField.SelectedIndex = 0; SortDirection.SelectedIndex = 1;
        configuringFormat = true; FormatFilter.SelectedValue = "all"; configuringFormat = false;
        ready = true; resetWaterfall = true; keywordPage = 1; SavePreferences(); await ApplyFormatAsync();
    }
    private async void Format_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!ready || configuringFormat || FormatFilter.SelectedItem is not FileFormatOption choice) return;
        preferences.FileFormat = choice.Key; SavePreferences(); resetWaterfall = true; keywordPage = 1;
        try { await ApplyFormatAsync(); } catch (Exception ex) { StatusText.Text = "格式筛选未完成：" + ex.Message; }
    }
    private async Task ApplyFormatAsync()
    {
        if (fullIndex is null || FormatFilter.SelectedItem is not FileFormatOption choice) return;
        formatCancellation?.Cancel(); var source = new CancellationTokenSource(); formatCancellation = source;
        var version = ++formatVersion; var current = fullIndex; var scope = selected.ToArray();
        matchCancellation?.Cancel(); matchingVersion++; matchTimer.Stop();
        InfiniteKeywordList.IsEnabled = false; EnterNextLevel.IsEnabled = false; OpenEverythingButton.IsEnabled = false;
        EmptyHint.Text = "正在统计当前范围的关键词…"; EmptyHint.Visibility = Visibility.Visible;
        try
        {
            var result = await Task.Run(() => KeywordDrilldown.Select(FileFormats.Select(current, choice, source.Token), scope, source.Token));
            var built = await Task.Run(() => new SearchEngine(result), source.Token);
            if (closing || version != formatVersion) return;
            index = result; engine = built; format = choice;
            InfiniteKeywordList.IsEnabled = true;
            PopulateKeywords(); await UpdateMatchesAsync();
            if (closing || version != formatVersion) return;
            IndexSummary.Text = $"{(Directory.Exists(result.Root) ? "在线" : "离线缓存")} · {choice.Label} · {result.Files.Count:N0} 个文件 · {result.GroupCount:N0} 个资料组 · 更新于 {result.BuiltUtc.ToLocalTime():MM-dd HH:mm} · 跳过 {result.Issues.Count} 项";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!closing && version == formatVersion) { StatusText.Text = "关键词统计未完成：" + ex.Message; } }
        finally
        {
            if (!closing && version == formatVersion) { InfiniteKeywordList.IsEnabled = true; EnterNextLevel.IsEnabled = pendingWords.Count > 0; }
            if (formatCancellation == source) formatCancellation = null; source.Dispose();
        }
    }
    private void ExtraSearch_Changed(object sender, TextChangedEventArgs e) { if (!ready) return; matchTimer.Stop(); matchTimer.Start(); }
    private async Task UpdateMatchesAsync()
    {
        if (engine is null || index is null || closing || !InfiniteKeywordList.IsEnabled) return;
        matchCancellation?.Cancel();
        var source = new CancellationTokenSource(); matchCancellation = source;
        var version = ++matchingVersion;
        var current = engine;
        var words = SearchWords; var extra = ExtraSearch.Text;
        SelectionSummary.Text = words.Length == 0 ? "点击关键词开始逐层检索" : "当前条件：" + string.Join(" ＋ ", words) + (pendingWords.Count > 0 ? "（含待进入条件）" : "");
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
        if (index is null || !InfiniteKeywordList.IsEnabled) return;
        everything = EverythingBridge.Discover(string.IsNullOrWhiteSpace(preferences.EverythingPath) ? everything?.Path : preferences.EverythingPath);
        UpdateEverythingStatus();
        if (everything is null) { StatusText.Text = "未找到 Everything，请点击“选择 Everything”指定已有程序。"; return; }
        if (!Directory.Exists(index.Root)) { StatusText.Text = "扫描目录或磁盘当前不可用，恢复连接后重试。"; return; }
        try { pendingEverythingQuery = EverythingBridge.BuildQuery(index.Root, SearchWords, ExtraSearch.Text) + FileFormats.EverythingClause(format); }
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
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223) { StatusText.Text = "Everything 启动已取消；再次点击“在 Everything 查看”可重试。"; }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 740) { StatusText.Text = "Everything 主程序需要管理员权限。请先手动打开原 Everything，确认一次后再点击“在 Everything 查看”。"; }
        catch (Exception ex) { StatusText.Text = "Everything 搜索未发送：" + ex.Message; SafeLog("Dispatch failed: " + ex.Message); }
        finally { dispatchingEverything = false; pendingEverythingQuery = null; }
    }
    private void UpdateEverythingStatus()
    {
        EverythingStatus.Text = everything is null ? "未找到 Everything · 请手动选择已有程序" : "已连接 Everything " + everything.Version;
        EverythingStatus.ToolTip = everything?.Path ?? "不安装或修改 Everything";
        OpenEverythingButton.IsEnabled = everything is not null && index is not null && InfiniteKeywordList.IsEnabled;
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
        pendingWords.RemoveWhere(word => !preferences.Rules.Accepts(index?.Keywords.FirstOrDefault(k => k.Word.Equals(word, StringComparison.OrdinalIgnoreCase)) ?? new(word, 0), CurrentMinimumGroups));
        SavePreferences(); PopulateKeywords(); await UpdateMatchesAsync();
        StatusText.Text = $"已添加 {words.Length} 个屏蔽词；可在忽略与恢复中恢复。";
    }
    private async void Ignore_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem item || item.Parent is not ContextMenu menu || menu.PlacementTarget is not Button { Tag: string word }) return;
        preferences.Ignored.Add(word); pendingWords.Remove(word); SavePreferences(); PopulateKeywords(); await UpdateMatchesAsync();
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
        var rules = preferences.Rules;
        var contextual = new KeywordRules { MinimumGroups = CurrentMinimumGroups, MinimumLength = rules.MinimumLength,
            MaximumLength = rules.MaximumLength, ChineseOnly = rules.ChineseOnly, ExcludedWords = rules.ExcludedWords, ExcludedContains = rules.ExcludedContains };
        var dialog = new RulesWindow(contextual) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Result is null) return;
        if (selected.Count > 0)
        {
            preferences.DrilldownMinimumGroups = dialog.Result.MinimumGroups;
            dialog.Result.MinimumGroups = preferences.Rules.MinimumGroups;
        }
        preferences.Rules = dialog.Result;
        pendingWords.RemoveWhere(word => !preferences.Rules.Accepts(index?.Keywords.FirstOrDefault(k => string.Equals(k.Word, word, StringComparison.OrdinalIgnoreCase)) ?? new(word, 0), CurrentMinimumGroups));
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
        formatCancellation?.Cancel();
    }
    private sealed record KeywordChoice(string Word, int Groups, int Rank, bool Selected);
    private sealed record KeywordBatch(List<KeywordChoice> Items);
    private sealed record LengthOption(int Value, string Label);
    private sealed record BlockEntry(string Word, string Kind) { public string Label => $"{Word}  ·  {Kind}"; }
}
