using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using KeywordViewer.Core;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        // Exercise the actual WPF controls and their attached handlers in a separate process and store.
        var project = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        var verification = Path.Combine(project, "verification");
        using var checkSource = JsonDocument.Parse(File.ReadAllText(Path.Combine(verification, "checks.json")));
        var fixture = checkSource.RootElement.GetProperty("Fixture").GetString()!;
        var rootStore = new ViewerStore(Path.Combine(verification, "ui-data-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")));
        var store = rootStore;
        var preferences = new ViewerPreferences { Root = fixture, Rules = new() { MinimumGroups = 2 } };
        store.SavePreferences(preferences);
        store.SaveIndex(new IndexScanner(Path.Combine(AppContext.BaseDirectory, "Resources")).Scan(fixture, default));
        var app = new TestApplication(); app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var applicationSource = System.Xml.Linq.XDocument.Load(Path.Combine(project, "src", "KeywordViewer", "App.xaml"));
        var presentation = applicationSource.Root!.Name.Namespace;
        var resourcesSource = applicationSource.Root.Element(presentation + "Application.Resources")!.Elements().Single();
        resourcesSource.SetAttributeValue(System.Xml.Linq.XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml");
        using (var sourceStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(resourcesSource.ToString())))
            app.Resources = (ResourceDictionary)System.Windows.Markup.XamlReader.Load(sourceStream, new System.Windows.Markup.ParserContext { BaseUri = new Uri("pack://application:,,,/KeywordViewer;component/") });
        // Main normally runs under Application.Run; preserve its dispatcher context for async handlers.
        System.Threading.SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        Exception? dispatcherError = null;
        app.DispatcherUnhandledException += (_, e) => { dispatcherError = e.Exception; e.Handled = true; };
        var panel = new KeywordViewer.MainWindow(rootStore, fixture); app.MainWindow = panel;
        ConfigureTestWindow(panel);
        var manager = new LibraryManager(rootStore); store = manager.StoreFor(manager.Active);
        try
        {
            panel.Show();
            Wait(() => ((TextBlock)panel.FindName("StatusText")).Text.StartsWith("已加载缓存"));
            Wait(() => WordButtons(panel).Count() >= 2);
            Assert(WordButtons(panel).Count() == 2, "Fixture keywords visible: " + string.Join(",", WordButtons(panel).Select(b => b.Tag)));
            Assert(Descendants<WrapPanel>(panel).Any(), "Keywords use wrapping panel");
            var fixtureLengths = (ComboBox)panel.FindName("LengthFilter");
            Assert(fixtureLengths.Items.Count == 2, "Fixture offers only All and existing two-character length");
            fixtureLengths.SelectedValue = 2;
            Assert((int)fixtureLengths.SelectedValue == 2, "Length selection uses character count, not item position");
            Modal(panel, "关键词规则", dialog =>
            {
                ((TextBox)dialog.FindName("MinimumGroupsBox")).Text = "1";
                Click(dialog, "保存规则");
                Assert(((TextBlock)dialog.FindName("ErrorText")).Text.Contains("2～"), "Invalid input blocked in dialog");
                ((TextBox)dialog.FindName("MinimumGroupsBox")).Text = "7";
                Click(dialog, "保存规则");
            });
            Wait(() => WordButtons(panel).Count() == 0);
            Assert(store.LoadPreferences().Rules.MinimumGroups == 7, "Frequency persisted through UI save");
            Modal(panel, "关键词规则", dialog =>
            {
                Assert(((TextBox)dialog.FindName("MinimumGroupsBox")).Text == "7", "Reopen retains rule value");
                ((TextBox)dialog.FindName("MinimumGroupsBox")).Text = "2";
                ((TextBox)dialog.FindName("ExcludedContainsBox")).Text = "旅行";
                Click(dialog, "保存规则");
            });
            Wait(() => WordButtons(panel).Count() == 1);
            Assert(store.LoadPreferences().Rules.ExcludedContains.SequenceEqual(new[] { "旅行" }), "Contains rule persisted");
            panel.UpdateLayout();
            var keyword = WordButtons(panel).Single(b => b.Tag is string word && word == "北京");
            keyword.ContextMenu!.PlacementTarget = keyword;
            keyword.ContextMenu.IsOpen = true;
            var ignore = (MenuItem)keyword.ContextMenu.Items[0];
            ignore.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            keyword.ContextMenu.IsOpen = false;
            Wait(() => WordButtons(panel).Count() == 0);
            Assert(store.LoadPreferences().Ignored.Contains("北京"), "Context menu ignore wired and persisted");
            Modal(panel, "忽略与恢复", dialog => { var ignoredList = Descendants<ListBox>(dialog).Single(); ignoredList.SelectedItem = ignoredList.Items.Cast<object>().Single(x => x.ToString()!.Contains("北京")); Click(dialog, "恢复选中的关键词"); dialog.Close(); });
            Wait(() => WordButtons(panel).Count() == 1);
            Assert(store.LoadPreferences().Ignored.Count == 0, "Restore control wired and persisted");
            Modal(panel, "关键词规则", dialog =>
            {
                ((TextBox)dialog.FindName("ExcludedContainsBox")).Text = "";
                Click(dialog, "保存规则");
            });
            Wait(() => WordButtons(panel).Count() == 2);
            var query = (TextBox)panel.FindName("KeywordSearch"); query.Text = "北京";
            Wait(() => WordButtons(panel).Count() == 1); query.Text = ""; Wait(() => WordButtons(panel).Count() == 2);
            Wait(() => ((TextBlock)panel.FindName("MatchSummary")).Text.Contains("本地索引匹配"));
            if (dispatcherError is not null) throw dispatcherError;
            panel.UpdateLayout();
            var blockChip = Descendants<Button>(panel).Single(b => b.Tag is string word && word == "北京" && Equals(b.Content, "×"));
            blockChip.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Wait(() => WordButtons(panel).Count() == 1);
            Assert(store.LoadPreferences().Rules.ExcludedWords.Contains("北京"), "Chip X block immediately applied and persisted");
            Modal(panel, "忽略与恢复", dialog => { var blockedList = Descendants<ListBox>(dialog).Single(); var blocked = blockedList.Items.Cast<object>().SingleOrDefault(x => x.ToString()!.Contains("北京")); Assert(blocked is not null, "Chip-blocked word must appear in ignore and restore"); blockedList.SelectedItem = blocked; Click(dialog, "恢复选中的关键词"); dialog.Close(); });
            Wait(() => WordButtons(panel).Count() == 2);
            // Real routed button events must drill down without starting Everything.
            var baselineFileCount = store.LoadIndex(fixture)!.Files.Count;
            ((TextBox)panel.FindName("KeywordSearch")).Text = "北京";
            Wait(() => WordButtons(panel).Count() == 1);
            WordButtons(panel).Single().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Wait(() => WordButtons(panel).Count() == 1 && (string)WordButtons(panel).Single().Tag == "旅行" && ((TextBlock)panel.FindName("IndexSummary")).Text.Contains("6 个文件"));
            Assert(((TextBox)panel.FindName("KeywordSearch")).Text == "", "Entering a level clears keyword search");
            var scopedFormat = (ComboBox)panel.FindName("FormatFilter"); scopedFormat.SelectedValue = "ext:.txt";
            var expectedScopedText = new SearchEngine(store.LoadIndex(fixture)!).Search(new("", ["北京"], ".txt", FileSort.Name, false, 1)).Total;
            Wait(() => ((TextBlock)panel.FindName("IndexSummary")).Text.Contains($"{expectedScopedText} 个文件") && ((TextBlock)panel.FindName("IndexSummary")).Text.Contains(".txt"));
            Assert(((ItemsControl)panel.FindName("SelectedWords")).Items.Count == 1, "Format change preserves committed navigation");
            scopedFormat.SelectedValue = "all";
            Wait(() => ((TextBlock)panel.FindName("IndexSummary")).Text.Contains("6 个文件") && WordButtons(panel).Count() == 1);

            Assert(((TextBlock)panel.FindName("KeywordSummary")).Text.Contains("门槛 2 组"), "Deeper scope defaults to two groups");
            Modal(panel, "关键词规则", dialog => { ((TextBox)dialog.FindName("MinimumGroupsBox")).Text = "3"; Click(dialog, "保存规则"); });
            Assert(store.LoadPreferences().Rules.MinimumGroups == 2 && store.LoadPreferences().DrilldownMinimumGroups == 3, "Child threshold does not modify home threshold");
            WordButtons(panel).Single().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Wait(() => WordButtons(panel).Count() == 0 && ((TextBlock)panel.FindName("EmptyHint")).Text.Contains("没有更多"));
            Assert(((ItemsControl)panel.FindName("SelectedWords")).Items.Count == 2, "Leaf retains all committed conditions");
            Click(panel, "北京");
            Wait(() => WordButtons(panel).Count() == 1 && ((ItemsControl)panel.FindName("SelectedWords")).Items.Count == 1);
            Click(panel, "全部资料");
            Wait(() => WordButtons(panel).Count() == 2 && ((TextBlock)panel.FindName("KeywordSummary")).Text.Contains("门槛 2 组"));
            ((CheckBox)panel.FindName("MultiSelect")).IsChecked = true;
            foreach (var wordButton in WordButtons(panel).ToArray()) wordButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Wait(() => ((Button)panel.FindName("EnterNextLevel")).IsEnabled && ((ItemsControl)panel.FindName("SelectedWords")).Items.Count == 2);
            Assert(WordButtons(panel).Count() == 2 && ((TextBlock)panel.FindName("IndexSummary")).Text.Contains($"{baselineFileCount:N0} 个文件"), "Staged multi-selection leaves current ranking unchanged");
            Click(panel, "进入下一层");
            Wait(() => WordButtons(panel).Count() == 0 && ((TextBlock)panel.FindName("IndexSummary")).Text.Contains("6 个文件"));
            Click(panel, "全部资料");
            Wait(() => WordButtons(panel).Count() == 2);
            ((CheckBox)panel.FindName("MultiSelect")).IsChecked = false;
            Assert(!File.ReadAllText(Path.Combine(store.BaseDirectory, "runtime.log")).Contains("Everything search dispatched"), "Keyword exploration never launches Everything");
            var stressRoot = Path.Combine(fixture, "stress"); Directory.CreateDirectory(stressRoot);
            var stressWords = Enumerable.Range(0, 6000).Select(i => new KeywordCount((i < 3000 ? "例" : "资料") + (char)(0x4e00 + i / 100) + (char)(0x4e00 + i % 100), 6002 - i)).Concat(new[] { new KeywordCount("北京", 4), new KeywordCount("旅行", 4) }).ToList();
            var sampleIndex = new FileIndex(stressRoot, DateTime.UtcNow, [], stressWords, [], 6002) { NameTokenVersion = KeywordTokenizer.CurrentVersion };
            var samplePrefs = store.LoadPreferences(); samplePrefs.Root = sampleIndex.Root; samplePrefs.Rules.MinimumGroups = 2; samplePrefs.Ignored.Clear(); samplePrefs.KeywordLength = 0;
            var stressProfile = manager.Add("压力样本", sampleIndex.Root, samplePrefs); store = manager.StoreFor(stressProfile); store.SaveIndex(sampleIndex);
            var offlineRoot = Path.Combine(fixture, "not-attached");
            var offlineProfile = manager.Add("离线样本", offlineRoot, new ViewerPreferences { Rules = new() { MinimumGroups = 2 } });
            var originalSample = manager.StoreFor(manager.Catalog.Libraries[0]).LoadIndex(fixture)!;
            manager.StoreFor(offlineProfile).SaveIndex(originalSample with { Root = offlineRoot });
            var largePanel = new KeywordViewer.MainWindow(rootStore, sampleIndex.Root); ConfigureTestWindow(largePanel); largePanel.Show();
            Wait(() => ((TextBlock)largePanel.FindName("StatusText")).Text.StartsWith("已加载缓存"));
            Wait(() => ((TextBlock)largePanel.FindName("KeywordSummary")).Text.Contains("/"));
            // Let the search debounce complete before checking all-keyword display.
            var settle = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) }; bool settled = false; settle.Tick += (_, _) => { settled = true; settle.Stop(); }; settle.Start(); Wait(() => settled);

            Assert(WordButtons(largePanel).Count() <= 100, "All keywords must remain paged with at most 100 rendered chips");
            var firstWord = WordButtons(largePanel).First().Tag;
            Click(largePanel, "下一页"); largePanel.UpdateLayout();
            Assert(((TextBlock)largePanel.FindName("PageSummary")).Text.Contains("2 /"), "Page advances");
            var waterfall = (ListBox)largePanel.FindName("InfiniteKeywordList");
            Assert(waterfall.Visibility == Visibility.Visible, "Waterfall is default without checkbox");
            Assert(largePanel.FindName("InfiniteMode") is null, "No waterfall checkbox");
            ((TextBox)largePanel.FindName("KeywordSearch")).Text = "北京";
            Wait(() => waterfall.Items.Count == 1);
            ((TextBox)largePanel.FindName("KeywordSearch")).Text = "";
            Wait(() => ((TextBlock)largePanel.FindName("KeywordSummary")).Text.Contains($"/ {KeywordView.Apply(sampleIndex.Keywords, store.LoadPreferences(), "").Count:N0}"));
            waterfall.UpdateLayout();
            for (int batch = 0; batch < 12; batch++)
            {
                var before = waterfall.Items.Count;
                var waterfallScroll = Descendants<ScrollViewer>(waterfall).First();
                waterfallScroll.ScrollToBottom();
                Wait(() => waterfall.Items.Count > before); waterfall.UpdateLayout();
                bool idle = false; largePanel.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => idle = true)); Wait(() => idle);
            }
            Assert(Descendants<Button>(waterfall).Count() < 1000, "Waterfall virtualizes accumulated pages instead of creating thousands of chips");
            var batchContainer = waterfall.ItemContainerGenerator.ContainerFromIndex(waterfall.Items.Count - 1) as ListBoxItem;
            if (batchContainer is not null) { batchContainer.IsSelected = true; batchContainer.ApplyTemplate(); Assert(!Descendants<Border>(batchContainer).Any(b => b.Name == "Bd"), "Batch has no selection frame"); }
            Click(largePanel, "上一页"); waterfall.UpdateLayout();
            ((ComboBox)largePanel.FindName("LengthFilter")).SelectedValue = 3;
            Wait(() => waterfall.Items.Count == 1 && WordButtons(largePanel).Any() && WordButtons(largePanel).All(b => ((string)b.Tag).Length == 3));
            ((ComboBox)largePanel.FindName("SortField")).SelectedIndex = 2;
            ((ComboBox)largePanel.FindName("SortDirection")).SelectedIndex = 0;
            Assert(store.LoadPreferences().KeywordLength == 3 && store.LoadPreferences().KeywordOrder == KeywordOrder.Length && !store.LoadPreferences().KeywordDescending, "View preferences persisted");
            Assert(largePanel.Icon is not null && Application.Current.TryFindResource("BrandLogo") is DrawingImage, "Logo and window icon loaded");
            var libraryBox = (ComboBox)largePanel.FindName("LibraryBox");
            libraryBox.SelectedValue = manager.Catalog.Libraries[0].Id;
            Wait(() => ((TextBox)largePanel.FindName("RootBox")).Text == fixture && ((TextBlock)largePanel.FindName("StatusText")).Text.StartsWith("已加载缓存"));
            var formats = (ComboBox)largePanel.FindName("FormatFilter"); formats.SelectedValue = "ext:.txt";
            Wait(() => ((TextBlock)largePanel.FindName("IndexSummary")).Text.Contains(".txt"));
            var formatSummary = ((TextBlock)largePanel.FindName("IndexSummary")).Text;
            var fixtureFileCount = manager.StoreFor(manager.Catalog.Libraries[0]).LoadIndex(fixture)!.Files.Count(f => f.Extension == ".txt");
            Assert(formatSummary.Contains($"{fixtureFileCount:N0} 个文件"), "Format filtering updates displayed count: " + formatSummary);
            ((TextBox)largePanel.FindName("KeywordSearch")).Text = "北京";
            Click(largePanel, "重置筛选");
            Wait(() => Equals(formats.SelectedValue, "all") && ((TextBox)largePanel.FindName("KeywordSearch")).Text.Length == 0);
            Assert(((ItemsControl)largePanel.FindName("SelectedWords")).Items.Count == 0, "Reset clears fixed selected words");
            libraryBox.SelectedValue = offlineProfile.Id;
            Wait(() => ((TextBlock)largePanel.FindName("StatusText")).Text.Contains("目录离线") && ((TextBlock)largePanel.FindName("IndexSummary")).Text.Contains("离线缓存"));
            Assert(WordButtons(largePanel).Any(), "Offline library remains browsable from cache");
            if (dispatcherError is not null) throw dispatcherError;
            var mixedRoot = Path.Combine(fixture, "mixed-keyword-library"); Directory.CreateDirectory(mixedRoot);
            var mixedProfile = manager.Add("Mixed keyword fixture", mixedRoot, new ViewerPreferences { Rules = new() { MinimumGroups = 2 } });
            var mixedStore = manager.StoreFor(mixedProfile);
            var legacyMixed = new FileIndex(mixedRoot, DateTime.UtcNow, Enumerable.Range(1, 2).Select(i =>
                new FileEntry(Path.Combine(mixedRoot, $"group{i}", "2026", "high", "01", "a1", "样本.txt"), $@"group{i}\2026\high\01\a1", "样本.txt", ".txt", 1, DateTime.UtcNow, $"group{i}", [])).ToList(), [], [], 2);
            mixedStore.SaveIndex(legacyMixed);
            var mixedPanel = new KeywordViewer.MainWindow(rootStore, mixedRoot); ConfigureTestWindow(mixedPanel); mixedPanel.Show();
            Wait(() => ((TextBlock)mixedPanel.FindName("StatusText")).Text.StartsWith("已加载缓存") && WordButtons(mixedPanel).Any());
            Modal(mixedPanel, "关键词规则", dialog => {
                ((CheckBox)dialog.FindName("ChineseOnlyBox")).IsChecked = false;
                ((CheckBox)dialog.FindName("IncludeNumbersBox")).IsChecked = true;
                Assert(dialog.FindName("MinimumNumericLengthBox") is TextBox, "Numeric rules expose a minimum and maximum length range");
                ((TextBox)dialog.FindName("MaximumNumericLengthBox")).Text = "6";
                Click(dialog, "保存规则");
            });
            Wait(() => new[] { "2026", "high", "01", "a1" }.All(word => WordButtons(mixedPanel).Any(b => Equals(b.Tag, word))));
            Assert(mixedStore.LoadIndex(mixedRoot)!.NameTokenVersion == KeywordTokenizer.CurrentVersion, "Loaded legacy populated cache is upgraded and saved");
            ((TextBox)mixedPanel.FindName("KeywordSearch")).Text = "high";
            Wait(() => WordButtons(mixedPanel).Count() == 1);
            ((TextBox)mixedPanel.FindName("KeywordSearch")).Text = "";
            Wait(() => WordButtons(mixedPanel).Count() > 1);
            WordButtons(mixedPanel).Single(b => Equals(b.Tag, "2026")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Wait(() => WordButtons(mixedPanel).Any(b => Equals(b.Tag, "01")) && WordButtons(mixedPanel).All(b => !Equals(b.Tag, "2026")));
            Modal(mixedPanel, "关键词规则", dialog => {
                Assert(((TextBox)dialog.FindName("MaximumNumericLengthBox")).Text == "6", "Nested rules retain numeric settings");
                ((CheckBox)dialog.FindName("IncludeNumbersBox")).IsChecked = false;
                Click(dialog, "保存规则");
            });
            Wait(() => WordButtons(mixedPanel).All(b => !Equals(b.Tag, "01")));
            Assert(WordButtons(mixedPanel).Any(b => Equals(b.Tag, "high")) && WordButtons(mixedPanel).Any(b => Equals(b.Tag, "a1")), "Hiding pure numbers preserves English and mixed labels");
            Assert(!mixedStore.LoadPreferences().Rules.IncludeNumbers && !mixedStore.LoadPreferences().Rules.ChineseOnly, "Numeric and language preferences saved");
            Click(mixedPanel, "全部资料");
            Wait(() => WordButtons(mixedPanel).Any(b => Equals(b.Tag, "high")));
            Modal(mixedPanel, "关键词规则", dialog => {
                ((CheckBox)dialog.FindName("IncludeNumbersBox")).IsChecked = true;
                ((TextBox)dialog.FindName("MinimumNumericLengthBox")).Text = "7";
                ((TextBox)dialog.FindName("MaximumNumericLengthBox")).Text = "6";
                Click(dialog, "保存规则");
                Assert(((TextBlock)dialog.FindName("ErrorText")).Text.Contains("最短位数"), "Reversed numeric range is rejected");
                ((TextBox)dialog.FindName("MinimumNumericLengthBox")).Text = "4";
                Click(dialog, "保存规则");
            });
            Wait(() => WordButtons(mixedPanel).Any(b => Equals(b.Tag, "2026")) && WordButtons(mixedPanel).All(b => !Equals(b.Tag, "01")));
            Assert(WordButtons(mixedPanel).Any(b => Equals(b.Tag, "high")) && WordButtons(mixedPanel).Any(b => Equals(b.Tag, "a1")), "Numeric range preserves nonnumeric words");
            Assert(mixedStore.LoadPreferences().Rules.MinimumNumericLength == 4 && mixedStore.LoadPreferences().Rules.MaximumNumericLength == 6, "Numeric range persists");
            Modal(mixedPanel, "关键词规则", dialog => {
                Assert(((TextBox)dialog.FindName("MinimumNumericLengthBox")).Text == "4" && ((TextBox)dialog.FindName("MaximumNumericLengthBox")).Text == "6", "Reopened rules retain both numeric bounds");
                dialog.Close();
            });
            mixedPanel.Close();
            var singleRoot = Path.Combine(verification, "single-group-search");
            var singleStore = new ViewerStore(Path.Combine(verification, "single-search-ui-" + Guid.NewGuid().ToString("N")));
            singleStore.SavePreferences(new() { Root = singleRoot, Rules = new() { MinimumGroups = 2 } });
            var singleIndex = new IndexScanner(Path.Combine(AppContext.BaseDirectory, "Resources")).Scan(singleRoot, default);
            singleStore.SaveIndex(singleIndex with { Keywords = [], NameTokenVersion = 2 });
            var singlePanel = new KeywordViewer.MainWindow(singleStore, singleRoot);
            ConfigureTestWindow(singlePanel); singlePanel.Show();
            Wait(() => ((TextBlock)singlePanel.FindName("StatusText")).Text.StartsWith("已加载缓存"));
            Assert(!WordButtons(singlePanel).Any(), "Single-group words stay out of the default ranking");
            var singleQuery = (TextBox)singlePanel.FindName("KeywordSearch"); singleQuery.Text = "一对一";
            Wait(() => WordButtons(singlePanel).SingleOrDefault()?.Tag is string word && word == "一对一");
            Assert(((TextBlock)singlePanel.FindName("KeywordSummary")).Text.Contains("搜索含 1 组词"), "Search explains the relaxed frequency threshold");
            WordButtons(singlePanel).Single().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Wait(() => ((TextBlock)singlePanel.FindName("IndexSummary")).Text.Contains("2 个文件"));
            Assert(singleQuery.Text == "", "Selecting a searched single-group word enters its file scope");
            singleQuery.Text = "摄影";
            Wait(() => WordButtons(singlePanel).SingleOrDefault()?.Tag is string word && word == "摄影");
            var singleFormat = (ComboBox)singlePanel.FindName("FormatFilter"); singleFormat.SelectedValue = "ext:.jpg";
            Wait(() => ((TextBlock)singlePanel.FindName("IndexSummary")).Text.Contains("1 个文件") && WordButtons(singlePanel).Count() == 1);
            singleQuery.Text = ""; Wait(() => !WordButtons(singlePanel).Any());
            singlePanel.Close();
            largePanel.Close();
            var passed = new { Passed = true, Cases = new[] { "isolated WPF cache load", "wrapping keyword layout", "invalid rule input", "frequency rule save and reopen", "contains exclusion save", "context-menu ignore", "restore ignored words", "restore rule-excluded words", "keyword search", "chip X block and unified restoration", "default waterfall without mode checkbox", "waterfall scroll appends 12 pages with virtualized controls", "sorting, length filter, persistence and branding", "library switch and format file count", "reset filters and fixed selected words", "offline cache browsing", "hierarchical routed clicks, inherited file scope, leaf and breadcrumb return", "independent child threshold and staged AND multi-selection", "no automatic Everything launch during keyword exploration", "legacy mixed-token cache upgrade, language and numeric rule controls, search and hierarchical filtering", "numeric minimum-maximum range, invalid input, persistence and reopening" }, Store = store.BaseDirectory };
            File.WriteAllText(Path.Combine(verification, "ui-checks.json"), JsonSerializer.Serialize(passed, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine(JsonSerializer.Serialize(passed));
            panel.Close(); app.Shutdown(); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); panel.Close(); app.Shutdown(); return 1; }
    }
    private sealed class TestApplication : Application
    {
        // Use the real resources without starting another real user session or single-instance guard.
        protected override void OnStartup(StartupEventArgs e) { }
    }
    private static void ConfigureTestWindow(Window window)
    {
        // Keep fixture windows away from the user's desktop; the formal application is inspected separately.
        window.ShowInTaskbar = false; window.ShowActivated = false; window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -30000; window.Top = -30000;
    }
    private static IEnumerable<Button> WordButtons(Window panel) { panel.UpdateLayout(); return Descendants<Button>((ListBox)panel.FindName("InfiniteKeywordList")).Where(b => b.Tag is string && b.ContextMenu is not null); }
    private static void Modal(Window panel, string title, Action<Window> action)
    {
        Exception? error = null;
        panel.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            Window? dialog = null;
            try { dialog = Application.Current.Windows.OfType<Window>().Single(w => w.Title == title); action(dialog); }
            catch (Exception ex) { error = ex; dialog?.Close(); }
        }));
        Click(panel, title);
        if (error is not null) throw error;
    }
    private static void Click(Window window, string content)
    {
        var button = Descendants<Button>(window).Single(b => Equals(b.Content, content));
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
    private static void Wait(Func<bool> predicate)
    {
        if (predicate()) return;
        var frame = new DispatcherFrame();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        timer.Tick += (_, _) => { if (predicate() || watch.Elapsed.TotalSeconds > 10) frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame); timer.Stop();
        Assert(predicate(), "Timed out waiting for UI state");
    }
    private static void Assert(bool value, string label) { if (!value) throw new InvalidOperationException(label); }
}
