using Eplan.EplApi.ApplicationFramework;
using Eplan.EplApi.Base;
using Eplan.EplApi.DataModel;
using Eplan.EplApi.DataModel.Graphics;
using Eplan.EplApi.HEServices;
using Eplan.EplApi.Scripting;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;

namespace EA.EplAddIn.TextBatchEdit;

public class TextBatchEditAction : IEplAction
{
    public const string ActionName = "TextBatchEditAction";

    [DeclareAction(ActionName)]
    public bool Execute(ActionCallingContext oActionName)
    {
        AddInLogger.Info("Action Execute: start");
        try
        {
            var selected = new SelectionSet().Selection ?? Array.Empty<StorableObject>();
            AddInLogger.Info("SelectionSet.Selection: count=" + selected.Length);

            var texts = selected.OfType<TextBase>().ToList();
            AddInLogger.Info("筛出 TextBase(自由文本/路径文本): count=" + texts.Count);

            if (texts.Count == 0)
            {
                MessageBox.Show("当前选择集中没有文本对象。\n请在图形编辑器中框选文本后再执行。",
                    "批量修改选中文本", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return true;
            }

            var project = texts[0].Project;

            // 源语言：优先项目翻译设置 PROJECT.TRANSLATEGUI.SOURCE_LANGUAGE；失败回退 PROJ_SOURCELANGUAGE 属性
            var sourceLang = ReadSourceLanguage(project);
            var projectLangs = ReadProjectLanguages(project);
            if (projectLangs.Count == 0)
            {
                projectLangs.Add(sourceLang);
                AddInLogger.Warn("项目语言设置为空，仅使用源语言 " + sourceLang);
            }
            // 保证源语言一定在语言集合中，且其余语言按设置固定顺序
            var orderedLangs = new List<ISOCode.Language> { sourceLang };
            foreach (var l in projectLangs)
            {
                if (!orderedLangs.Contains(l)) { orderedLangs.Add(l); }
            }

            AddInLogger.Info("项目源语言=" + sourceLang + "，项目语言=[" + string.Join(",", orderedLangs) + "]");

            using (var form = new TextBatchEditForm(texts, sourceLang, orderedLangs))
            {
                form.ShowDialog();
            }
            AddInLogger.Info("Action Execute: 窗口已关闭");
            return true;
        }
        catch (Exception ex)
        {
            AddInLogger.Error("Action Execute 未处理异常", ex);
            MessageBox.Show("执行失败：" + ex.Message + "\n\n详见日志。",
                "批量修改选中文本", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
    }

    /// <summary>
    /// 源语言：项目设置 TRANSLATEGUI.SOURCE_LANGUAGE（ProjectSettings 已锚定 PROJECT，键不带前缀），
    /// 失败回退只读属性 PROJ_SOURCELANGUAGE。
    /// </summary>
    private static ISOCode.Language ReadSourceLanguage(Project project)
    {
        // 1) TRANSLATEGUI.SOURCE_LANGUAGE（单值，索引 0，如 zh_CN）
        try
        {
            var raw = project.Settings.GetStringSetting("TRANSLATEGUI.SOURCE_LANGUAGE", 0);
            AddInLogger.Debug("TRANSLATEGUI.SOURCE_LANGUAGE 设置原始值=" + (raw == null ? "(null)" : "[" + raw + "]"));
            if (LangHelper.TryParse(raw, out var l)) { return l; }
        }
        catch (Exception ex)
        {
            AddInLogger.Debug("读取 TRANSLATEGUI.SOURCE_LANGUAGE 失败，回退属性：" + ex.GetType().Name + " " + ex.Message);
        }

        // 2) 只读项目属性 PROJ_SOURCELANGUAGE（Int64 语言编号）
        try
        {
            var id = project.Properties.PROJ_SOURCELANGUAGE.ToInt();
            AddInLogger.Debug("PROJ_SOURCELANGUAGE 属性编号=" + id);
            if (Enum.IsDefined(typeof(ISOCode.Language), id)) { return (ISOCode.Language)id; }
        }
        catch (Exception ex)
        {
            AddInLogger.Error("读取 PROJ_SOURCELANGUAGE 属性失败", ex);
        }

        AddInLogger.Warn("无法确定源语言，默认 zh_CN");
        return ISOCode.Language.L_zh_CN;
    }

    /// <summary>
    /// 读取项目翻译语言集合：TRANSLATEGUI.TRANSLATE_LANGUAGES 是单个分号串（索引 0，如
    /// "en_US;zh_CN;ja_JP;"），不是按索引多值。按 ';' 拆分去空。
    /// </summary>
    private static List<ISOCode.Language> ReadProjectLanguages(Project project)
    {
        var result = new List<ISOCode.Language>();
        string raw;
        try
        {
            raw = project.Settings.GetStringSetting("TRANSLATEGUI.TRANSLATE_LANGUAGES", 0);
        }
        catch (Exception ex)
        {
            AddInLogger.Warn("读取 TRANSLATEGUI.TRANSLATE_LANGUAGES 失败：" + ex.GetType().Name + " " + ex.Message);
            return result;
        }

        AddInLogger.Debug("TRANSLATEGUI.TRANSLATE_LANGUAGES 设置原始值=[" + (raw ?? "(null)") + "]");
        if (string.IsNullOrWhiteSpace(raw)) { return result; }

        foreach (var part in raw!.Split(';'))
        {
            var code = part.Trim();
            if (code.Length == 0) { continue; }
            if (LangHelper.TryParse(code, out var lang))
            {
                if (!result.Contains(lang)) { result.Add(lang); }
            }
            else
            {
                AddInLogger.Warn("无法解析项目语言代码 [" + code + "]，跳过");
            }
        }
        return result;
    }

    public void GetActionProperties(ref ActionProperties actionProperties) { }

    public bool OnRegister(ref string Name, ref int Ordinal) { return true; }
}
