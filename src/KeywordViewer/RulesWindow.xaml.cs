using System.Windows;
using KeywordViewer.Core;

namespace KeywordViewer;
public partial class RulesWindow : Window
{
    public KeywordRules? Result { get; private set; }
    public RulesWindow(KeywordRules rules) { InitializeComponent(); Fill(rules); }
    private void Fill(KeywordRules rules)
    {
        MinimumGroupsBox.Text = rules.MinimumGroups.ToString(); MinimumLengthBox.Text = rules.MinimumLength.ToString(); MaximumLengthBox.Text = rules.MaximumLength.ToString();
        ChineseOnlyBox.IsChecked = rules.ChineseOnly; ExcludedWordsBox.Text = string.Join(Environment.NewLine, rules.ExcludedWords.OrderBy(x => x)); ExcludedContainsBox.Text = string.Join(Environment.NewLine, rules.ExcludedContains);
    }
    private void Defaults_Click(object sender, RoutedEventArgs e) => Fill(new());
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(MinimumGroupsBox.Text, out var groups) || !int.TryParse(MinimumLengthBox.Text, out var minimum) || !int.TryParse(MaximumLengthBox.Text, out var maximum)) { ErrorText.Text = "资料组数和词长需填写整数。"; return; }
        var rules = new KeywordRules { MinimumGroups = groups, MinimumLength = minimum, MaximumLength = maximum, ChineseOnly = ChineseOnlyBox.IsChecked == true,
            ExcludedWords = new(Lines(ExcludedWordsBox.Text), StringComparer.OrdinalIgnoreCase), ExcludedContains = Lines(ExcludedContainsBox.Text).ToList() };
        try { rules.Validate(); Result = rules; DialogResult = true; }
        catch (ArgumentException ex) { ErrorText.Text = ex.Message; }
    }
    private static IEnumerable<string> Lines(string text) => text.Split(['\r','\n'], StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase);
}
