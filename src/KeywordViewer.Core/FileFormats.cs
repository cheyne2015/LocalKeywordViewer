using System.Text.RegularExpressions;
namespace KeywordViewer.Core;

public sealed record FileFormatOption(string Key, string Label, string[] Extensions)
{
    public bool Accepts(FileEntry file) => Key == "all" || Extensions.Contains(file.Extension, StringComparer.OrdinalIgnoreCase);
}
public static class FileFormats
{
    private static readonly (string Key, string Label, string Extensions)[] Categories =
    [ ("video", "视频", ".mp4 .mkv .avi .mov .wmv .flv .webm .ts .m4v .rmvb .mpg .mpeg .m2ts"),
      ("image", "图片", ".jpg .jpeg .png .gif .webp .bmp .tif .tiff .heic .avif .svg"),
      ("audio", "音频", ".mp3 .flac .wav .aac .m4a .ogg .opus .wma .ape"),
      ("document", "文档", ".pdf .txt .doc .docx .xls .xlsx .ppt .pptx .md .rtf .epub .csv .html .htm"),
      ("archive", "压缩包", ".zip .rar .7z .tar .gz .bz2 .xz .iso") ];
    public static List<FileFormatOption> Options(FileIndex index)
    {
        var extensions = index.Files.Select(f => f.Extension.ToLowerInvariant()).Distinct().Order().ToList();
        var options = new List<FileFormatOption> { new("all", "全部格式", []) };
        foreach (var c in Categories)
        {
            var matches = extensions.Intersect(c.Extensions.Split(' '), StringComparer.OrdinalIgnoreCase).ToArray();
            if (matches.Length > 0) options.Add(new(c.Key, c.Label, matches));
        }
        foreach (var ext in extensions.Where(e => Regex.IsMatch(e, @"^\.[\p{L}\p{N}_+-]+$"))) options.Add(new("ext:" + ext, ext, [ext]));
        if (extensions.Contains("")) options.Add(new("extensionless", "无扩展名", [""]));
        return options;
    }
    public static FileIndex EnsureKeywords(FileIndex index, string resources, CancellationToken token)
    {
        if (index.Files.All(f => f.Keywords is not null)) return index;
        var tokenizer = new KeywordTokenizer(resources);
        var directories = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var names = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var files = new List<FileEntry>();
        foreach (var file in index.Files)
        {
            token.ThrowIfCancellationRequested();
            if (file.Keywords is not null) { files.Add(file); continue; }
            if (!directories.TryGetValue(file.RelativeDirectory, out var inherited))
            {
                inherited = new(StringComparer.OrdinalIgnoreCase);
                foreach (var part in file.RelativeDirectory.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries)) inherited.UnionWith(tokenizer.Extract(part));
                directories[file.RelativeDirectory] = inherited;
            }
            if (!names.TryGetValue(file.Name, out var own)) names[file.Name] = own = tokenizer.Extract(file.Name, true);
            var words = new HashSet<string>(inherited, StringComparer.OrdinalIgnoreCase); words.UnionWith(own);
            files.Add(file with { Keywords = words.ToList() });
        }
        return index with { Files = files };
    }
    public static FileIndex Select(FileIndex index, FileFormatOption option, CancellationToken token)
    {
        if (option.Key == "all") return index;
        var files = index.Files.Where(option.Accepts).ToList();
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var groups = files.GroupBy(f => f.GroupKey).ToList();
        foreach (var group in groups)
        {
            token.ThrowIfCancellationRequested();
            var words = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in group) words.UnionWith(file.Keywords ?? throw new InvalidOperationException("名称索引尚未升级。"));
            foreach (var word in words) counts[word] = counts.GetValueOrDefault(word) + 1;
        }
        return index with { Files = files, GroupCount = groups.Count, Keywords = counts.Where(k => k.Value >= 2).Select(k => new KeywordCount(k.Key, k.Value)).OrderByDescending(k => k.Groups).ThenBy(k => k.Word).ToList() };
    }
    public static string EverythingClause(FileFormatOption option)
    {
        if (option.Key == "all") return "";
        if (option.Key == "extensionless") return " ext:";
        if (option.Extensions.Any(e => !Regex.IsMatch(e, @"^\.[\p{L}\p{N}_+-]+$"))) throw new ArgumentException("文件格式不支持搜索。");
        return " ext:" + string.Join(';', option.Extensions.Select(e => e[1..]));
    }
}
