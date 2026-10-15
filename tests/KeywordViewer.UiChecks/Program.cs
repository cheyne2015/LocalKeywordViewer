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
        var app = new KeywordViewer.App(); app.InitializeComponent(); app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        // Main normally runs under Application.Run; preserve its dispatcher context for async handlers.
        System.Threading.SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        Exception? dispatcherError = null;
        app.DispatcherUnhandledException += (_, e) => { dispatcherError = e.Exception; e.Handled = true; };
        var panel = new KeywordViewer.MainWindow(rootStore, fixture); app.MainWindow = panel;
        var manager = new LibraryManager(rootStore); store = manager.StoreFor(manager.Active);
        try
        {
            panel.Show();
            Wait(() => ((TextBlock)panel.FindName("StatusText")).Text.StartsWith("已加载缓存"));

            Assert(WordButtons(panel).Count() == 2, "Fixture keywords visible");
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
            var stressRoot = Path.Combine(fixture, "stress"); Directory.CreateDirectory(stressRoot);
            var stressWords = Enumerable.Range(0, 6000).Select(i => new KeywordCount((i < 3000 ? "例" : "资料") + (char)(0x4e00 + i / 100) + (char)(0x4e00 + i % 100), 6002 - i)).Concat(new[] { new KeywordCount("北京", 4), new KeywordCount("旅行", 4) }).ToList();
            var sampleIndex = new FileIndex(stressRoot, DateTime.UtcNow, [], stressWords, [], 6002);
            var samplePrefs = store.LoadPreferences(); samplePrefs.Root = sampleIndex.Root; samplePrefs.Rules.MinimumGroups = 2; samplePrefs.Ignored.Clear(); samplePrefs.KeywordLength = 0;
            var stressProfile = manager.Add("压力样本", sampleIndex.Root, samplePrefs); store = manager.StoreFor(stressProfile); store.SaveIndex(sampleIndex);
            var largePanel = new KeywordViewer.MainWindow(rootStore, sampleIndex.Root); largePanel.Show();
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
            Assert(((TextBlock)largePanel.FindName("IndexSummary")).Text.Contains("7 个文件"), "Format filtering updates displayed count");
            ((TextBox)largePanel.FindName("KeywordSearch")).Text = "北京";
            Click(largePanel, "重置筛选");
            Wait(() => Equals(formats.SelectedValue, "all") && ((TextBox)largePanel.FindName("KeywordSearch")).Text.Length == 0);
            Assert(((ItemsControl)largePanel.FindName("SelectedWords")).Items.Count == 0, "Reset clears fixed selected words");
            if (dispatcherError is not null) throw dispatcherError;
            largePanel.Close();
            var passed = new { Passed = true, Cases = new[] { "real WPF cache load", "wrapping keyword layout", "invalid rule input", "frequency rule save and reopen", "contains exclusion save", "context-menu ignore", "restore ignored words", "restore rule-excluded words", "keyword search", "chip X block and unified restoration", "default waterfall without mode checkbox", "waterfall scroll appends 12 pages with virtualized controls", "sorting, length filter, persistence and branding" }, Store = store.BaseDirectory };
            File.WriteAllText(Path.Combine(verification, "ui-checks.json"), JsonSerializer.Serialize(passed, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine(JsonSerializer.Serialize(passed));
            panel.Close(); app.Shutdown(); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); panel.Close(); app.Shutdown(); return 1; }
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
