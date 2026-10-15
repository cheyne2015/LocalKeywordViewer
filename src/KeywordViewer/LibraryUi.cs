using System.IO;
using System.Windows;
using System.Windows.Controls;
using KeywordViewer.Core;
namespace KeywordViewer;

public partial class MainWindow
{
    private void SyncLibraryBox()
    {
        var previous = switchingLibrary; switchingLibrary = true;
        LibraryBox.ItemsSource = libraries.Catalog.Libraries.ToList(); LibraryBox.SelectedValue = libraries.Catalog.ActiveId;
        switchingLibrary = previous;
    }
    private async void Library_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!ready || switchingLibrary || LibraryBox.SelectedItem is not LibraryProfile library || library.Id == libraries.Catalog.ActiveId) return;
        LibraryBox.IsEnabled = false;
        try
        {
            scanCancellation?.Cancel(); if (activeScan is not null) await activeScan;
            libraries.Activate(library.Id); await LoadCurrentLibraryAsync();
        }
        catch (Exception ex) { StatusText.Text = "资料库切换失败：" + ex.Message; SyncLibraryBox(); }
        finally { LibraryBox.IsEnabled = true; }
    }
    private async Task LoadCurrentLibraryAsync()
    {
        switchingLibrary = true; LibraryBox.IsEnabled = false; RefreshButton.IsEnabled = false;
        formatCancellation?.Cancel(); formatVersion++; matchCancellation?.Cancel(); matchingVersion++;
        index = null; fullIndex = null; engine = null; keywordBatches.Clear(); selected.Clear(); SelectedWords.ItemsSource = null;
        pendingEverythingQuery = null;
        KeywordSummary.Text = "等待索引"; IndexSummary.Text = "当前资料库尚无可用索引";
        EmptyHint.Visibility = Visibility.Visible; EmptyHint.Text = "正在准备资料库…";
        configuringFormat = true; FormatFilter.ItemsSource = null; configuringFormat = false;
        store = libraries.StoreFor(libraries.Active); preferences = store.LoadPreferences();
        var root = libraries.Active.Root; RootBox.Text = root;
        ready = false; KeywordSearch.Text = ""; ExtraSearch.Text = ""; MultiSelect.IsChecked = false;
        SortField.SelectedIndex = Math.Clamp((int)preferences.KeywordOrder, 0, 2); SortDirection.SelectedIndex = preferences.KeywordDescending ? 1 : 0;
        ready = true; keywordPage = 1; resetWaterfall = true; OpenEverythingButton.IsEnabled = false; IssuesButton.IsEnabled = false;
        SelectionSummary.Text = "尚未选择关键词"; MatchSummary.Text = "正在加载资料库…";
        try
        {
            if (root.Length == 0) { StatusText.Text = "请在资料库与设置中为该资料库选择目录。"; EmptyHint.Text = "请先选择该资料库的目录"; return; }
            StatusText.Text = "正在加载本地缓存并准备格式索引…";
            var cached = await Task.Run(() => store.LoadIndex(root));
            if (cached is not null)
            {
                await ApplyIndexAsync(cached);
                StatusText.Text = $"已加载缓存 · {cached.Files.Count:N0} 个文件；{(Directory.Exists(root) ? "新增或删除后请刷新" : "目录离线，可浏览缓存，暂时无法打开原文件")}";
                SafeLog($"Cache loaded: files={cached.Files.Count}, groups={cached.GroupCount}");
            }
            else if (Directory.Exists(root)) await StartScanAsync();
            else { StatusText.Text = "目录或磁盘不可用，且尚无缓存；恢复连接后刷新。"; MatchSummary.Text = "没有可用索引"; EmptyHint.Text = "目录离线，尚无可用缓存"; }
        }
        catch (Exception ex) { StatusText.Text = "资料库未能加载，原缓存保持不变：" + ex.Message; SafeLog("Library load failed: " + ex.Message); }
        finally { switchingLibrary = false; LibraryBox.IsEnabled = true; RefreshButton.IsEnabled = root.Length > 0; }
    }
    private async void Libraries_Click(object sender, RoutedEventArgs e)
    {
        if (switchingLibrary || scanCancellation is not null) { StatusText.Text = "请先等待加载完成，或取消当前扫描后管理资料库。"; return; }
        var dialog = new Window { Title = "资料库与设置", Width = 600, Height = 620, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var list = new ListBox { Height = 160, DisplayMemberPath = "Name", ItemsSource = libraries.Catalog.Libraries.ToList(), SelectedValuePath = "Id", SelectedValue = libraries.Catalog.ActiveId };
        var name = new TextBox { Margin = new Thickness(0, 6, 0, 6) };
        var root = new TextBox { Margin = new Thickness(0, 6, 0, 6) };
        var message = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = System.Windows.Media.Brushes.Firebrick, Margin = new Thickness(0, 8, 0, 8) };
        var paths = new CheckBox { Content = "导出时包含本机路径（个人备份使用；分享时不要勾选）", Margin = new Thickness(0, 12, 0, 12) };
        var body = new StackPanel { Margin = new Thickness(16) };
        body.Children.Add(new TextBlock { Text = "每个资料库对应一个根目录，缓存、屏蔽词和筛选各自保存。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) });
        body.Children.Add(list); body.Children.Add(new TextBlock { Text = "名称" }); body.Children.Add(name); body.Children.Add(new TextBlock { Text = "目录（导入的无路径设置可稍后指定）" }); body.Children.Add(root);
        var actions = new WrapPanel(); body.Children.Add(actions);
        var pick = Button("选择目录", () => { var folder = new Microsoft.Win32.OpenFolderDialog { Title = "选择资料库根目录" }; if (folder.ShowDialog(dialog) == true) { root.Text = folder.FolderName; if (name.Text.Length == 0) name.Text = Path.GetFileName(folder.FolderName); } });
        var add = Button("新增资料库", () => { var item = libraries.Add(name.Text, root.Text); list.ItemsSource = libraries.Catalog.Libraries.ToList(); list.SelectedValue = item.Id; message.Text = "已新增。关闭此窗口后可从主界面切换。"; });
        var save = Button("保存修改", () => { if (list.SelectedItem is LibraryProfile item) { var changed = libraries.Update(item.Id, name.Text, root.Text); list.ItemsSource = libraries.Catalog.Libraries.ToList(); list.SelectedValue = changed.Id; message.Text = "已保存。"; } });
        var remove = Button("移除资料库", () => { if (list.SelectedItem is LibraryProfile item && MessageBox.Show(dialog, "仅移除资料库缓存和偏好，不删除原文件。继续？", "移除资料库", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes) { libraries.Remove(item.Id); list.ItemsSource = libraries.Catalog.Libraries.ToList(); list.SelectedValue = libraries.Catalog.ActiveId; } });
        actions.Children.Add(pick); actions.Children.Add(add); actions.Children.Add(save); actions.Children.Add(remove);
        body.Children.Add(paths); var transfers = new WrapPanel(); body.Children.Add(transfers);
        transfers.Children.Add(Button("导出设置", () => { var file = new Microsoft.Win32.SaveFileDialog { Filter = "设置文件|*.json", FileName = "settings-export.json" }; if (file.ShowDialog(dialog) == true) { libraries.Export(file.FileName, paths.IsChecked == true); message.Text = "已导出设置，不含文件索引。"; } }));
        transfers.Children.Add(Button("导入设置", () => { var file = new Microsoft.Win32.OpenFileDialog { Filter = "设置文件|*.json" }; if (file.ShowDialog(dialog) == true) { var count = libraries.Import(file.FileName); list.ItemsSource = libraries.Catalog.Libraries.ToList(); list.SelectedValue = libraries.Catalog.ActiveId; message.Text = $"已导入 {count} 个资料库；原有资料库未覆盖。"; } }));
        body.Children.Add(message); body.Children.Add(Button("使用选中资料库并关闭", () => { if (list.SelectedItem is LibraryProfile item) libraries.Activate(item.Id); dialog.Close(); }));
        list.SelectionChanged += (_, _) => { if (list.SelectedItem is LibraryProfile item) { name.Text = item.Name; root.Text = item.Root; } };
        if (list.SelectedItem is LibraryProfile initial) { name.Text = initial.Name; root.Text = initial.Root; }
        dialog.Content = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        dialog.ShowDialog(); SyncLibraryBox(); await LoadCurrentLibraryAsync();
        Button Button(string label, Action action)
        {
            var button = new Button { Content = label, Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(3) };
            button.Click += (_, _) => { try { action(); } catch (Exception ex) { message.Text = ex.Message; } }; return button;
        }
    }
}
