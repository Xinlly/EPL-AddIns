using Eplan.EplApi.ApplicationFramework;
using Eplan.EplApi.Base;
using Eplan.EplApi.DataModel;
using Eplan.EplApi.DataModel.Graphics;
using Eplan.EplApi.HEServices;
using Eplan.EplApi.Scripting;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows.Forms;

namespace EA.EplAddIn.TextBatchEdit;

public class TextBatchEditAction : IEplAction
{
    public const string ActionName = "TextBatchEditAction";

    /// <summary>当前已非模态打开的编辑窗（单例）；关闭后置 null。</summary>
    private static TextBatchEditForm? _openForm;

    // TEMP-PERF 收集段独立开关：收集发生在 TextBatchEditForm 构造之前，不引用 Form 的开关（避免跨类耦合），
    // 本类内放同名同默认 static readonly（不用 const：const false + if 触发 CS0162，已实测）。
    // 真机量测时与 TextBatchEditForm.PerfTrace 一并改 true；验收后 grep TEMP-PERF 整体移除。
    private static readonly bool PerfTrace = false; // TEMP-PERF 一键开关：真机量测时改 true

    /// <summary>把 EPLAN 主窗口句柄包装成 WinForms 属主，使浮动窗始终在主窗之上并随其最小化。</summary>
    private static IWin32Window? GetMainWindowOwner()
    {
        try
        {
            var hwnd = Process.GetCurrentProcess().MainWindowHandle;
            if (hwnd == IntPtr.Zero) { return null; }
            return new WindowOwner(hwnd);
        }
        catch (Exception ex)
        {
            AddInLogger.Debug("获取 EPLAN 主窗句柄失败，浮动窗不设属主：" + ex.Message);
            return null;
        }
    }

    private sealed class WindowOwner : IWin32Window
    {
        public WindowOwner(IntPtr h) { Handle = h; }
        public IntPtr Handle { get; }
    }

    /// <summary>
    /// 收集本次要编辑的文本及所属项目。依据“活动窗口的完整选择”（SelectionSet.Selection）严格区分上下文，
    /// 绝不在“图面只是打开着某页、却什么都没选”时把当前页当成选中页枚举整页。
    /// 规则：
    ///  1) 选择里有 TextBase（图面选中文本）→ 仅这些文本；
    ///  2) 选择为空（图面打开页但未选中任何对象）→ 返回空，不回退当前页；
    ///  3) 显式页选择（选择含 Page，或选择非空且不含图面 Placement、GetSelectedPages 展开非空——页树选结构节点）
    ///     → 枚举所选页全部文本；
    ///  4) 其余（图面选中的是元件等非文本 Placement）→ 返回空。
    /// </summary>
    private static List<TextBase> CollectTexts(out Project project, out string sourceDesc)
    {
        project = null!;
        sourceDesc = string.Empty;
        var result = new List<TextBase>();

        var ss = new SelectionSet();
        var selection = ss.Selection ?? Array.Empty<StorableObject>();

        // 1) 图形编辑器直接选中的文本
        foreach (var o in selection)
        {
            if (o is TextBase tb)
            {
                result.Add(tb);
                project ??= SafeProjectOf(tb);
            }
        }
        if (result.Count > 0)
        {
            sourceDesc = "图形编辑器选中文本 " + result.Count + " 个";
            AddInLogger.Info("CollectTexts: " + sourceDesc);
            return result;
        }

        // 2) 无任何显式选择 → 绝不把“当前打开页”当选中页（GetSelectedPages 在此情形可能返回幽灵当前页）
        if (selection.Length == 0)
        {
            AddInLogger.Info("CollectTexts: 选择集为空（图面仅打开页而未选中对象），不回退枚举当前页");
            return result;
        }

        // 3) 页导航器显式选中页/结构节点
        Page[] pages;
        try { pages = ss.GetSelectedPages() ?? Array.Empty<Page>(); }
        catch (Exception ex)
        {
            AddInLogger.Debug("GetSelectedPages 失败：" + ex.Message);
            return result;
        }
        if (!IsExplicitPageSelection(selection, pages))
        {
            AddInLogger.Info("CollectTexts: 选择非空但既非文本也非页选择（图面选中非文本对象），不枚举整页。选择类型="
                + string.Join(",", selection.Select(o => o.GetType().Name).Take(5)));
            return result;
        }

        AddInLogger.Info("CollectTexts: 页导航器显式选中页 count=" + pages.Length + "，枚举各页文本");
        var perfAllPlacements = 0; // TEMP-PERF 各页 AllPlacements 物化对象总数（页内全部对象，非仅文本）
        foreach (var page in pages)
        {
            try
            {
                project ??= SafeProjectOf(page);
                var placements = page.AllPlacements;
                if (placements == null) { continue; }
                foreach (var pl in placements)
                {
                    if (PerfTrace) { perfAllPlacements++; } // TEMP-PERF 复用既有遍历自增，不额外触发 native 往返
                    if (pl is TextBase tb2) { result.Add(tb2); }
                }
            }
            catch (Exception ex)
            {
                AddInLogger.Warn("枚举页文本失败 页=" + SafePageName(page) + "：" + ex.Message);
            }
        }
        if (PerfTrace) // TEMP-PERF 页路径明细一行（页数 / AllPlacements 总数 / 命中文本数）
        {
            AddInLogger.Debug("PERF CollectTexts(页枚举明细): 页数=" + pages.Length
                + " AllPlacements总数=" + perfAllPlacements + " 命中文本=" + result.Count);
        }

        if (result.Count > 0)
        {
            project ??= SafeProjectOf(result[0]);
            sourceDesc = "页导航器 " + pages.Length + " 页，文本 " + result.Count + " 个";
            AddInLogger.Info("CollectTexts: " + sourceDesc);
        }
        return result;
    }

    /// <summary>
    /// 判断当前选择是否构成“显式页选择”（页导航器），用于排除图面的幽灵当前页。
    /// 直接含 Page 即成立；选结构节点时选择非空、不含图面 Placement 且 GetSelectedPages 已展开出页也成立。
    /// </summary>
    private static bool IsExplicitPageSelection(StorableObject[] selection, Page[] pages)
    {
        if (pages == null || pages.Length == 0) { return false; }
        if (selection.Any(o => o is Page)) { return true; }
        return selection.Length > 0 && !selection.Any(o => o is Placement);
    }

    /// <summary>图纸（图形编辑器）右键显隐用：当前选择集是否包含文本对象（TextBase）。
    /// 严格只认文本——图面只是打开页、未选中任何文本时返回 false，绝不因“当前页”而放行。</summary>
    public static bool SelectionHasText()
    {
        try
        {
            var sel = new SelectionSet().Selection;
            return sel != null && sel.Any(o => o is TextBase);
        }
        catch (Exception ex)
        {
            AddInLogger.Debug("SelectionHasText 判定异常：" + ex.Message);
            return false;
        }
    }

    /// <summary>页导航器右键显隐用：是否显式选中了页/结构节点（选节点时节点内页也算）。
    /// 仅允许在“页树菜单”上下文调用；图面菜单不得用它（GetSelectedPages 会返回当前打开页）。</summary>
    public static bool SelectionHasPage()
    {
        try
        {
            var pages = new SelectionSet().GetSelectedPages();
            return pages != null && pages.Length > 0;
        }
        catch (Exception ex)
        {
            AddInLogger.Debug("SelectionHasPage 判定异常：" + ex.Message);
            return false;
        }
    }

    private static Project SafeProjectOf(StorableObject o)
    {
        try { return o.Project; } catch (Exception ex) { AddInLogger.Debug("取对象项目失败：" + ex.Message); return null!; }
    }

    private static string SafePageName(Page p)
    {
        try { return p.Name; } catch { return "(取页名失败)"; }
    }

    [DeclareAction(ActionName)]
    public bool Execute(ActionCallingContext oActionName)
    {
        AddInLogger.Info("Action Execute: start");
        try
        {
            // 收集待编辑文本：图面选中文本，或页导航器显式选中页；图面仅打开页而未选中时返回空（不枚举整页）。
            var swPerfCollect = PerfTrace ? Stopwatch.StartNew() : null; // TEMP-PERF 收集总计
            var texts = CollectTexts(out var project, out var sourceDesc);
            if (PerfTrace)
            {
                swPerfCollect!.Stop(); // TEMP-PERF
                AddInLogger.Debug("PERF CollectTexts(调用点): 总=" + swPerfCollect.Elapsed.TotalMilliseconds.ToString("0.0") + "ms" // TEMP-PERF
                    + " 文本数=" + texts.Count + " 来源=" + (string.IsNullOrEmpty(sourceDesc) ? "(空/未枚举 sourceDesc=N/A)" : sourceDesc));
            }

            if (texts.Count == 0)
            {
                MessageBox.Show("没有可编辑的文本。\n请在图形编辑器中框选文本、在页导航器中选中页，或在查找结果列表中选中文本/页后再执行。",
                    "文本批量编辑", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return true;
            }

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

            // 非模态常驻：重复触发动作时，用最新选择集刷新已打开窗口的行（有未保存修改会弹窗询问）；
            // 选择集未变化则只把窗口前置。首次触发则新建窗口。
            if (_openForm != null && !_openForm.IsDisposed)
            {
                if (_openForm.WindowState == FormWindowState.Minimized) { _openForm.WindowState = FormWindowState.Normal; }
                _openForm.ReloadSelection(texts, sourceLang, orderedLangs, project);
                _openForm.BringToFront();
                return true;
            }

            var form = new TextBatchEditForm(texts, sourceLang, orderedLangs, project);
            _openForm = form;
            form.FormClosed += (_, _) =>
            {
                form.Dispose();
                if (ReferenceEquals(_openForm, form)) { _openForm = null; }
            };
            form.Show(GetMainWindowOwner());
            AddInLogger.Info("Action Execute: 窗口已非模态打开");
            return true;
        }
        catch (Exception ex)
        {
            AddInLogger.Error("Action Execute 未处理异常", ex);
            MessageBox.Show("执行失败：" + ex.Message + "\n\n详见日志。",
                "文本批量编辑", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
    }

    /// <summary>
    /// 源语言：项目设置 TRANSLATEGUI.SOURCE_LANGUAGE（ProjectSettings 已锚定 PROJECT，键不带前缀）。
    /// 当项目源语言选择“##_##（对话语言）”时，设置值/只读属性可能返回占位编号 119（不是有效语言枚举），
    /// 此时改从用户层级对话框语言 USER.SYSTEM.GUI.LANGUAGE 解析真实语言；失败再读 Languages.GuiLanguage。
    /// </summary>
    private static ISOCode.Language ReadSourceLanguage(Project project)
    {
        // 1) TRANSLATEGUI.SOURCE_LANGUAGE（单值，索引 0，如 zh_CN；对话语言占位值可能为 119 或 ##_##）
        try
        {
            var raw = project.Settings.GetStringSetting("TRANSLATEGUI.SOURCE_LANGUAGE", 0);
            AddInLogger.Debug("TRANSLATEGUI.SOURCE_LANGUAGE 设置原始值=" + (raw == null ? "(null)" : "[" + raw + "]"));

            if (IsDialogLanguagePlaceholder(raw))
            {
                var dialogLanguage = ReadDialogLanguage();
                if (dialogLanguage.HasValue) { return dialogLanguage.Value; }
            }
            else if (LangHelper.TryParse(raw, out var l))
            {
                return l;
            }
        }
        catch (Exception ex)
        {
            AddInLogger.Debug("读取 TRANSLATEGUI.SOURCE_LANGUAGE 失败，回退属性：" + ex.GetType().Name + " " + ex.Message);
        }

        // 2) 只读项目属性 PROJ_SOURCELANGUAGE（Int64 语言编号；对话语言占位时同样可能是 119）
        try
        {
            var id = project.Properties.PROJ_SOURCELANGUAGE.ToInt();
            AddInLogger.Debug("PROJ_SOURCELANGUAGE 属性编号=" + id);

            if (id == DialogLanguagePlaceholderId)
            {
                var dialogLanguage = ReadDialogLanguage();
                if (dialogLanguage.HasValue) { return dialogLanguage.Value; }
            }
            else if (Enum.IsDefined(typeof(ISOCode.Language), id))
            {
                return (ISOCode.Language)id;
            }
        }
        catch (Exception ex)
        {
            AddInLogger.Error("读取 PROJ_SOURCELANGUAGE 属性失败", ex);
        }

        AddInLogger.Warn("无法确定源语言，默认 zh_CN");
        return ISOCode.Language.L_zh_CN;
    }

    private const int DialogLanguagePlaceholderId = 119;

    /// <summary>
    /// 判断项目源语言设置是否为“##_##（对话语言）”占位值。已确认 2.9.4 中 119 不是 ISOCode.Language 枚举成员。
    /// </summary>
    private static bool IsDialogLanguagePlaceholder(string? raw)
    {
        var s = raw == null ? string.Empty : raw.Trim();
        if (s.Length == 0) { return false; }
        if (s == "##_##" || string.Equals(s, "L___", StringComparison.Ordinal)) { return true; }

        return int.TryParse(s, out var id) && id == DialogLanguagePlaceholderId;
    }

    /// <summary>
    /// 读取当前 EPLAN 对话框语言：优先用户设置 USER.SYSTEM.GUI.LANGUAGE（Settings 默认 USER 级，不带 USER. 前缀），
    /// 再用运行时 API Languages.GuiLanguage 兜底。
    /// </summary>
    private static ISOCode.Language? ReadDialogLanguage()
    {
        try
        {
            var settings = new Settings();
            const string settingPath = "SYSTEM.GUI.LANGUAGE";
            if (settings.ExistSetting(settingPath))
            {
                var raw = settings.GetStringSetting(settingPath, 0);
                AddInLogger.Debug("源语言为对话语言占位值，USER.SYSTEM.GUI.LANGUAGE 原始值=" + (raw == null ? "(null)" : "[" + raw + "]"));
                if (LangHelper.TryParse(raw, out var lang))
                {
                    AddInLogger.Info("项目源语言=对话语言，按用户对话框语言解析为 " + lang);
                    return lang;
                }
            }
            else
            {
                AddInLogger.Debug("用户设置 SYSTEM.GUI.LANGUAGE 不存在，尝试 Languages.GuiLanguage");
            }
        }
        catch (Exception ex)
        {
            AddInLogger.Debug("读取用户对话框语言设置失败，尝试 Languages.GuiLanguage：" + ex.GetType().Name + " " + ex.Message);
        }

        try
        {
            using (var languages = new Languages())
            {
                var lang = languages.GuiLanguage.GetNumber();
                AddInLogger.Info("项目源语言=对话语言，按 Languages.GuiLanguage 解析为 " + lang);
                return lang;
            }
        }
        catch (Exception ex)
        {
            AddInLogger.Error("读取 Languages.GuiLanguage 失败", ex);
            return null;
        }
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
