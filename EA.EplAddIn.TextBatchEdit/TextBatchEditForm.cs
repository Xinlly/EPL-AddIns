using Eplan.EplApi.Base;
using Eplan.EplApi.DataModel;
using Eplan.EplApi.DataModel.Graphics;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace EA.EplAddIn.TextBatchEdit;

/// <summary>
/// 批量修改选中文本。显式区分两种文本：
///  - 未翻译（语言无关串，所有语言显示相同）：值取自/写回源语言列；"翻译"复选框不勾
///  - 已翻译（多语言）：中文 zh_CN / 英文 en_US 分列；复选框勾选
/// 写回统一新建 MultiLangString 后经 TextBase.Contents 的 setter 赋回（getter 对象就地修改不落库）。
/// </summary>
public class TextBatchEditForm : Form
{
    private const int ColIndex = 0;
    private const int ColType = 1;
    private const int ColTranslated = 2;
    private const int ColZh = 3;
    private const int ColEn = 4;

    private readonly List<TextBase> _texts;
    private readonly ISOCode.Language _sourceLang;
    private readonly DataGridView _grid;
    private ContextMenuStrip? _contextMenu;

    public TextBatchEditForm(List<TextBase> texts)
    {
        _texts = texts;
        AddInLogger.Debug("TextBatchEditForm..ctor: texts=" + texts.Count);

        // 项目源语言：只读项目属性 PROJ_SOURCELANGUAGE（Int64，值即 ISOCode.Language 枚举编号）
        _sourceLang = ISOCode.Language.L_zh_CN;
        string sourceLangName;
        try
        {
            var srcInt = texts[0].Project.Properties.PROJ_SOURCELANGUAGE.ToInt();
            _sourceLang = (ISOCode.Language)srcInt;
            using (var iso = new ISOCode())
            {
                sourceLangName = iso.GetLongName(_sourceLang);
            }
        }
        catch (Exception ex)
        {
            AddInLogger.Error("读取项目源语言失败，按 zh_CN 处理", ex);
            sourceLangName = "zh_CN（读取失败，按默认）";
        }
        AddInLogger.Info("项目源语言=" + _sourceLang + " (" + sourceLangName + ")");

        Text = "批量修改选中文本 — 源语言：" + sourceLangName;
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(820, 500);
        MinimumSize = new Size(560, 320);

        _grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None,
            SelectionMode = DataGridViewSelectionMode.CellSelect,
            MultiSelect = true,
            RowHeadersVisible = false,
            BackgroundColor = System.Drawing.Color.White,
            ClipboardCopyMode = DataGridViewClipboardCopyMode.EnableWithoutHeaderText,
        };
        _grid.Columns.Add("idx", "#");
        _grid.Columns.Add("type", "对象类型");
        _grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "translated", HeaderText = "翻译", Width = 48 });
        var zhHeader = "中文 zh_CN" + (_sourceLang == ISOCode.Language.L_zh_CN ? "（源语言）" : "");
        var enHeader = "英文 en_US" + (_sourceLang == ISOCode.Language.L_en_US ? "（源语言）" : "");
        _grid.Columns.Add("zh", zhHeader);
        _grid.Columns.Add("en", enHeader);
        _grid.Columns[ColIndex].Width = 44;
        _grid.Columns[ColIndex].ReadOnly = true;
        _grid.Columns[ColType].Width = 100;
        _grid.Columns[ColType].ReadOnly = true;
        _grid.Columns[ColZh].Width = 290;
        _grid.Columns[ColEn].Width = 290;

        var unknown = ISOCode.Language.L___;
        var zh = ISOCode.Language.L_zh_CN;
        var en = ISOCode.Language.L_en_US;

        for (var i = 0; i < texts.Count; i++)
        {
            var t = texts[i];
            var isTranslated = true;
            var sourceVal = string.Empty;
            var zhVal = string.Empty;
            var enVal = string.Empty;
            try
            {
                var c = t.Contents;
                var langs = new LanguageList();
                c.GetLanguageList(ref langs);
                var hasUnknown = false;
                var langNames = new List<string>();
                for (var k = 0; k < langs.Count; k++)
                {
                    var l = langs.get_Language(k);
                    langNames.Add(l.ToString());
                    if (l == unknown) { hasUnknown = true; }
                }

                string pageName;
                try { pageName = t.Page?.Name ?? "(无页)"; } catch { pageName = "(取页失败)"; }
                AddInLogger.Debug("选中[" + i + "] 类型=" + t.GetType().Name
                    + " 页=" + pageName + " DBID=" + Safe(() => t.DatabaseIdentifier.ToString())
                    + " 语言列表=[" + string.Join(",", langNames) + "]"
                    + " InternalString=" + Preview(c.InternalString));

                if (langs.Count == 0 || hasUnknown)
                {
                    // 未翻译（语言无关串）：多路读取，DEBUG 留证，取第一个不带内部前缀的干净值
                    isTranslated = false;
                    var byDisplay = Safe(() => c.GetStringToDisplay(_sourceLang));
                    var byUnknown = Safe(() => c.GetString(unknown));
                    var internalRaw = c.InternalString ?? string.Empty;
                    AddInLogger.Debug("  未翻译读法对比 GetStringToDisplay(" + _sourceLang + ")=" + Preview(byDisplay)
                        + " | GetString(L___)=" + Preview(byUnknown)
                        + " | InternalString=" + Preview(internalRaw));
                    sourceVal = PickClean(byDisplay, byUnknown, internalRaw);
                }
                else
                {
                    zhVal = c.GetString(zh) ?? string.Empty;
                    enVal = c.GetString(en) ?? string.Empty;
                    AddInLogger.Debug("  已翻译读取 zh_CN=" + Preview(zhVal) + " en_US=" + Preview(enVal));
                    if (_sourceLang != zh && _sourceLang != en)
                    {
                        AddInLogger.Warn("行" + (i + 1) + " 源语言 " + _sourceLang + " 非中英，未翻译值映射暂不支持，按空处理");
                    }
                }
            }
            catch (Exception ex)
            {
                AddInLogger.Error("读取对象 " + i + " 文本失败", ex);
            }

            if (!isTranslated)
            {
                if (_sourceLang == en) { enVal = sourceVal; }
                else { zhVal = sourceVal; }
            }

            _grid.Rows.Add((i + 1).ToString(), t.GetType().Name, isTranslated, zhVal, enVal);
            ApplyRowEditState(i, isTranslated);
        }

        BuildContextMenu();
        _grid.CellMouseClick += GridOnCellMouseClick;
        _grid.CurrentCellDirtyStateChanged += GridOnDirtyStateChanged;
        _grid.CellValueChanged += GridOnCellValueChanged;
        _grid.KeyDown += GridOnKeyDown;

        var buttonPanel = new BufferedPanel { Dock = DockStyle.Bottom, Height = 44 };
        _grid.ColumnWidthChanged += (_, _) => buttonPanel.Invalidate(true);

        var hint = new Label
        {
            Text = "勾选\"翻译\"=多语言文本；不勾选=未翻译（仅源语言列生效）。Ctrl+C/X/V 块粘贴；Delete 清空；右键菜单",
            Location = new Point(10, 14),
            AutoSize = true,
        };
        var applyButton = new Button { Text = "写回 EPLAN", Size = new Size(110, 28) };
        applyButton.Click += (_, _) => ApplyChanges();
        var closeButton = new Button { Text = "关闭", Size = new Size(72, 28) };
        closeButton.Click += (_, _) => Close();
        buttonPanel.Controls.Add(hint);
        buttonPanel.Controls.Add(applyButton);
        buttonPanel.Controls.Add(closeButton);
        buttonPanel.Resize += (_, _) =>
        {
            closeButton.Location = new Point(buttonPanel.Width - closeButton.Width - 12, 8);
            applyButton.Location = new Point(closeButton.Left - applyButton.Width - 8, 8);
        };

        Controls.Add(buttonPanel);
        Controls.Add(_grid);
        AcceptButton = applyButton;
        CancelButton = closeButton;
    }

    /// <summary>按翻译勾选状态切换该行非源语言列的可编辑性。</summary>
    private void ApplyRowEditState(int row, bool translated)
    {
        var nonSourceCol = _sourceLang == ISOCode.Language.L_en_US ? ColZh : ColEn;
        _grid[nonSourceCol, row].ReadOnly = !translated;
        _grid[nonSourceCol, row].Style.BackColor = translated
            ? System.Drawing.Color.White
            : System.Drawing.Color.FromArgb(240, 240, 240);
    }

    // 复选框提交即时生效（默认需失去焦点）
    private void GridOnDirtyStateChanged(object? sender, EventArgs e)
    {
        if (_grid.IsCurrentCellDirty && _grid.CurrentCell?.ColumnIndex == ColTranslated)
        {
            _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        }
    }

    private void GridOnCellValueChanged(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0 || e.ColumnIndex != ColTranslated) { return; }
        var translated = Convert.ToBoolean(_grid[ColTranslated, e.RowIndex].Value ?? false);
        ApplyRowEditState(e.RowIndex, translated);
        AddInLogger.Debug("行" + (e.RowIndex + 1) + " 翻译标志=" + translated);
    }

    private bool IsEditing() => _grid.IsCurrentCellInEditMode;

    // ---------- 右键菜单 ----------

    private void BuildContextMenu()
    {
        _contextMenu = new ContextMenuStrip();
        _contextMenu.Items.Add("剪切(&T)\tCtrl+X", null, (_, _) => CutSelection());
        _contextMenu.Items.Add("复制(&C)\tCtrl+C", null, (_, _) => CopySelection());
        _contextMenu.Items.Add("粘贴(&P)\tCtrl+V", null, (_, _) => PasteClipboard());
        _contextMenu.Items.Add("清除内容(&D)\tDel", null, (_, _) => ClearSelectionValues());
        _contextMenu.Items.Add(new ToolStripSeparator());
        _contextMenu.Items.Add("全选(&A)\tCtrl+A", null, (_, _) => _grid.SelectAll());
        _grid.ContextMenuStrip = _contextMenu;
    }

    private void GridOnCellMouseClick(object? sender, DataGridViewCellMouseEventArgs e)
    {
        if (e.Button != MouseButtons.Right || e.RowIndex < 0 || e.ColumnIndex < 0) { return; }
        var cell = _grid[e.ColumnIndex, e.RowIndex];
        if (!cell.Selected) { _grid.CurrentCell = cell; }
    }

    // ---------- 快捷键（编辑态不拦截；复选框列不拦截）----------

    private void GridOnKeyDown(object? sender, KeyEventArgs e)
    {
        if (IsEditing()) { return; }
        if (e.Control && e.KeyCode == Keys.C) { CopySelection(); e.Handled = true; }
        else if (e.Control && e.KeyCode == Keys.X) { CutSelection(); e.Handled = true; }
        else if (e.Control && e.KeyCode == Keys.V) { PasteClipboard(); e.Handled = true; }
        else if (e.Control && e.KeyCode == Keys.A) { _grid.SelectAll(); e.Handled = true; }
        else if (e.KeyCode == Keys.Delete) { ClearSelectionValues(); e.Handled = true; }
    }

    // ---------- 剪贴板（只读列 #/类型/翻译 不接受粘贴）----------

    private void CopySelection()
    {
        if (_grid.SelectedCells.Count == 0) { return; }
        try
        {
            var content = _grid.GetClipboardContent();
            if (content == null) { return; }
            Clipboard.SetDataObject(content);
            AddInLogger.Info("CopySelection: cells=" + _grid.SelectedCells.Count);
        }
        catch (Exception ex) { AddInLogger.Error("CopySelection 异常", ex); }
    }

    private void CutSelection()
    {
        if (_grid.SelectedCells.Count == 0) { return; }
        CopySelection();
        ClearSelectionValues();
    }

    private void PasteClipboard()
    {
        var anchor = SelectionAnchorCell;
        if (anchor == null) { return; }
        if (anchor.ReadOnly || anchor.ColumnIndex == ColTranslated)
        {
            AddInLogger.Warn("Paste: 起点为只读/复选框列，忽略");
            return;
        }

        var text = Clipboard.GetDataObject()?.GetData(DataFormats.Text) as string;
        if (string.IsNullOrEmpty(text)) { return; }

        var lines = text!.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
        var startRow = anchor.RowIndex;
        var startCol = anchor.ColumnIndex;
        var pasted = 0;
        foreach (DataGridViewCell c in _grid.SelectedCells) { c.Selected = false; }

        for (var r = 0; r < lines.Length; r++)
        {
            var targetRow = startRow + r;
            if (targetRow >= _grid.Rows.Count) { break; }
            var fields = lines[r].Split('\t');
            for (var c = 0; c < fields.Length; c++)
            {
                var targetCol = startCol + c;
                if (targetCol >= _grid.ColumnCount) { break; }
                if (targetCol == ColIndex || targetCol == ColType || targetCol == ColTranslated) { continue; }
                if (_grid[targetCol, targetRow].ReadOnly) { continue; }
                _grid[targetCol, targetRow].Value = fields[c];
                _grid[targetCol, targetRow].Selected = true;
                pasted++;
            }
        }
        AddInLogger.Info("Paste: 起点(r" + startRow + ",c" + startCol + ") 写入格数=" + pasted);
    }

    private DataGridViewCell? SelectionAnchorCell
    {
        get
        {
            if (_grid.SelectedCells.Count == 0) { return _grid.CurrentCell; }
            var minRow = int.MaxValue; var minCol = int.MaxValue;
            foreach (DataGridViewCell cell in _grid.SelectedCells)
            {
                minRow = Math.Min(minRow, cell.RowIndex);
                minCol = Math.Min(minCol, cell.ColumnIndex);
            }
            return _grid[minCol, minRow];
        }
    }

    private void ClearSelectionValues()
    {
        var cleared = 0;
        foreach (DataGridViewCell cell in _grid.SelectedCells)
        {
            if (!cell.ReadOnly && cell.ColumnIndex != ColTranslated && cell.RowIndex != _grid.NewRowIndex)
            {
                cell.Value = null;
                cleared++;
            }
        }
        AddInLogger.Info("ClearSelectionValues: cleared=" + cleared);
    }

    // ---------- 写回 EPLAN ----------

    private void ApplyChanges()
    {
        AddInLogger.Info("ApplyChanges: begin, rows=" + _grid.Rows.Count);
        var zh = ISOCode.Language.L_zh_CN;
        var en = ISOCode.Language.L_en_US;
        var unknown = ISOCode.Language.L___;
        var changedObjects = 0;

        // 进入撤销栈的正确范式：UndoStep 包住一个 Transaction，修改后 Commit；
        // 绝不能调 undo.DoUndo()（那是编程式立即撤销）。LockingStep 只是对象锁，与撤销无关，
        // 且本代码运行在内置 IEplAction 内（平台隐式提供 LockingStep），无需显式创建。
        UndoStep? undo = null;
        Transaction? txn = null;
        try
        {
            undo = new UndoManager().CreateUndoStep();
            undo.SetUndoDescription("批量修改选中文本（中英文）");
            txn = new TransactionManager().CreateTransaction();

            for (var i = 0; i < _texts.Count; i++)
            {
                var t = _texts[i];
                if (!t.IsValid)
                {
                    AddInLogger.Warn("ApplyChanges: 行 " + (i + 1) + " 对象已失效，跳过");
                    continue;
                }

                var translated = Convert.ToBoolean(_grid[ColTranslated, i].Value ?? false);
                var zhVal = _grid[ColZh, i].Value as string ?? string.Empty;
                var enVal = _grid[ColEn, i].Value as string ?? string.Empty;

                // 记录 before
                var beforeMls = t.Contents;
                var beforeZh = Safe(() => beforeMls.GetString(zh));
                var beforeEn = Safe(() => beforeMls.GetString(en));
                var beforeInternal = Preview(beforeMls.InternalString);

                // 新建 MultiLangString 写好后经 setter 整体赋回（对 getter 对象就地改不落库）
                var mls = new MultiLangString();
                string mode;
                if (translated)
                {
                    if (zhVal.Length > 0) { mls.AddString(zh, zhVal); }
                    if (enVal.Length > 0) { mls.AddString(en, enVal); }
                    mode = "已翻译 zh/en";
                }
                else
                {
                    // 未翻译：语言无关串。用 AddString(L___) 写入（L___ 官方语义=设置语言无关串）
                    var sourceVal = _sourceLang == en ? enVal : zhVal;
                    mls.AddString(unknown, sourceVal);
                    mode = "未翻译(语言无关串) 源语言=" + _sourceLang;
                }

                t.Contents = mls;
                changedObjects++;
                AddInLogger.Debug("写回 行" + (i + 1) + " " + mode
                    + "\n    before: zh=" + Preview(beforeZh) + " en=" + Preview(beforeEn) + " internal=" + beforeInternal
                    + "\n    after : zh=" + Preview(zhVal) + " en=" + Preview(enVal));
            }

            txn.Commit();
            txn.Dispose();
            txn = null;
            // 关闭撤销步：提交的修改成为一个可 Ctrl+Z 的撤销点（不调 DoUndo）
            undo.CloseOpenUndo();
            undo.Dispose();
            undo = null;

            AddInLogger.Info("ApplyChanges: 完成 对象数=" + changedObjects);
            MessageBox.Show("已写回 " + changedObjects + " 个文本对象。\n可在 EPLAN 中用 Ctrl+Z 撤销本批修改。",
                "批量修改选中文本", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            AddInLogger.Error("ApplyChanges 异常，尝试中止事务", ex);
            try { txn?.Abort(); } catch (Exception abortEx) { AddInLogger.Error("事务 Abort 失败", abortEx); }
            MessageBox.Show("写回失败：" + ex.Message, "批量修改选中文本",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            txn?.Dispose();
            undo?.Dispose();
        }
    }

    private static string Safe(Func<string?> fn)
    {
        try { return fn() ?? "(null)"; }
        catch (Exception ex) { return "(" + ex.GetType().Name + ")"; }
    }

    /// <summary>日志预览：长度 + 截断 100 字符，换行转义。</summary>
    private static string Preview(string? s)
    {
        if (s == null) { return "(null)"; }
        var one = s.Replace("\r", "\\r").Replace("\n", "\\n");
        return "[len=" + s.Length + "]" + (one.Length > 100 ? one.Substring(0, 100) + "…" : one);
    }

    /// <summary>从多路读法中取第一个不带内部语言标记（??_??@…;）的干净值；都带则剥掉标记。</summary>
    private static string PickClean(params string[] candidates)
    {
        foreach (var c in candidates)
        {
            if (!string.IsNullOrEmpty(c) && !c.StartsWith("??_??@"))
            {
                return c;
            }
        }
        foreach (var c in candidates)
        {
            if (string.IsNullOrEmpty(c)) { continue; }
            var v = c;
            if (v.StartsWith("??_??@")) { v = v.Substring(6); }
            if (v.EndsWith(";")) { v = v.Substring(0, v.Length - 1); }
            return v;
        }
        return string.Empty;
    }

    private class BufferedPanel : Panel
    {
        public BufferedPanel() { DoubleBuffered = true; }
    }
}
