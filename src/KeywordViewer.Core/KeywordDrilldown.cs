using System.Text;

namespace KeywordViewer.Core;

public static class KeywordDrilldown
{
    // Work from file records, never from the displayed top 100 words or whole matching groups.
    public static FileIndex Select(FileIndex index, IEnumerable<string> selected, CancellationToken token = default)
    {
        var words = selected.Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(w => w.Normalize(NormalizationForm.FormKC)).ToArray();
        if (words.Length == 0) return index;
        var files = new List<FileEntry>();
        var groups = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < index.Files.Count; i++)
        {
            if ((i & 255) == 0) token.ThrowIfCancellationRequested();
            var file = index.Files[i];
            var text = file.SearchText.Normalize(NormalizationForm.FormKC);
            if (words.Any(w => !text.Contains(w, StringComparison.OrdinalIgnoreCase))) continue;
            files.Add(file);
            if (!groups.TryGetValue(file.GroupKey, out var keywords))
                groups[file.GroupKey] = keywords = new(StringComparer.OrdinalIgnoreCase);
            keywords.UnionWith(file.Keywords ?? throw new InvalidOperationException("名称索引尚未升级。"));
        }
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var keywords in groups.Values)
        {
            token.ThrowIfCancellationRequested();
            foreach (var word in keywords) counts[word] = counts.GetValueOrDefault(word) + 1;
        }
        token.ThrowIfCancellationRequested();
        return index with { Files = files, GroupCount = groups.Count,
            Keywords = counts.Where(k => k.Value >= 2 && !words.Contains(k.Key, StringComparer.OrdinalIgnoreCase))
                .Select(k => new KeywordCount(k.Key, k.Value)).OrderByDescending(k => k.Groups).ThenBy(k => k.Word).ToList() };
    }
}
