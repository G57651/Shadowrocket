using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Shadowrocket.Core;

namespace Shadowrocket.Views;

public partial class RulesDialog : Window
{
    private readonly AppRuntime _rt;
    private List<MihomoApi.RuleItem> _all = [];

    public RulesDialog(AppRuntime rt)
    {
        InitializeComponent();
        _rt = rt;
        Owner = Application.Current.MainWindow;
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        _all = await _rt.Api.GetRulesAsync();
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var q = SearchBox.Text.Trim();
        var rows = _all
            .Select((r, i) => new RuleRowViewModel((i + 1).ToString(), r.Type, r.Payload, r.Proxy))
            .Where(r => q.Length == 0 ||
                        r.Payload.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                        r.Type.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                        r.Proxy.Contains(q, StringComparison.OrdinalIgnoreCase))
            .ToList();
        RulesList.ItemsSource = rows;
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    /// <summary>规则测试：按内核规则顺序对域名做客户端模拟匹配（IP 类规则标注需实际连接判定）。</summary>
    private void RunTest()
    {
        var input = TestInput.Text.Trim().ToLowerInvariant();
        if (input.Length == 0)
        {
            return;
        }
        foreach (var r in _all)
        {
            var hit = r.Type switch
            {
                "Domain" => input.Equals(r.Payload, StringComparison.OrdinalIgnoreCase),
                "DomainSuffix" => input.EndsWith(r.Payload, StringComparison.OrdinalIgnoreCase),
                "DomainKeyword" => input.Contains(r.Payload, StringComparison.OrdinalIgnoreCase),
                "Match" => true,
                _ => false,
            };
            if (hit)
            {
                TestResult.Text = $"「{input}」命中第 {_all.IndexOf(r) + 1} 条规则：{r.Type},{r.Payload} → {r.Proxy}";
                return;
            }
            if (r.Type is "IPCIDR" or "GeoIP")
            {
                TestResult.Text = $"「{input}」在域名类规则中没有命中；更早的 IP 类规则（{r.Type},{r.Payload}）需要实际连接后由内核判定。";
                return;
            }
        }
        TestResult.Text = $"「{input}」没有匹配到任何规则。";
    }

    private void Test_Click(object sender, RoutedEventArgs e) => RunTest();

    private void TestInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            RunTest();
        }
    }
}

public record RuleRowViewModel(string Index, string Type, string Payload, string Proxy);
