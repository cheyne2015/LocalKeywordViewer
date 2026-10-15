using System.Text;

namespace KeywordViewer.Core;

public sealed class SearchEngine
{
    private readonly FileIndex index;
    private readonly string[] texts;
    public SearchEngine(FileIndex index)
    {
        this.index = index;
        texts = index.Files.Select(x => x.SearchText.Normalize(NormalizationForm.FormKC)).ToArray();
    }
    public SearchPage Search(SearchRequest request, CancellationToken token = default)
    {
        if (request.PageSize is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(request.PageSize));
        var query = request.Text.Trim().Normalize(NormalizationForm.FormKC);
        var words = request.Keywords.Select(x => x.Normalize(NormalizationForm.FormKC)).ToArray();
        var hits = new List<FileEntry>();
        for (int i = 0; i < index.Files.Count; i++)
        {
            if ((i & 255) == 0) token.ThrowIfCancellationRequested();
            var file = index.Files[i];
            if (request.Extension.Length != 0 && !string.Equals(file.Extension, request.Extension, StringComparison.OrdinalIgnoreCase)) continue;
            if (!texts[i].Contains(query, StringComparison.OrdinalIgnoreCase) || words.Any(w => !texts[i].Contains(w, StringComparison.OrdinalIgnoreCase))) continue;
            hits.Add(file);
        }
        token.ThrowIfCancellationRequested();
        IOrderedEnumerable<FileEntry> ordered = request.Sort switch
        {
            FileSort.Size => request.Descending ? hits.OrderByDescending(x => x.Size) : hits.OrderBy(x => x.Size),
            FileSort.Modified => request.Descending ? hits.OrderByDescending(x => x.ModifiedUtc) : hits.OrderBy(x => x.ModifiedUtc),
            _ => request.Descending ? hits.OrderByDescending(Key, StringComparer.CurrentCultureIgnoreCase) : hits.OrderBy(Key, StringComparer.CurrentCultureIgnoreCase)
        };
        var pages = Math.Max(1, (hits.Count + request.PageSize - 1) / request.PageSize);
        var page = Math.Clamp(request.Page, 1, pages);
        var rows = ordered.ThenBy(x => x.FullPath, StringComparer.OrdinalIgnoreCase).Skip((page - 1) * request.PageSize).Take(request.PageSize).ToList();
        token.ThrowIfCancellationRequested();
        return new(rows, hits.Count, page, pages);
        string Key(FileEntry x) => request.Sort switch { FileSort.Directory => x.RelativeDirectory, FileSort.Type => x.Extension, _ => x.Name };
    }
}
