using System.Text.Json;
namespace KeywordViewer.Core;

public sealed record LibraryProfile(string Id, string Name, string Root);
public sealed class LibraryCatalog
{
    public string ActiveId { get; set; } = "";
    public List<LibraryProfile> Libraries { get; set; } = [];
}
public sealed record ExportLibrary(string Name, string Root, ViewerPreferences Preferences);
public sealed record SettingsExport(int Version, bool IncludesPaths, List<ExportLibrary> Libraries);

public sealed class LibraryManager
{
    private readonly ViewerStore storage;
    public LibraryCatalog Catalog { get; }
    public LibraryProfile Active => Catalog.Libraries.Single(l => l.Id == Catalog.ActiveId);
    public LibraryManager(ViewerStore storage, string? requestedRoot = null)
    {
        this.storage = storage;
        var path = Path.Combine(storage.BaseDirectory, "libraries.json");
        if (File.Exists(path))
        {
            Catalog = JsonSerializer.Deserialize<LibraryCatalog>(File.ReadAllText(path)) ?? throw new InvalidDataException("资料库配置为空。");
            foreach (var library in Catalog.Libraries) ValidateId(library.Id);
            if (Catalog.Libraries.Count == 0) throw new InvalidDataException("资料库配置缺少条目。");
            if (!Catalog.Libraries.Any(l => l.Id == Catalog.ActiveId)) Catalog.ActiveId = Catalog.Libraries[0].Id;
        }
        else
        {
            Catalog = new();
            var legacy = storage.LoadPreferences();
            var root = Path.GetFullPath(requestedRoot ?? legacy.Root);
            var library = new LibraryProfile(Guid.NewGuid().ToString("N"), DefaultName(root), root);
            var target = StoreFor(library);
            legacy.Root = root; target.SavePreferences(legacy);
            var oldCache = storage.CachePath(root);
            if (File.Exists(oldCache)) File.Copy(oldCache, target.CachePath(root), overwrite: false);
            Catalog.Libraries.Add(library); Catalog.ActiveId = library.Id; Save();
        }
        if (requestedRoot is not null)
        {
            var root = Path.GetFullPath(requestedRoot);
            var found = Catalog.Libraries.FirstOrDefault(l => SameRoot(l.Root, root)) ?? Add(DefaultName(root), root);
            Activate(found.Id);
        }
    }
    public ViewerStore StoreFor(LibraryProfile library)
    {
        ValidateId(library.Id);
        return new ViewerStore(Path.Combine(storage.BaseDirectory, "libraries", library.Id));
    }
    public LibraryProfile Add(string name, string root, ViewerPreferences? preferences = null)
    {
        if (Catalog.Libraries.Count >= 100) throw new ArgumentException("最多支持 100 个资料库。");
        name = name.Trim(); if (name.Length is < 1 or > 100) throw new ArgumentException("资料库名称需为 1～100 个字符。");
        root = root.Length == 0 ? "" : Path.GetFullPath(root);
        if (root.Length > 0 && Catalog.Libraries.Any(l => SameRoot(l.Root, root))) throw new ArgumentException("该目录已有资料库，请直接切换。");
        var library = new LibraryProfile(Guid.NewGuid().ToString("N"), name, root);
        preferences ??= new(); preferences.Root = root; preferences.Rules.Validate(); StoreFor(library).SavePreferences(preferences);
        Catalog.Libraries.Add(library); Save(); return library;
    }
    public void Activate(string id) { if (!Catalog.Libraries.Any(l => l.Id == id)) throw new ArgumentException("资料库不存在。"); Catalog.ActiveId = id; Save(); }
    public LibraryProfile Update(string id, string name, string root)
    {
        var old = Catalog.Libraries.Single(l => l.Id == id);
        name = name.Trim(); if (name.Length is < 1 or > 100) throw new ArgumentException("资料库名称需为 1～100 个字符。");
        root = root.Length == 0 ? "" : Path.GetFullPath(root);
        if (root.Length > 0 && Catalog.Libraries.Any(l => l.Id != id && SameRoot(l.Root, root))) throw new ArgumentException("该目录已有资料库。");
        var updated = old with { Name = name, Root = root };
        Catalog.Libraries[Catalog.Libraries.IndexOf(old)] = updated;
        var prefs = StoreFor(old).LoadPreferences(); prefs.Root = root; StoreFor(old).SavePreferences(prefs); Save(); return updated;
    }
    public void Remove(string id)
    {
        if (Catalog.Libraries.Count == 1) throw new InvalidOperationException("请至少保留一个资料库。");
        var library = Catalog.Libraries.Single(l => l.Id == id);
        var folder = StoreFor(library).BaseDirectory;
        Catalog.Libraries.Remove(library); if (Catalog.ActiveId == id) Catalog.ActiveId = Catalog.Libraries[0].Id; Save();
        foreach (var file in Directory.EnumerateFiles(folder))
            if (Path.GetFileName(file) == "preferences.json" || Path.GetFileName(file) == "runtime.log" || Path.GetFileName(file).StartsWith("index-", StringComparison.Ordinal) && file.EndsWith(".json.gz", StringComparison.Ordinal)) File.Delete(file);
        if (!Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
    }
    public void Export(string path, bool includePaths)
    {
        var entries = Catalog.Libraries.Select(l =>
        {
            var prefs = StoreFor(l).LoadPreferences();
            if (!includePaths) { prefs.Root = ""; prefs.EverythingPath = ""; }
            return new ExportLibrary(l.Name, includePaths ? l.Root : "", prefs);
        }).ToList();
        var destination = Path.GetFullPath(path);
        new ViewerStore(Path.GetDirectoryName(destination)!).SaveDocument(Path.GetFileName(destination), new SettingsExport(1, includePaths, entries));
    }
    public int Import(string path)
    {
        if (new FileInfo(path).Length > 5 * 1024 * 1024) throw new InvalidDataException("设置文件过大。");
        var package = JsonSerializer.Deserialize<SettingsExport>(File.ReadAllText(path)) ?? throw new InvalidDataException("设置文件无效。");
        if (package.Version != 1 || package.Libraries is null || package.Libraries.Count + Catalog.Libraries.Count > 100) throw new InvalidDataException("设置版本或资料库数量不支持。");
        // Validate the complete document before writing any profile. Imported profiles never overwrite existing ones.
        foreach (var item in package.Libraries)
        {
            if (string.IsNullOrWhiteSpace(item.Name) || item.Name.Length > 100 || item.Preferences?.Rules is null || item.Root is null) throw new InvalidDataException("资料库设置无效。");
            item.Preferences.Rules.Validate();
            if (package.IncludesPaths && item.Root.Length > 0) Path.GetFullPath(item.Root);
        }
        foreach (var item in package.Libraries)
        {
            var root = package.IncludesPaths ? item.Root : "";
            if (Catalog.Libraries.Any(l => SameRoot(l.Root, root))) root = "";
            if (!package.IncludesPaths) item.Preferences.EverythingPath = "";
            Add(item.Name.Length <= 96 ? item.Name + "（导入）" : item.Name, root, item.Preferences);
        }
        return package.Libraries.Count;
    }
    private void Save() => storage.SaveDocument("libraries.json", Catalog);
    private static void ValidateId(string id) { if (!Guid.TryParseExact(id, "N", out _)) throw new InvalidDataException("资料库标识无效。"); }
    private static string DefaultName(string root) => Path.GetFileName(Path.TrimEndingDirectorySeparator(root)) is { Length: > 0 } name ? name : "默认资料库";
    private static bool SameRoot(string first, string second) => first.Length > 0 && second.Length > 0 && string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)), StringComparison.OrdinalIgnoreCase);
}
