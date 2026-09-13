using Eplan.EplApi.Base;
using System;

namespace EA.EplAddIn.TextBatchEdit;

/// <summary>
/// 项目翻译语言设置值解析。设置里可能存短码（zh_CN）、带前缀名（L_zh_CN）或语言编号（100）。
/// </summary>
internal static class LangHelper
{
    public static bool TryParse(string? raw, out ISOCode.Language lang)
    {
        lang = ISOCode.Language.L___;
        if (string.IsNullOrWhiteSpace(raw)) { return false; }
        var s = raw!.Trim();

        // 数字编号
        if (int.TryParse(s, out var num))
        {
            if (Enum.IsDefined(typeof(ISOCode.Language), num)) { lang = (ISOCode.Language)num; return true; }
            return false;
        }

        // 枚举名：接受 "L_zh_CN" 或短码 "zh_CN"
        var name = s.StartsWith("L_", StringComparison.Ordinal) ? s : "L_" + s;
        if (Enum.TryParse(name, out ISOCode.Language parsed)) { lang = parsed; return true; }

        // 兜底：走 EPLAN ISOCode（短码 → 编号）
        try
        {
            using (var iso = new ISOCode())
            {
                iso.SetString(s);
                var n = iso.GetNumber();
                if (n != ISOCode.Language.L___) { lang = n; return true; }
            }
        }
        catch { /* 忽略，返回失败 */ }

        return false;
    }

    /// <summary>列标题用短码（去 L_ 前缀）。</summary>
    public static string Code(ISOCode.Language lang)
    {
        var n = lang.ToString();
        return n.StartsWith("L_", StringComparison.Ordinal) ? n.Substring(2) : n;
    }

    /// <summary>列标题用中文语言名（如 zh_CN→中文、en_US→英文）；未知语言回退为短码。</summary>
    public static string DisplayName(ISOCode.Language lang)
    {
        switch (Code(lang))
        {
            case "zh_CN": return "中文";
            case "zh_TW": return "繁体中文";
            case "en_US": return "英文";
            case "ja_JP": return "日文";
            case "ko_KR": return "韩文";
            case "de_DE": return "德文";
            case "fr_FR": return "法文";
            case "es_ES": return "西班牙文";
            case "ru_RU": return "俄文";
            case "pt_BR": return "葡萄牙文（巴西）";
            case "it_IT": return "意大利文";
            case "nl_NL": return "荷兰文";
            case "pl_PL": return "波兰文";
            case "cs_CZ": return "捷克文";
            default: return Code(lang);
        }
    }
}
