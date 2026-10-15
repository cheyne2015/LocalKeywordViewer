using System.Diagnostics;
using Microsoft.Win32;
using System.Runtime.InteropServices;

namespace KeywordViewer.Core;
public sealed record EverythingInfo(string Path, string Version);

public static class EverythingBridge
{
    private static readonly SemaphoreSlim StartupLock = new(1, 1);
    public static async Task ShowAsync(string executable, string query, CancellationToken token = default)
    {
        await StartupLock.WaitAsync(token);
        try
        {
            var runtime = ResolveSearchExecutable(executable);
            if (!IsRunning())
            {
                var bootstrap = IsPortableLauncher(executable) ? executable : ShortcutTargets().FirstOrDefault(IsPortableLauncher);
                if (bootstrap is not null)
                {
                    await Task.Run(() => { using var launched = Process.Start(new ProcessStartInfo(bootstrap) { UseShellExecute = true }) ?? throw new IOException("Everything 启动器未能启动。"); }, token);
                    var deadline = DateTime.UtcNow.AddSeconds(60);
                    while (DateTime.UtcNow < deadline)
                    {
                        token.ThrowIfCancellationRequested();
                        await Task.Delay(300, token);
                        runtime = ResolveSearchExecutable(bootstrap);
                        if (!IsPortableLauncher(runtime) && File.Exists(runtime) && IsRunning()) break;
                    }
                    if (IsPortableLauncher(runtime) || !IsRunning()) throw new TimeoutException("等待 Everything 启动超时；若有管理员确认窗口，请手动确认后重试。");
                }
            }
            using var process = Process.Start(StartInfo(runtime, query)) ?? throw new IOException("Everything 未能启动。");
        }
        finally { StartupLock.Release(); }
    }
    private static bool IsRunning()
    {
        var processes = Process.GetProcessesByName("Everything");
        foreach (var process in processes) process.Dispose();
        return processes.Length > 0;
    }
    private static bool IsPortableLauncher(string path)
    {
        try { return File.Exists(path) && FileVersionInfo.GetVersionInfo(path).FileDescription == "Everyone Lite"; }
        catch (IOException) { return false; }
    }
    public static EverythingInfo SelectProgram(string path)
    {
        path = Path.GetFullPath(path.Trim().Trim('"'));
        if (!File.Exists(path)) throw new FileNotFoundException("所选 Everything 程序不存在或磁盘不可用。", path);
        var version = FileVersionInfo.GetVersionInfo(path);
        if (!path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !IsEverythingProgram(version)) throw new ArgumentException("所选文件不是可识别的 Everything 程序或便携启动器。");
        return new(path, version.FileVersion ?? "未知版本");
    }
    public static EverythingInfo? Discover(string? preferred = null)
    {
        var paths = new List<string>();
        if (!string.IsNullOrWhiteSpace(preferred)) paths.Add(preferred.Trim().Trim('"'));
        // The installed portable launcher extracts here on this machine.
        paths.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Everything", "Everything.exe"));
        if (OperatingSystem.IsWindows())
        {
            foreach (var process in Process.GetProcessesByName("Everything"))
            {
                using (process) { try { if (process.MainModule?.FileName is string path) paths.Add(path); } catch { } }
            }
            foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
            {
                using var key = hive.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\App Paths\Everything.exe");
                if (key?.GetValue(null) is string path) paths.Add(path.Trim('"'));
            }
        }
        paths.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Everything", "Everything.exe"));
        paths.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Everything", "Everything.exe"));
        if (OperatingSystem.IsWindows()) paths.AddRange(ShortcutTargets());
        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(path)) continue;
            var version = FileVersionInfo.GetVersionInfo(path);
            if (!IsEverythingProgram(version)) continue;
            return new(Path.GetFullPath(path), version.FileVersion ?? "未知版本");
        }
        return null;
    }
    public static bool IsEverythingProgram(FileVersionInfo version) =>
        version.FileDescription?.Contains("Everything", StringComparison.OrdinalIgnoreCase) == true ||
        (version.FileDescription == "Everyone Lite" && Path.GetFileName(version.FileName).Contains("Everything", StringComparison.OrdinalIgnoreCase));
    private static IEnumerable<string> ShortcutTargets()
    {
        var targets = new List<string>();
        if (!OperatingSystem.IsWindows()) return targets;
        object? shell = null;
        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type is null) return targets;
            shell = Activator.CreateInstance(type);
            foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Environment.GetFolderPath(Environment.SpecialFolder.Programs) })
            {
                if (!Directory.Exists(root)) continue;
                foreach (var link in Directory.EnumerateFiles(root, "*Everything*.lnk", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 3 }))
                {
                    object? shortcut = null;
                    try { shortcut = ((dynamic)shell!).CreateShortcut(link); var target = (string)((dynamic)shortcut).TargetPath; if (target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) targets.Add(target); }
                    catch { }
                    finally { if (shortcut is not null && Marshal.IsComObject(shortcut)) Marshal.FinalReleaseComObject(shortcut); }
                }
            }
        }
        catch { }
        finally { if (shell is not null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell); }
        return targets;
    }

    public static string BuildQuery(string root, IEnumerable<string> words, string text = "")
    {
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        // Per-term modifiers preserve Everything's existing Match Path/Case/Whole Word settings.
        var terms = new List<string> { "file:", Literal(normalized) };
        foreach (var word in words.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase)) terms.Add(Literal(word));
        if (!string.IsNullOrWhiteSpace(text)) terms.Add(Literal(text.Trim()));
        return string.Join(" ", terms);
    }
    private static string Literal(string value)
    {
        // A Windows filename cannot contain these characters. Reject them instead of allowing search operators.
        if (value.IndexOfAny(['"', '\r', '\n', '\0', '*', '?', '<', '>', '|']) >= 0) throw new ArgumentException("搜索文字不能包含引号、换行或 * ? < > |；请用文件名中的普通文字搜索。");
        return "path:nocase:nowholeword:noregex:\"" + value + "\"";
    }
    public static ProcessStartInfo StartInfo(string executable, string query)
    {
        executable = ResolveSearchExecutable(executable);
        if (!File.Exists(executable)) throw new FileNotFoundException("Everything 程序已移动或不可用，请重新选择程序。", executable);
        // Everything 1.4 parses triple quotes as a literal quote; avoid shell commands and shell associations.
        // Searching must never relaunch the elevating portable wrapper.
        if (FileVersionInfo.GetVersionInfo(executable).FileDescription == "Everyone Lite")
            throw new InvalidOperationException("Everything 尚未就绪，请先手动打开原 Everything 一次，再点击关键词；搜索不会反复启动管理员确认。");
        return new(executable) { UseShellExecute = false, Arguments = "-search \"" + query.Replace("\"", "\"\"\"") + "\"" };
    }
    public static string ResolveSearchExecutable(string executable)
    {
        executable = Path.GetFullPath(executable.Trim().Trim('"'));
        if (!File.Exists(executable)) return executable;
        if (FileVersionInfo.GetVersionInfo(executable).FileDescription != "Everyone Lite") return executable;
        var runtime = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Everything", "Everything.exe");
        if (File.Exists(runtime) && FileVersionInfo.GetVersionInfo(runtime).FileDescription?.Contains("Everything", StringComparison.OrdinalIgnoreCase) == true)
            return runtime;
        return executable;
    }
    public static void Show(string executable, string query)
    {
        using var process = Process.Start(StartInfo(executable, query)) ?? throw new IOException("Everything 未能启动。");
    }
}
