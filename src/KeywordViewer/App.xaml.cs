using System.IO;
using System.Text.Json;
using System.Windows;
using KeywordViewer.Core;

namespace KeywordViewer;
public partial class App : Application
{
    private SingleInstanceGuard? instance;
    protected override void OnExit(ExitEventArgs e) { instance?.Dispose(); base.OnExit(e); }
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        string? Argument(string key) { var i = Array.IndexOf(e.Args, key); return i >= 0 && i + 1 < e.Args.Length ? e.Args[i + 1] : null; }
        var store = new ViewerStore(Argument("--data"));
        DispatcherUnhandledException += (_, error) =>
        {
            try { store.Log("Unhandled: " + error.Exception); } catch { }
            MessageBox.Show("操作未完成：" + error.Exception.Message, "文件关键词查看器", MessageBoxButton.OK, MessageBoxImage.Error);
            error.Handled = true;
        };
        // Runs the exact packaged assemblies and dictionary without opening any original file.
        if (Argument("--scan-report") is string report)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try
            {
                var started = System.Diagnostics.Stopwatch.StartNew();
                var index = await Task.Run(() => new IndexScanner(Path.Combine(AppContext.BaseDirectory, "Resources")).Scan(Argument("--root") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"), default));
                await Task.Run(() => store.SaveIndex(index));
                File.WriteAllText(report, JsonSerializer.Serialize(new { index.Root, Files = index.Files.Count, Groups = index.GroupCount, Keywords = index.Keywords.Count, Issues = index.Issues.Count, Seconds = started.Elapsed.TotalSeconds, AssemblyPath = Environment.ProcessPath }));
                store.Log($"Packaged scan: files={index.Files.Count}, groups={index.GroupCount}, keywords={index.Keywords.Count}, skipped={index.Issues.Count}");
                Shutdown(0);
            }
            catch (Exception ex) { File.WriteAllText(report, JsonSerializer.Serialize(new { Error = ex.Message })); Shutdown(1); }
            return;
        }
        instance = new SingleInstanceGuard(store.BaseDirectory, () => Dispatcher.BeginInvoke(new Action(() =>
        {
            if (MainWindow is null) return;
            if (MainWindow.WindowState == WindowState.Minimized) MainWindow.WindowState = WindowState.Normal;
            MainWindow.Show(); MainWindow.Activate();
        })));
        if (!instance.IsFirst) { Shutdown(); return; }
        var window = new MainWindow(store, Argument("--root"));
        MainWindow = window;
        window.Show();
    }
}
