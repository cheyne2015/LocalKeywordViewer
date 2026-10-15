using System.Text.Json.Serialization;

namespace KeywordViewer.Core;

public sealed record FileEntry(string FullPath, string RelativeDirectory, string Name, string Extension,
    long Size, DateTime ModifiedUtc, string GroupKey)
{
    [JsonIgnore] public string SearchText => string.IsNullOrEmpty(RelativeDirectory) ? Name : RelativeDirectory + "\\" + Name;
    [JsonIgnore] public string DirectoryDisplay => string.IsNullOrEmpty(RelativeDirectory) ? "（根目录）" : RelativeDirectory;
    [JsonIgnore] public string SizeDisplay => Size >= 1073741824 ? $"{Size / 1073741824d:F2} GB" : Size >= 1048576 ? $"{Size / 1048576d:F1} MB" : Size >= 1024 ? $"{Size / 1024d:F1} KB" : $"{Size} B";
}
public sealed record KeywordCount(string Word, int Groups);
public sealed record ScanIssue(string Path, string Reason);
public sealed record ScanProgress(int Files, int Directories, int Skipped);
public sealed record FileIndex(string Root, DateTime BuiltUtc, List<FileEntry> Files,
    List<KeywordCount> Keywords, List<ScanIssue> Issues, int GroupCount, int Version = 2);
public sealed class ViewerPreferences
{
    public string Root { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    public string EverythingPath { get; set; } = "";
    public HashSet<string> Ignored { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public KeywordRules Rules { get; set; } = new();
    public KeywordOrder KeywordOrder { get; set; } = KeywordOrder.Frequency;
    public bool KeywordDescending { get; set; } = true;
    public int KeywordLength { get; set; }
    public double WindowWidth { get; set; } = 780;
    public double WindowHeight { get; set; } = 900;
}
public enum KeywordOrder { Frequency, Name, Length }
public enum FileSort { Name, Directory, Size, Modified, Type }
public sealed record SearchRequest(string Text, string[] Keywords, string Extension, FileSort Sort, bool Descending, int Page, int PageSize = 200);
public sealed record SearchPage(List<FileEntry> Files, int Total, int Page, int Pages);
