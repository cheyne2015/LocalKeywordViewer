using KeywordViewer.Core;
using System.IO;
using System.Diagnostics;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Text;

var resources = Path.Combine(AppContext.BaseDirectory, "Resources");
var project = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
var verification = Path.Combine(project, "verification");
Directory.CreateDirectory(verification);
var passed = new List<string>();
var viewPrefs = new ViewerPreferences { Rules = new() { MinimumGroups = 2 } };
var viewWords = new[] { new KeywordCount("北京", 2), new KeywordCount("上海", 8), new KeywordCount("图书馆", 5) };
Assert(KeywordView.AvailableLengths(viewWords, viewPrefs, "").SequenceEqual(new[] { 2, 3 }), "Only existing keyword lengths offered");
Assert(KeywordView.AvailableLengths(viewWords, viewPrefs, "北京").SequenceEqual(new[] { 2 }), "Length options follow keyword search");
viewPrefs.Ignored.Add("图书馆");
Assert(KeywordView.AvailableLengths(viewWords, viewPrefs, "").SequenceEqual(new[] { 2 }), "Length options exclude ignored keywords");
viewPrefs.Ignored.Clear();
Assert(KeywordView.Apply(viewWords, viewPrefs, "").Select(x => x.Groups).SequenceEqual(new[] { 8, 5, 2 }), "Frequency descending");
viewPrefs.KeywordDescending = false;
Assert(KeywordView.Apply(viewWords, viewPrefs, "").Select(x => x.Groups).SequenceEqual(new[] { 2, 5, 8 }), "Frequency ascending");
viewPrefs.KeywordLength = 3;
Assert(KeywordView.Apply(viewWords, viewPrefs, "").Single().Word == "图书馆", "Exact keyword length filter");
viewPrefs.KeywordLength = 0; viewPrefs.KeywordOrder = KeywordOrder.Length;
Assert(KeywordView.Apply(viewWords, viewPrefs, "").Last().Word == "图书馆", "Length ascending");
viewPrefs.KeywordOrder = KeywordOrder.Name;
Assert(KeywordView.Apply(viewWords, viewPrefs, "图").Single().Word == "图书馆", "Search combined with view settings");
passed.Add("keyword frequency, direction, length sorting and exact length filtering");
var fixture = Path.Combine(verification, "fixture-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"));
Directory.CreateDirectory(fixture);
Write("北京 旅行/001.txt", "one");
Write("北京 旅行/002.txt", "two longer");
Write("北京 旅行/照片/003.jpg", "sample placeholder");
Write("上海 旅行/004.txt", "four");
Write("上海 旅行/北京/005.txt", "five");
Write("独立北京旅行.txt", "six");
Write("北京旅行 root.pdf", "seven");
Write("无关分组/999.txt", "eight");
Write("2026年10月12日 12345678901234567890123456789012.jpg", "noise");
Write("网址 https-example.xyz 6789.jpg", "noise");
var scanner = new IndexScanner(resources);
var indexFixture = scanner.Scan(fixture, default);
Assert(indexFixture.Files.Count == 10 && indexFixture.GroupCount == 7, "Fixture file/group count"); passed.Add("root files and first-level groups");
Assert(indexFixture.Keywords.Single(k => k.Word == "北京").Groups == 4, "Group deduplication"); passed.Add("keyword group deduplication across nested folders");
Assert(indexFixture.Keywords.Single(k => k.Word == "旅行").Groups == 4, "Repeated filenames must not inflate frequency");
var tokenizerFixture = new KeywordTokenizer(resources);
var noise = tokenizerFixture.Extract("2026年10月12日 www.example.com 389929.xyz 12345678901234567890123456789012.jpg", true);
Assert(noise.Count == 0, "Date/url/hash/extension noise");
Assert(tokenizerFixture.Extract("OpenAI_Research 报告.mp4", true).Contains("openai_research"), "English name preservation"); passed.Add("Chinese segmentation, English names and noise suppression");
var fixtureEngine = new SearchEngine(indexFixture);
var formatOptions = FileFormats.Options(indexFixture);
Assert(!formatOptions.Any(o => o.Key == "video") && formatOptions.Any(o => o.Key == "image"), "Formats list only actual categories");
var textFormat = formatOptions.Single(o => o.Key == "ext:.txt");
var textIndex = FileFormats.Select(indexFixture, textFormat, default);
Assert(textIndex.Files.Count == 6 && textIndex.GroupCount == 4 && textIndex.Keywords.Single(k => k.Word == "北京").Groups == 3, "Format statistics deduplicate only matching files by group");
Assert(FileFormats.Select(indexFixture, formatOptions.Single(o => o.Key == "image"), default).Keywords.All(k => k.Word != "北京"), "Image keyword below two groups is excluded");
Assert(FileFormats.EverythingClause(textFormat) == " ext:txt", "Everything extension clause agrees with local format");
var oldIndex = indexFixture with { Files = indexFixture.Files.Select(f => f with { Keywords = null }).ToList() };
Assert(FileFormats.EnsureKeywords(oldIndex, resources, default).Files.Zip(indexFixture.Files).All(pair => pair.First.Keywords!.ToHashSet().SetEquals(pair.Second.Keywords!)), "Legacy name cache upgrades without reading content");
passed.Add("format availability, group statistics, Everything clause and legacy cache upgrade");
var beijing = fixtureEngine.Search(new("", ["北京"], "", FileSort.Name, false, 1));
Assert(beijing.Total == 6, "Inherited directory matches");
Assert(fixtureEngine.Search(new("", ["北京", "旅行"], ".txt", FileSort.Size, true, 1)).Total == 4, "Multi-keyword AND plus type");
Assert(fixtureEngine.Search(new("不存在", [], "", FileSort.Name, false, 1)).Total == 0, "Zero search");
Assert(fixtureEngine.Search(new("005", [], "", FileSort.Name, false, 1)).Total == 1, "Arbitrary substring");
var page = fixtureEngine.Search(new("", [], "", FileSort.Name, false, 99, 3));
Assert(page.Pages == 4 && page.Page == 4 && page.Files.Count == 1, "Pagination clamp"); passed.Add("filename and directory matching, AND search, type, sorting, paging, empty results");
var testStore = new ViewerStore(Path.Combine(verification, "fixture-data"));
testStore.SaveIndex(indexFixture);
Assert(testStore.LoadIndex(fixture)!.Files.Count == 10, "Cache round trip");
var cacheBefore = File.ReadAllBytes(testStore.CachePath(fixture));
using (var cancelled = new CancellationTokenSource())
{
    cancelled.Cancel();
    Expect<OperationCanceledException>(() => scanner.Scan(fixture, cancelled.Token));
    Expect<OperationCanceledException>(() => testStore.SaveIndex(indexFixture with { Files = [] }, cancelled.Token));
    Expect<OperationCanceledException>(() => fixtureEngine.Search(new("", [], "", FileSort.Name, false, 1), cancelled.Token));
}
Assert(File.ReadAllBytes(testStore.CachePath(fixture)).SequenceEqual(cacheBefore), "Cancellation must preserve cache");
Expect<DirectoryNotFoundException>(() => scanner.Scan(Path.Combine(fixture, "missing"), default));
Assert(Directory.GetFiles(testStore.BaseDirectory, "*.tmp").Length == 0, "Temporary cache cleanup"); passed.Add("cache round trip, cancelled scan/search/save, cache retention, unavailable directory");
var prefs = new ViewerPreferences { Root = fixture }; prefs.Ignored.Add("北京"); testStore.SavePreferences(prefs);
Assert(testStore.LoadPreferences().Ignored.Contains("北京"), "Ignored preference persistence"); prefs.Ignored.Remove("北京"); testStore.SavePreferences(prefs); Assert(testStore.LoadPreferences().Ignored.Count == 0, "Restored preference persistence"); passed.Add("ignore and restore persistence");
var rules = new KeywordRules();
Assert(!rules.Accepts(new("北京", 9)) && rules.Accepts(new("北京", 10)), "Frequency rule boundary");
Assert(!rules.Accepts(new("OpenAI", 100)) && !rules.Accepts(new("超长关键词名称测试", 100)), "Chinese and length rules");
Assert(!rules.Accepts(new("推荐", 100)), "Default exclusions");
rules.ExcludedWords.Remove("推荐"); Assert(rules.Accepts(new("推荐", 100)), "Restore rule exclusion");
rules.ExcludedContains.Add("旅行"); Assert(!rules.Accepts(new("旅行", 100)) && !rules.Accepts(new("旅行照片", 100)), "Contains exclusions");
rules.ExcludedContains.Clear(); rules.ChineseOnly = false; Assert(rules.Accepts(new("OpenAI", 100)), "Allow English");
rules.MinimumGroups = 2; prefs.Rules = rules; testStore.SavePreferences(prefs); Assert(testStore.LoadPreferences().Rules.MinimumGroups == 2 && !testStore.LoadPreferences().Rules.ChineseOnly, "Rule persistence");
Expect<ArgumentException>(() => new KeywordRules { MinimumGroups = 1 }.Validate());
Expect<ArgumentException>(() => new KeywordRules { MinimumLength = 8, MaximumLength = 2 }.Validate()); passed.Add("editable keyword rules, boundaries, restore exclusions and persistence");
// All mutations below are within the generated fixture; never change the source media directory.
File.Delete(Path.Combine(fixture, "北京 旅行", "002.txt")); Write("新增旅行/006.txt", "new");
var refreshed = scanner.Scan(fixture, default);
Assert(refreshed.Files.Count == 10 && refreshed.Files.Any(f => f.Name == "006.txt") && !refreshed.Files.Any(f => f.Name == "002.txt"), "Refresh add/remove");
testStore.SaveIndex(refreshed); passed.Add("refresh recognizes additions and removals");
var link = Path.Combine(fixture, "循环目录链接");
try { Directory.CreateSymbolicLink(link, fixture); var linked = scanner.Scan(fixture, default); Assert(linked.Files.Count == 10 && linked.Issues.Any(x => x.Path == link), "Skip links"); passed.Add("directory links skipped"); }
catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { passed.Add("directory link creation unavailable; scanner reparse guard reviewed"); }
var deep = Path.Combine(fixture, "长路径旅行", new string('a', 90), new string('b', 90), new string('c', 90));
Directory.CreateDirectory(deep); File.WriteAllText(Path.Combine(deep, "北京测试.txt"), "long path");
var longIndex = scanner.Scan(fixture, default); Assert(longIndex.Files.Any(f => f.FullPath.Length > 260), "Long paths"); passed.Add("long paths over 260 characters");
// Seed a harmless UI demo cache without the generated link so preview screenshots contain no original filenames.
testStore.SaveIndex(longIndex);
Assert(EverythingBridge.BuildQuery(fixture, ["北京", "旅行"]).Contains("path:nocase:nowholeword:noregex:\"北京\""), "Everything path query");
Expect<ArgumentException>(() => EverythingBridge.BuildQuery(fixture, ["x|file:"]));
Expect<FileNotFoundException>(() => EverythingBridge.StartInfo(Path.Combine(fixture, "missing.exe"), "test")); passed.Add("safe Everything expression and missing executable");
var result = new { Passed = true, Checks = passed, Fixture = fixture, UiData = testStore.BaseDirectory };
var catalogStore = new ViewerStore(Path.Combine(verification, "catalog-" + Guid.NewGuid().ToString("N")));
prefs.Root = fixture; prefs.EverythingPath = Path.Combine(fixture, "example.exe"); catalogStore.SavePreferences(prefs); catalogStore.SaveIndex(indexFixture);
var legacyPreferences = File.ReadAllBytes(Path.Combine(catalogStore.BaseDirectory, "preferences.json"));
var manager = new LibraryManager(catalogStore);
Assert(manager.StoreFor(manager.Active).LoadIndex(fixture)!.Files.Count == 10, "Migration retains original index");
var originalProfile = manager.Active;
var secondRoot = Path.Combine(fixture, "second-library"); Directory.CreateDirectory(secondRoot); File.WriteAllText(Path.Combine(secondRoot, "original.txt"), "preserve");
var second = manager.Add("第二资料库", secondRoot); manager.Activate(second.Id);
var independentPreferences = manager.StoreFor(second).LoadPreferences(); independentPreferences.Ignored.Add("独立屏蔽"); manager.StoreFor(second).SavePreferences(independentPreferences);
Assert(!manager.StoreFor(originalProfile).LoadPreferences().Ignored.Contains("独立屏蔽"), "Library preferences isolated");
Assert(new LibraryManager(catalogStore).Active.Id == second.Id, "Last library remembered");
manager.Update(second.Id, "已改名", secondRoot); Assert(manager.Active.Name == "已改名", "Rename updates profile");
var settingsFile = Path.Combine(verification, "settings-export-" + Guid.NewGuid().ToString("N") + ".json"); manager.Export(settingsFile, false);
Assert(!File.ReadAllText(settingsFile).Contains(fixture.Replace("\\", "\\\\"), StringComparison.OrdinalIgnoreCase) && !File.ReadAllText(settingsFile).Contains("example.exe"), "Shareable settings omit root and executable paths");
manager.Update(originalProfile.Id, new string('库', 100), originalProfile.Root); manager.Export(settingsFile, false);
var beforeImport = manager.Catalog.Libraries.Count; manager.Import(settingsFile); Assert(manager.Catalog.Libraries.Count == beforeImport * 2 && manager.Catalog.Libraries.Skip(beforeImport).All(p => p.Root.Length == 0), "Import adds isolated profiles requiring path assignment");
Assert(manager.Catalog.Libraries.Any(p => p.Id != originalProfile.Id && p.Name.Length == 100), "Maximum length library name round-trips through export/import");
manager.Remove(second.Id); Assert(File.Exists(Path.Combine(secondRoot, "original.txt")), "Remove library never touches source files");
Assert(File.ReadAllBytes(Path.Combine(catalogStore.BaseDirectory, "preferences.json")).SequenceEqual(legacyPreferences), "Migration preserves old preferences backup");
passed.Add("library migration, isolation, switching, rename, safe removal and path-free export/import");
using (var awakened = new ManualResetEventSlim())
using (var firstInstance = new SingleInstanceGuard(catalogStore.BaseDirectory, () => awakened.Set()))
{
    Assert(firstInstance.IsFirst, "First instance acquires guard"); bool duplicate = false;
    var otherThread = new Thread(() => { using var next = new SingleInstanceGuard(catalogStore.BaseDirectory, () => { }); duplicate = !next.IsFirst; });
    otherThread.Start(); otherThread.Join(); Assert(duplicate && awakened.Wait(2000), "Second instance signals activation instead of acquiring guard");
}
passed.Add("single instance and activation notification");
File.WriteAllText(Path.Combine(verification, "checks.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine(JsonSerializer.Serialize(result));
void Write(string relative, string contents) { var path = Path.Combine(fixture, relative.Replace('/', Path.DirectorySeparatorChar)); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, contents); }
static void Assert(bool condition, string label) { if (!condition) throw new Exception("FAIL: " + label); }
static void Expect<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }

