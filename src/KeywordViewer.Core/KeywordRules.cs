using System.Text.RegularExpressions;

namespace KeywordViewer.Core;
public sealed class KeywordRules
{
    public int MinimumGroups { get; set; } = 10;
    public int MinimumLength { get; set; } = 2;
    public int MaximumLength { get; set; } = 8;
    public bool ChineseOnly { get; set; } = true;
    public HashSet<string> ExcludedWords { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    { "最新", "合集", "全集", "高清", "超清", "视频", "图片", "精品", "推荐", "无水印", "去重", "版本", "极品", "超级", "顶级", "高质", "质量", "全新", "新品", "精选", "超多", "大量", "稀有", "重磅", "独家", "完整", "更新", "系列", "精彩", "各种", "一个", "两个", "全部" };
    public List<string> ExcludedContains { get; set; } = [];
    private static readonly Regex Chinese = new(@"^[\p{IsCJKUnifiedIdeographs}]+$", RegexOptions.Compiled);
    public bool Accepts(KeywordCount keyword)
    {
        return keyword.Groups >= MinimumGroups && keyword.Word.Length >= MinimumLength && keyword.Word.Length <= MaximumLength
            && (!ChineseOnly || Chinese.IsMatch(keyword.Word)) && !ExcludedWords.Contains(keyword.Word)
            && !ExcludedContains.Any(fragment => !string.IsNullOrWhiteSpace(fragment) && keyword.Word.Contains(fragment, StringComparison.OrdinalIgnoreCase));
    }
    public void Validate()
    {
        if (MinimumGroups < 2 || MinimumGroups > 1000000) throw new ArgumentException("资料组数量门槛需要在 2～1,000,000 之间。");
        if (MinimumLength < 2 || MaximumLength < MinimumLength || MaximumLength > 100) throw new ArgumentException("词长需满足：2 ≤ 最短长度 ≤ 最长长度 ≤ 100。");
    }
}
