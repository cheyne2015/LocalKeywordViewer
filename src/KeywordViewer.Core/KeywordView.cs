namespace KeywordViewer.Core;

public static class KeywordView
{
    public static List<int> AvailableLengths(IEnumerable<KeywordCount> words, ViewerPreferences preferences, string search) =>
        Filter(words, preferences, search).Select(k => k.Word.Length).Distinct().Order().ToList();
    private static IEnumerable<KeywordCount> Filter(IEnumerable<KeywordCount> words, ViewerPreferences preferences, string search) =>
        words.Where(k => preferences.Rules.Accepts(k) && !preferences.Ignored.Contains(k.Word)
            && k.Word.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase));
    public static List<KeywordCount> Apply(IEnumerable<KeywordCount> words, ViewerPreferences preferences, string search)
    {
        var filtered = Filter(words, preferences, search).Where(k => preferences.KeywordLength == 0 || k.Word.Length == preferences.KeywordLength);
        IOrderedEnumerable<KeywordCount> ordered = preferences.KeywordOrder switch
        {
            KeywordOrder.Name => preferences.KeywordDescending ? filtered.OrderByDescending(k => k.Word, StringComparer.CurrentCultureIgnoreCase) : filtered.OrderBy(k => k.Word, StringComparer.CurrentCultureIgnoreCase),
            KeywordOrder.Length => preferences.KeywordDescending ? filtered.OrderByDescending(k => k.Word.Length) : filtered.OrderBy(k => k.Word.Length),
            _ => preferences.KeywordDescending ? filtered.OrderByDescending(k => k.Groups) : filtered.OrderBy(k => k.Groups)
        };
        return ordered.ThenBy(k => k.Word, StringComparer.CurrentCultureIgnoreCase).ToList();
    }
}
