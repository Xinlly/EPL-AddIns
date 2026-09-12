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
}
