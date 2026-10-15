using System.Diagnostics;

namespace KeywordViewer.Core;

public sealed class IndexScanner(string resources)
{
    public FileIndex Scan(string root, CancellationToken cancellation, IProgress<ScanProgress>? progress = null)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("扫描目录不存在或磁盘不可用：" + root);
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) throw new IOException("扫描根目录是目录链接，请选择实际目录。");
        cancellation.ThrowIfCancellationRequested();
        var tokenizer = new KeywordTokenizer(resources);
        var files = new List<FileEntry>();
        var issues = new List<ScanIssue>();
        var groups = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var names = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var pending = new Stack<(string Path, string Group, HashSet<string> Inherited)>();
        pending.Push((root, "", new(StringComparer.OrdinalIgnoreCase)));
        var dirs = 0;
        var clock = Stopwatch.StartNew();
        long lastUpdate = 0;
        while (pending.TryPop(out var dir))
        {
            cancellation.ThrowIfCancellationRequested();
            dirs++;
            IEnumerator<string>? enumerator = null;
            try
            {
                // Attribute checks occur before descending into links; inaccessible children remain explicit issues.
                enumerator = Directory.EnumerateFileSystemEntries(dir.Path).GetEnumerator();
                while (enumerator.MoveNext())
                {
                    cancellation.ThrowIfCancellationRequested();
                    var path = enumerator.Current;
                    try
                    {
                        var attrs = File.GetAttributes(path);
                        if ((attrs & FileAttributes.ReparsePoint) != 0) { issues.Add(new(path, "跳过文件或目录链接")); continue; }
                        if ((attrs & FileAttributes.Directory) != 0)
                        {
                            var inherited = new HashSet<string>(dir.Inherited, StringComparer.OrdinalIgnoreCase);
                            inherited.UnionWith(Tokens(Path.GetFileName(path), false));
                            pending.Push((path, dir.Group.Length == 0 ? Path.GetFileName(path) : dir.Group, inherited));
                        }
                        else
                        {
                            var info = new FileInfo(path);
                            var relativeDir = Path.GetRelativePath(root, dir.Path);
                            if (relativeDir == ".") relativeDir = "";
                            // Prefixes separate standalone root files from identically named groups.
                            var groupKey = dir.Group.Length == 0 ? "file:" + info.Name : "dir:" + dir.Group;
                            files.Add(new(path, relativeDir, info.Name, info.Extension.ToLowerInvariant(), info.Length, info.LastWriteTimeUtc, groupKey));
                            if (!groups.TryGetValue(groupKey, out var groupWords)) groups[groupKey] = groupWords = new(StringComparer.OrdinalIgnoreCase);
                            groupWords.UnionWith(dir.Inherited);
                            groupWords.UnionWith(Tokens(info.Name, true));
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                    { issues.Add(new(path, ex.Message)); }
                    if (clock.ElapsedMilliseconds - lastUpdate > 200)
                    { progress?.Report(new(files.Count, dirs, issues.Count)); lastUpdate = clock.ElapsedMilliseconds; }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                if (dir.Path == root) throw new IOException("无法读取扫描根目录。", ex);
                issues.Add(new(dir.Path, ex.Message));
            }
            finally { enumerator?.Dispose(); }
        }
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var words in groups.Values)
        {
            cancellation.ThrowIfCancellationRequested();
            foreach (var word in words) counts[word] = counts.GetValueOrDefault(word) + 1;
        }
        var keywords = counts.Where(x => x.Value >= 2).Select(x => new KeywordCount(x.Key, x.Value))
            .OrderByDescending(x => x.Groups).ThenBy(x => x.Word, StringComparer.OrdinalIgnoreCase).ToList();
        progress?.Report(new(files.Count, dirs, issues.Count));
        return new(root, DateTime.UtcNow, files, keywords, issues, groups.Count);

        HashSet<string> Tokens(string name, bool isFile)
        {
            var key = (isFile ? "f:" : "d:") + name;
            if (!names.TryGetValue(key, out var tokens)) names[key] = tokens = tokenizer.Extract(name, isFile);
            return tokens;
        }
    }
}
