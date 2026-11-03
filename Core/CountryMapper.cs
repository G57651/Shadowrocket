using System.Windows.Media;

namespace Shadowrocket.Core;

/// <summary>
/// 从节点名推断地区（机场节点名普遍带地区关键词/国旗 emoji 文本），
/// 用于展示国旗色块徽标；识别不出则不带地区。
/// </summary>
public static class CountryMapper
{
    private static readonly (string[] Keys, string Color, string Code)[] Table =
    [
        (["香港", "hk", "hongkong", "hong kong", "🇭🇰"], "#DE2910", "HK"),
        (["台湾", "台北", "新北", "tw", "taiwan", "🇹🇼"], "#000095", "TW"),
        (["日本", "东京", "大阪", "jp", "japan", "🇯🇵"], "#BC002D", "JP"),
        (["美国", "洛杉矶", "美西", "美东", "us", "usa", "🇺🇸"], "#3C3B6E", "US"),
        (["新加坡", "狮城", "sg", "singapore", "🇸🇬"], "#ED2939", "SG"),
        (["韩国", "首尔", "kr", "korea", "🇰🇷"], "#CD2E3A", "KR"),
        (["英国", "伦敦", "united kingdom", "uk", "🇬🇧"], "#012169", "GB"),
        (["德国", "法兰克福", "de", "germany", "🇩🇪"], "#1A1A1A", "DE"),
        (["法国", "巴黎", "fr", "🇫🇷"], "#0055A4", "FR"),
        (["加拿大", "ca", "canada", "🇨🇦"], "#D52B1E", "CA"),
        (["荷兰", "nl", "🇳🇱"], "#AE1C28", "NL"),
        (["俄罗斯", "ru", "🇷🇺"], "#0039A6", "RU"),
        (["澳门", "mo", "🇲🇴"], "#007168", "MO"),
        (["马来西亚", "my", "🇲🇾"], "#010066", "MY"),
        (["泰国", "th", "🇹🇭"], "#A51931", "TH"),
        (["越南", "vn", "🇻🇳"], "#DA251D", "VN"),
        (["印度", "in", "🇮🇳"], "#FF9933", "IN"),
        (["土耳其", "tr", "🇹🇷"], "#E30A17", "TR"),
        (["阿根廷", "ar", "🇦🇷"], "#74ACDF", "AR"),
        (["巴西", "br", "🇧🇷"], "#009C3B", "BR"),
        (["菲律宾", "ph", "🇵🇭"], "#0038A8", "PH"),
        (["印尼", "id", "🇮🇩"], "#CE1126", "ID"),
        (["澳大利亚", "澳洲", "au", "🇦🇺"], "#00247D", "AU"),
        (["意大利", "it", "🇮🇹"], "#008C45", "IT"),
        (["瑞士", "ch", "🇨🇭"], "#D52B1E", "CH"),
        (["瑞典", "se", "🇸🇪"], "#006AA7", "SE"),
        (["土耳其"], "#E30A17", "TR"),
    ];

    public static (string Color, string Code)? Match(string nodeName)
    {
        if (string.IsNullOrEmpty(nodeName))
        {
            return null;
        }
        var lower = nodeName.ToLowerInvariant();
        foreach (var (keys, color, code) in Table)
        {
            foreach (var k in keys)
            {
                // 短代码类关键词（gb/us/hk…）必须按词边界匹配，避免 "66.75 GB" 误判为英国
                if (k.Length <= 3 && char.IsAscii(k[0]))
                {
                    if (System.Text.RegularExpressions.Regex.IsMatch(lower, $@"(^|[^a-z]){System.Text.RegularExpressions.Regex.Escape(k)}([^a-z]|$)"))
                    {
                        return (color, code);
                    }
                }
                else if (lower.Contains(k))
                {
                    return (color, code);
                }
            }
        }
        return null;
    }

    public static Brush? MatchBrush(string nodeName)
    {
        var m = Match(nodeName);
        if (m == null)
        {
            return null;
        }
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(m.Value.Color));
        b.Freeze();
        return b;
    }
}
