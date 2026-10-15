using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace KeywordViewer.Core;

public sealed class ViewerStore
{
    public string BaseDirectory { get; }
    public ViewerStore(string? directory = null)
    {
        BaseDirectory = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LocalKeywordViewer");
        Directory.CreateDirectory(BaseDirectory);
    }
    public string CachePath(string root)
    {
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)).ToUpperInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..20];
        return Path.Combine(BaseDirectory, "index-" + hash + ".json.gz");
    }
    public FileIndex? LoadIndex(string root)
    {
        var path = CachePath(root);
        if (!File.Exists(path)) return null;
        using var input = File.OpenRead(path);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        var index = JsonSerializer.Deserialize<FileIndex>(gzip);
        if (index is null || index.Version != 2 || !string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)), index.Root, StringComparison.OrdinalIgnoreCase)) return null;
        return index;
    }
    public void SaveIndex(FileIndex index, CancellationToken token = default)
    {
        var destination = CachePath(index.Root);
        AtomicWrite(destination, output => { using var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true); JsonSerializer.Serialize(gzip, index); }, token);
    }
    public ViewerPreferences LoadPreferences()
    {
        var path = Path.Combine(BaseDirectory, "preferences.json");
        if (!File.Exists(path)) return new();
        var prefs = JsonSerializer.Deserialize<ViewerPreferences>(File.ReadAllText(path)) ?? new();
        prefs.Ignored = new(prefs.Ignored ?? [], StringComparer.OrdinalIgnoreCase);
        prefs.Rules ??= new();
        prefs.Rules.ExcludedWords = new(prefs.Rules.ExcludedWords ?? [], StringComparer.OrdinalIgnoreCase);
        prefs.Rules.ExcludedContains ??= [];
        prefs.Rules.Validate();
        return prefs;
    }
    public void SavePreferences(ViewerPreferences preferences) => AtomicWrite(Path.Combine(BaseDirectory, "preferences.json"), stream => JsonSerializer.Serialize(stream, preferences));
    public void Log(string message) => File.AppendAllText(Path.Combine(BaseDirectory, "runtime.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
    internal void SaveDocument<T>(string name, T document) => AtomicWrite(Path.Combine(BaseDirectory, name), output => JsonSerializer.Serialize(output, document, new JsonSerializerOptions { WriteIndented = true }));
    private static void AtomicWrite(string destination, Action<Stream> write, CancellationToken token = default)
    {
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { write(stream); stream.Flush(true); }
            token.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
