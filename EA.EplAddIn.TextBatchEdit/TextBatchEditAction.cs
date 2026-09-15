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
    /// 收集本次要编辑的文本及所属项目。
    /// 优先图形编辑器中直接选中的文本；若没有，则取页导航器中选中的页（可多页/选节点），
    /// 枚举各页 AllPlacements 里的 TextBase。两路互斥（图面有文本即返回），各自不产生重复，无需去重。
    /// </summary>
    private static List<TextBase> CollectTexts(out Project project, out string sourceDesc)
    {
        project = null!;
        sourceDesc = string.Empty;
        var result = new List<TextBase>();

        var ss = new SelectionSet();

        // 1) 图形编辑器直接选中的文本
        var direct = ss.Selection ?? Array.Empty<StorableObject>();
        foreach (var o in direct)
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

        // 2) 页导航器选中的页 → 各页全部文本
        Page[] pages;
        try { pages = ss.GetSelectedPages() ?? Array.Empty<Page>(); }
        catch (Exception ex)
        {
            AddInLogger.Debug("GetSelectedPages 失败：" + ex.Message);
            return result;
        }
        AddInLogger.Info("CollectTexts: 页导航器选中页 count=" + pages.Length);

        foreach (var page in pages)
        {
            try
            {
                project ??= SafeProjectOf(page);
                var placements = page.AllPlacements;
                if (placements == null) { continue; }
                foreach (var pl in placements)
                {
                    if (pl is TextBase tb2) { result.Add(tb2); }
                }
            }
            catch (Exception ex)
            {
                AddInLogger.Warn("枚举页文本失败 页=" + SafePageName(page) + "：" + ex.Message);
            }
        }

        if (result.Count > 0)
        {
            project ??= SafeProjectOf(result[0]);
            sourceDesc = "页导航器 " + pages.Length + " 页，文本 " + result.Count + " 个";
            AddInLogger.Info("CollectTexts: " + sourceDesc);
        }
        return result;
    }

    private static Project SafeProjectOf(StorableObject o)
    {
        try { return o.Project; } catch (Exception ex) { AddInLogger.Debug("取对象项目失败：" + ex.Message); return null!; }
    }

    private static string SafePageName(Page p)
    {
        try { return p.Name; } catch { return "(取页名失败)"; }
    }

    /// <summary>当前选择集中是否存在文本对象（TextBase）。供 Execute 与右键菜单钩子共用。</summary>
    public static bool SelectionHasText()
    {
        try
        {
            var sel = new SelectionSet().Selection;
            return sel != null && sel.Any(o => o is TextBase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>页导航器中当前是否选中了一个或多个页（选中等高层级节点时，节点内页也算）。供页树右键钩子共用。</summary>
    public static bool SelectionHasPage()
    {
        try
        {
            var pages = new SelectionSet().GetSelectedPages();
            return pages != null && pages.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    [DeclareAction(ActionName)]
    public bool Execute(ActionCallingContext oActionName)
    {
        AddInLogger.Info("Action Execute: start");
        try
        {
            // 收集待编辑文本：
            //  1) 图形编辑器里直接选中的文本优先；
            //  2) 否则取页导航器中选中的页（支持多页/选节点，节点内所有页都会返回），枚举各页全部文本。
            var texts = CollectTexts(out var project, out var sourceDesc);

            if (texts.Count == 0)
            {
                MessageBox.Show("没有可编辑的文本。\n请在图形编辑器中框选文本，或在页导航器中选中一个/多个页后再执行。",
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
