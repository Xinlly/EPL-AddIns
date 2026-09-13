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

    /// <summary>
    /// 列标题用语言全名（对齐 EPLAN/Windows 名称，如 zh_CN→中文(中国)、en_US→英文(美国)）。
    /// EPLAN 2.9 API 不提供本地化语言名，此处用确定映射（语言名取“中文/英文”而非“汉语/英语”）；
    /// 未命中时回退 .NET CultureInfo，再不行回退短码。
    /// </summary>
    public static string DisplayName(ISOCode.Language lang)
    {
        var code = Code(lang);
        if (Known.TryGetValue(code, out var name)) { return name; }

        // 回退：CultureInfo 的“语言(地区)”，并把“汉语/英语”等统一为“中文/英文”
        try
        {
            var ci = new System.Globalization.CultureInfo(code.Replace('_', '-'));
            var d = ci.DisplayName; // 形如 “中文(中国)” / “英语(美国)” / “German (Germany)”
            d = d.Replace("汉语", "中文").Replace("英语", "英文");
            if (!string.IsNullOrWhiteSpace(d) && d != ci.Name) { return d; }
        }
        catch { /* 忽略，落到短码 */ }

        return code;
    }

    private static readonly System.Collections.Generic.Dictionary<string, string> Known = new()
    {
        ["zh_CN"] = "中文(中国)",
        ["zh_TW"] = "中文(台湾)",
        ["zh_HK"] = "中文(香港)",
        ["zh_SG"] = "中文(新加坡)",
        ["en_US"] = "英文(美国)",
        ["en_GB"] = "英文(英国)",
        ["en_AU"] = "英文(澳大利亚)",
        ["en_CA"] = "英文(加拿大)",
        ["ja_JP"] = "日文(日本)",
        ["ko_KR"] = "韩文(韩国)",
        ["de_DE"] = "德文(德国)",
        ["de_AT"] = "德文(奥地利)",
        ["fr_FR"] = "法文(法国)",
        ["es_ES"] = "西班牙文(西班牙)",
        ["es_MX"] = "西班牙文(墨西哥)",
        ["ru_RU"] = "俄文(俄罗斯)",
        ["pt_BR"] = "葡萄牙文(巴西)",
        ["pt_PT"] = "葡萄牙文(葡萄牙)",
        ["it_IT"] = "意大利文(意大利)",
        ["nl_NL"] = "荷兰文(荷兰)",
        ["pl_PL"] = "波兰文(波兰)",
        ["cs_CZ"] = "捷克文(捷克)",
        ["sv_SE"] = "瑞典文(瑞典)",
        ["tr_TR"] = "土耳其文(土耳其)",
        ["ar_SA"] = "阿拉伯文(沙特阿拉伯)",
        ["th_TH"] = "泰文(泰国)",
        ["vi_VN"] = "越南文(越南)",
        ["id_ID"] = "印尼文(印尼)",
    };
}
