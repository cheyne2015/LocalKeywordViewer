using System.Text;
using System.Text.RegularExpressions;
using JiebaNet.Segmenter;

namespace KeywordViewer.Core;

public sealed class KeywordTokenizer
{
    private readonly JiebaSegmenter segmenter;
    private readonly HashSet<string> stopWords;
    private static readonly Regex Noise = new(@"https?://\S+|www\.\S+|(?<![A-Za-z0-9])[A-Za-z0-9][A-Za-z0-9.-]*\.(?:xyz|com|net|org|cn|top|cc|vip|site|info|me|tv|io)\b|\b[0-9a-fA-F]{16,}\b|\d{4}年(?:\d{1,2}月)?(?:\d{1,2}日)?|\d{4}[-_.]\d{1,2}[-_.]\d{1,2}|\d{1,2}[月日]|\b\d+\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Pieces = new(@"[\p{IsCJKUnifiedIdeographs}]+|[A-Za-z][A-Za-z0-9_+-]*", RegexOptions.Compiled);
    private static readonly Regex Chinese = new(@"^[\p{IsCJKUnifiedIdeographs}]+$", RegexOptions.Compiled);

    public KeywordTokenizer(string resources)
    {
        ConfigManager.ConfigFileBaseDir = Path.GetFullPath(resources);
        if (!File.Exists(Path.Combine(resources, "dict.txt"))) throw new FileNotFoundException("缺少中文分词词典，请保持 Resources 文件夹与程序在一起。");
        segmenter = new JiebaSegmenter();
        stopWords = new(StringComparer.OrdinalIgnoreCase) { "jpg", "jpeg", "png", "gif", "webp", "mp4", "avi", "mov", "ts", "html", "txt", "pdf", "zip", "rar", "part", "watermarked", "img", "image", "video", "dsc", "下载", "文件" };
        var stopFile = Path.Combine(resources, "stopwords.txt");
        if (File.Exists(stopFile)) foreach (var word in File.ReadLines(stopFile)) if (!string.IsNullOrWhiteSpace(word)) stopWords.Add(word.Trim());
    }

    // A scanner owns this instance; Jieba's mutable caches are not shared across concurrent scans.
    public HashSet<string> Extract(string name, bool fileName = false)
    {
        var text = (fileName ? Path.GetFileNameWithoutExtension(name) : name).Normalize(NormalizationForm.FormKC);
        text = Noise.Replace(text, " ");
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match piece in Pieces.Matches(text))
        {
            if (Chinese.IsMatch(piece.Value))
            {
                foreach (var word in segmenter.CutForSearch(piece.Value, hmm: true))
                    if (word.Length >= 2 && Chinese.IsMatch(word) && !stopWords.Contains(word)) result.Add(word);
            }
            else
            {
                var word = piece.Value.Trim('_', '-', '+').ToLowerInvariant();
                if (word.Length >= 2 && !stopWords.Contains(word) && !Regex.IsMatch(word, @"^(?:img|dsc|part)\d*$", RegexOptions.IgnoreCase)) result.Add(word);
            }
        }
        return result;
    }
}
