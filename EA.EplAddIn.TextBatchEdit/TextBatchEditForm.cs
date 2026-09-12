using Eplan.EplApi.Base;
using Eplan.EplApi.DataModel;
using Eplan.EplApi.DataModel.Graphics;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace EA.EplAddIn.TextBatchEdit;

public class TextBatchEditForm : Form
{
    private readonly List<TextBase> _texts;
    private readonly ISOCode.Language _sourceLang;
    private readonly List<ISOCode.Language> _projectLangs; // 固定顺序，首位为源语言
    private readonly Dictionary<ISOCode.Language, int> _langCol = new();

    private DataGridView _grid = null!;
    private Button _applyBtn = null!;
    private Button _closeBtn = null!;

    private const int ColIndex = 0;
    private const int ColType = 1;
    private const int ColMultilang = 2;   // "多语言"复选框（原"翻译"）
    private const int ColNoAutoTrans = 3; // "不自动翻译"复选框（勾选=IsAutomaticallyTranslated=false）
    private const int ColSourceMirror = 4; // 独立"源语言"列，与项目语言区源语言列双向同步
    private const int LangColStart = 5;

    private bool _syncing; // 镜像列同步防递归

    public TextBatchEditForm(List<TextBase> texts,
        ISOCode.Language sourceLang, List<ISOCode.Language> projectLangs)
    {
        _texts = texts;
        _sourceLang = sourceLang;
        _projectLangs = projectLangs;

        Text = "批量修改选中文本（源语言：" + LangHelper.Code(sourceLang) + "，共 " + texts.Count + " 个文本）";
        StartPosition = FormStartPosition.CenterScreen;
        Width = 820;
        Height = 560;
        MinimumSize = new Size(560, 360);

        BuildGrid();
        BuildBottomBar();
        LoadRows();
    }

    private void BuildGrid()
    {
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
        _grid.Columns[ColIndex].Width = 42;
        _grid.Columns[ColIndex].ReadOnly = true;
        _grid.Columns[ColIndex].SortMode = DataGridViewColumnSortMode.NotSortable;

        _grid.Columns.Add("type", "对象类型");
        _grid.Columns[ColType].Width = 110;
        _grid.Columns[ColType].ReadOnly = true;
        _grid.Columns[ColType].SortMode = DataGridViewColumnSortMode.NotSortable;

        _grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "multilang", HeaderText = "多语言", Width = 60 });
        _grid.Columns[ColMultilang].SortMode = DataGridViewColumnSortMode.NotSortable;

        _grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "noAutoTrans", HeaderText = "不自动翻译", Width = 84 });
        _grid.Columns[ColNoAutoTrans].SortMode = DataGridViewColumnSortMode.NotSortable;

        // 独立源语言列（最左语言列）
        _grid.Columns.Add("srcMirror", "源语言(" + LangHelper.Code(_sourceLang) + ")");
        _grid.Columns[ColSourceMirror].Width = 200;
        _grid.Columns[ColSourceMirror].SortMode = DataGridViewColumnSortMode.NotSortable;

        // 项目语言列（含源语言本身），固定顺序
        for (var i = 0; i < _projectLangs.Count; i++)
        {
            var lang = _projectLangs[i];
            var colIdx = LangColStart + i;
            var title = LangHelper.Code(lang) + (lang == _sourceLang ? "（源语言）" : "");
            _grid.Columns.Add("lang_" + LangHelper.Code(lang), title);
            _grid.Columns[colIdx].Width = 200;
            _grid.Columns[colIdx].SortMode = DataGridViewColumnSortMode.NotSortable;
            _langCol[lang] = colIdx;
        }

        // 复选框切换 → 行可编辑状态 + 镜像列同步（两种复选框都即时提交）
        _grid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            var col = _grid.CurrentCell?.ColumnIndex ?? -1;
            if (_grid.IsCurrentCellDirty && (col == ColMultilang || col == ColNoAutoTrans))
            {
                _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            }
        };
        _grid.CellValueChanged += (_, e) =>
        {
            if (e.RowIndex < 0) { return; }
            if (e.ColumnIndex == ColMultilang)
            {
                var tr = Convert.ToBoolean(_grid[ColMultilang, e.RowIndex].Value ?? false);
                SetRowEditable(e.RowIndex, tr);
                AddInLogger.Debug("行" + (e.RowIndex + 1) + " 多语言复选框=" + tr);
            }
            else if (e.ColumnIndex == ColNoAutoTrans)
            {
                var noAuto = Convert.ToBoolean(_grid[ColNoAutoTrans, e.RowIndex].Value ?? false);
                AddInLogger.Debug("行" + (e.RowIndex + 1) + " 不自动翻译复选框=" + noAuto
                    + "（IsAutomaticallyTranslated=" + !noAuto + "）");
            }
            else if (!_syncing)
            {
                SyncMirror(e.RowIndex, e.ColumnIndex);
            }
        };

        // 右键菜单
        var menu = new ContextMenuStrip();
        menu.Items.Add("复制(&C)", null, (_, _) => CopySelection());
        menu.Items.Add("剪切(&X)", null, (_, _) => CutSelection());
        menu.Items.Add("粘贴(&V)", null, (_, _) => PasteClipboard());
        menu.Items.Add("清除内容(&D)", null, (_, _) => ClearSelection());
        menu.Opening += (_, _) => AddInLogger.Debug("右键菜单 Opening");
        _grid.ContextMenuStrip = menu;

        // 快捷键兜底（在 ACP/EPLAN 宿主里 DataGridView 快捷键可能被拦截）
        _grid.KeyDown += GridOnKeyDown;
        _grid.CellMouseClick += GridOnCellMouseClick;
        _grid.DataError += (_, e) =>
        {
            AddInLogger.Warn("网格 DataError: ctx=" + e.Context + " " + (e.Exception?.Message ?? ""));
            e.ThrowException = false;
        };

        Controls.Add(_grid);
    }

    /// <summary>未翻译：仅镜像列可编辑，项目语言区只读；已翻译：全部语言列可编辑。</summary>
    private void SetRowEditable(int row, bool translated)
    {
        for (var c = LangColStart; c < LangColStart + _projectLangs.Count; c++)
        {
            _grid[c, row].ReadOnly = !translated;
            _grid[c, row].Style.BackColor = translated
                ? System.Drawing.Color.White
                : System.Drawing.Color.FromArgb(245, 245, 245);
        }
    }

    /// <summary>镜像列 ↔ 项目语言区源语言列 双向同步。</summary>
    private void SyncMirror(int row, int changedCol)
    {
        var srcProjectCol = _langCol[_sourceLang];
        _syncing = true;
        try
        {
            if (changedCol == ColSourceMirror)
            {
                _grid[srcProjectCol, row].Value = _grid[ColSourceMirror, row].Value;
            }
            else if (changedCol == srcProjectCol)
            {
                _grid[ColSourceMirror, row].Value = _grid[srcProjectCol, row].Value;
            }
        }
        finally
        {
            _syncing = false;
        }
    }

    private void BuildBottomBar()
    {
        var panel = new BufferedPanel { Dock = DockStyle.Bottom, Height = 86 };

        var tip = new Label
        {
            Text = "勾选\"多语言\"=多语言文本（各语言列可编辑），不勾选=语言无关串（仅左侧源语言列可编辑）。\n"
                 + "\"不自动翻译\"对应文本属性 Do not translate automatically，与是否多语言相互独立。支持框选 Ctrl+C/X/V 块粘贴（Tab 分列、换行分行，可与 Excel 互贴），Delete 清除。",
            Dock = DockStyle.Top,
            Height = 40,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(10, 2, 10, 0),
        };

        var btnPanel = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 40, FlowDirection = FlowDirection.RightToLeft };

        _closeBtn = new Button { Text = "关闭", Width = 90, Height = 28, Margin = new Padding(6, 6, 10, 6) };
        _closeBtn.Click += (_, _) => Close();

        _applyBtn = new Button { Text = "写回 EPLAN", Width = 120, Height = 28, Margin = new Padding(6) };
        _applyBtn.Click += (_, _) => ApplyChanges();

        btnPanel.Controls.Add(_closeBtn);
        btnPanel.Controls.Add(_applyBtn);

        panel.Controls.Add(tip);
        panel.Controls.Add(btnPanel);
        Controls.Add(panel);

        // 底栏双缓冲再保险：列宽变化后强制底栏重绘
        _grid.ColumnWidthChanged += (_, _) => panel.Invalidate(true);
    }

    private void LoadRows()
    {
        AddInLogger.Info("LoadRows: count=" + _texts.Count
            + " 项目语言=[" + string.Join(",", _projectLangs.Select(LangHelper.Code)) + "] 源语言=" + _sourceLang);
        var unknown = ISOCode.Language.L___;

        _grid.Rows.Clear();
        for (var i = 0; i < _texts.Count; i++)
        {
            var t = _texts[i];
            var typeName = t.GetType().Name;
            var isTranslated = true;
            var noAutoTrans = false; // IsAutomaticallyTranslated=false
            var values = new Dictionary<ISOCode.Language, string>();
            string mirrorVal = string.Empty;

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
                AddInLogger.Debug("选中[" + i + "] 类型=" + typeName + " 页=" + pageName
                    + " DBID=" + Safe(() => t.DatabaseIdentifier.ToString())
                    + " 语言列表=[" + string.Join(",", langNames) + "]"
                    + " IsAutomaticallyTranslated=" + Safe(() => t.IsAutomaticallyTranslated.ToString())
                    + " InternalString=" + Preview(c.InternalString));
                noAutoTrans = !t.IsAutomaticallyTranslated;

                if (langs.Count == 0 || hasUnknown)
                {
                    isTranslated = false;
                    var byDisplay = Safe(() => c.GetStringToDisplay(_sourceLang));
                    var byUnknown = Safe(() => c.GetString(unknown));
                    var internalRaw = c.InternalString ?? string.Empty;
                    AddInLogger.Debug("  未翻译读法对比 GetStringToDisplay(" + _sourceLang + ")=" + Preview(byDisplay)
                        + " | GetString(L___)=" + Preview(byUnknown)
                        + " | InternalString=" + Preview(internalRaw));
                    mirrorVal = PickClean(byDisplay, byUnknown, internalRaw);
                }
                else
                {
                    foreach (var lang in _projectLangs)
                    {
                        var v = Safe(() => c.GetString(lang));
                        values[lang] = (v == "(null)" || v.StartsWith("(")) ? string.Empty : v;
                    }
                    mirrorVal = values.TryGetValue(_sourceLang, out var sv) ? sv : string.Empty;
                    var dbg = string.Join(" ", _projectLangs.Select(l => LangHelper.Code(l) + "=" + Preview(values.TryGetValue(l, out var x) ? x : "")));
                    AddInLogger.Debug("  已翻译读取 " + dbg);
                }
            }
            catch (Exception ex)
            {
                AddInLogger.Error("读取对象 " + i + " 文本失败", ex);
            }

            var rowIdx = _grid.Rows.Add();
            var row = _grid.Rows[rowIdx];
            row.Cells[ColIndex].Value = (i + 1).ToString();
            row.Cells[ColType].Value = typeName;
            row.Cells[ColMultilang].Value = isTranslated;
            row.Cells[ColNoAutoTrans].Value = noAutoTrans;
            row.Cells[ColSourceMirror].Value = mirrorVal;
            foreach (var lang in _projectLangs)
            {
                var col = _langCol[lang];
                if (isTranslated)
                {
                    row.Cells[col].Value = values.TryGetValue(lang, out var v) ? v : string.Empty;
                }
                else
                {
                    // 未翻译：源语言项目列也显示镜像值（同步），其余留空
                    row.Cells[col].Value = lang == _sourceLang ? mirrorVal : string.Empty;
                }
            }
            SetRowEditable(rowIdx, isTranslated);
        }
    }

    private void GridOnCellMouseClick(object? s, DataGridViewCellMouseEventArgs e)
    {
        if (e.Button != MouseButtons.Right || e.RowIndex < 0 || e.ColumnIndex < 0) { return; }
        AddInLogger.Debug("右键 行=" + (e.RowIndex + 1) + " 列=" + _grid.Columns[e.ColumnIndex].Name
            + " 选中格数=" + _grid.SelectedCells.Count);
        if (!_grid[e.ColumnIndex, e.RowIndex].Selected)
        {
            _grid.CurrentCell = _grid[e.ColumnIndex, e.RowIndex];
        }
    }

    private void GridOnKeyDown(object? sender, KeyEventArgs e)
    {
        if (IsEditing()) { return; } // 编辑中走原生，保证文字输入
        if (e.Control && e.KeyCode == Keys.C) { CopySelection(); e.Handled = true; }
        else if (e.Control && e.KeyCode == Keys.X) { CutSelection(); e.Handled = true; }
        else if (e.Control && e.KeyCode == Keys.V) { PasteClipboard(); e.Handled = true; }
        else if (e.KeyCode == Keys.Delete) { ClearSelection(); e.Handled = true; }
    }

    private bool IsEditing() => _grid.IsCurrentCellInEditMode || _grid.EditingControl != null;

    private List<DataGridViewCell> EditableSelectedCells() =>
        _grid.SelectedCells.Cast<DataGridViewCell>()
            .Where(c => c.RowIndex >= 0 && !c.ReadOnly
                && c.ColumnIndex != ColIndex && c.ColumnIndex != ColType
                && c.ColumnIndex != ColMultilang && c.ColumnIndex != ColNoAutoTrans)
            .ToList();

    private void CopySelection()
    {
        var cells = _grid.SelectedCells.Cast<DataGridViewCell>()
            .Where(c => c.RowIndex >= 0).ToList();
        if (cells.Count == 0) { AddInLogger.Debug("Copy: 无选区"); return; }
        var content = _grid.GetClipboardContent();
        if (content == null) { AddInLogger.Warn("CopySelection: GetClipboardContent 返回 null"); return; }
        Clipboard.SetDataObject(content);
        AddInLogger.Debug("Copy: 选中格=" + cells.Count);
    }

    private void CutSelection()
    {
        var cells = EditableSelectedCells();
        if (cells.Count == 0) { return; }
        CopySelection();
        foreach (var c in cells)
        {
            c.Value = string.Empty;
            SyncMirror(c.RowIndex, c.ColumnIndex);
        }
        AddInLogger.Debug("Cut: 清空 " + cells.Count + " 格");
    }

    private void ClearSelection()
    {
        var cells = EditableSelectedCells();
        if (cells.Count == 0) { return; }
        foreach (var c in cells)
        {
            c.Value = string.Empty;
            SyncMirror(c.RowIndex, c.ColumnIndex);
        }
        AddInLogger.Debug("Delete: 清空 " + cells.Count + " 格");
    }

    private void PasteClipboard()
    {
        var cells = EditableSelectedCells();
        if (cells.Count == 0)
        {
            AddInLogger.Debug("Paste: 无可编辑选区");
            MessageBox.Show("请先选择要粘贴的目标单元格（语言数据列）。", "批量修改选中文本",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var text = Clipboard.GetText(TextDataFormat.Text);
        if (string.IsNullOrEmpty(text)) { AddInLogger.Debug("Paste: 剪贴板无文本"); return; }

        var anchor = SelectionAnchorCell();
        if (anchor == null) { return; }

        var lines = text!.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
        var pasted = 0;
        for (var r = 0; r < lines.Length; r++)
        {
            var rowIdx = anchor.RowIndex + r;
            if (rowIdx >= _grid.Rows.Count) { break; }
            var fields = lines[r].Split('\t');
            for (var c = 0; c < fields.Length; c++)
            {
                var colIdx = anchor.ColumnIndex + c;
                if (colIdx >= LangColStart + _projectLangs.Count || colIdx < ColSourceMirror) { continue; }
                var cell = _grid[colIdx, rowIdx];
                if (cell.ReadOnly) { continue; }
                cell.Value = fields[c];
                SyncMirror(rowIdx, colIdx);
                pasted++;
            }
        }
        AddInLogger.Debug("Paste: 写入 " + pasted + " 格，起点 行" + (anchor.RowIndex + 1) + " 列" + _grid.Columns[anchor.ColumnIndex].Name);
    }

    /// <summary>粘贴起点：选区左上角（最小行、最小列）；单选退回 CurrentCell。</summary>
    private DataGridViewCell? SelectionAnchorCell()
    {
        var cells = _grid.SelectedCells.Cast<DataGridViewCell>()
            .Where(c => c.RowIndex >= 0).ToList();
        if (cells.Count == 0) { return null; }
        if (cells.Count == 1) { return _grid.CurrentCell; }
        var minRow = cells.Min(c => c.RowIndex);
        var minCol = cells.Where(c => c.RowIndex == minRow).Min(c => c.ColumnIndex);
        return _grid[minCol, minRow];
    }

    private void ApplyChanges()
    {
        AddInLogger.Info("ApplyChanges: begin, rows=" + _grid.Rows.Count);
        var unknown = ISOCode.Language.L___;
        var changedObjects = 0;
        var mismatchRows = new List<int>();

        UndoStep? undo = null;
        Transaction? txn = null;
        try
        {
            undo = new UndoManager().CreateUndoStep();
            undo.SetUndoDescription("批量修改选中文本");
            txn = new TransactionManager().CreateTransaction();

            for (var i = 0; i < _texts.Count; i++)
            {
                var t = _texts[i];
                if (!t.IsValid)
                {
                    AddInLogger.Warn("ApplyChanges: 行 " + (i + 1) + " 对象已失效，跳过");
                    continue;
                }

                var translated = Convert.ToBoolean(_grid[ColMultilang, i].Value ?? false);
                var noAutoTrans = Convert.ToBoolean(_grid[ColNoAutoTrans, i].Value ?? false);
                var wantAutoTrans = !noAutoTrans; // 勾选"不自动翻译" → IsAutomaticallyTranslated=false
                var mirrorVal = _grid[ColSourceMirror, i].Value as string ?? string.Empty;

                // 期望写入：语言 → 值
                var expected = new Dictionary<ISOCode.Language, string>();
                string mode;
                if (translated)
                {
                    foreach (var lang in _projectLangs)
                    {
                        var v = _grid[_langCol[lang], i].Value as string ?? string.Empty;
                        if (v.Length > 0) { expected[lang] = v; }
                    }
                    mode = "已翻译 语言数=" + expected.Count;
                }
                else
                {
                    expected[unknown] = mirrorVal;
                    mode = "未翻译(语言无关串)";
                }

                var beforeInternal = Preview(t.Contents.InternalString);
                var beforeAuto = t.IsAutomaticallyTranslated;
                var mls = new MultiLangString();
                foreach (var kv in expected) { mls.AddString(kv.Key, kv.Value); }
                t.Contents = mls;
                if (beforeAuto != wantAutoTrans)
                {
                    t.IsAutomaticallyTranslated = wantAutoTrans;
                }
                changedObjects++;

                AddInLogger.Debug("写回 行" + (i + 1) + " " + mode
                    + " 自动翻译 " + beforeAuto + "→" + wantAutoTrans + (beforeAuto != wantAutoTrans ? "(改)" : "")
                    + "\n    before internal=" + beforeInternal
                    + "\n    after : " + string.Join(" ", expected.Select(kv => LangHelper.Code(kv.Key) + "=" + Preview(kv.Value))));
            }

            txn.Commit();
            txn.Dispose();
            txn = null;
            undo.CloseOpenUndo();
            undo.Dispose();
            undo = null;

            // 回读校验：提交后重读每个对象，逐语言比对
            for (var i = 0; i < _texts.Count; i++)
            {
                var t = _texts[i];
                if (!t.IsValid) { continue; }
                var translated = Convert.ToBoolean(_grid[ColMultilang, i].Value ?? false);
                var wantAutoTrans = !Convert.ToBoolean(_grid[ColNoAutoTrans, i].Value ?? false);
                var mirrorVal = _grid[ColSourceMirror, i].Value as string ?? string.Empty;
                var c = t.Contents;
                bool rowOk = true;

                // 回读自动翻译标志
                if (t.IsAutomaticallyTranslated != wantAutoTrans)
                {
                    rowOk = false;
                    AddInLogger.Error("回读不一致 行" + (i + 1) + " IsAutomaticallyTranslated 期望=" + wantAutoTrans
                        + " 实际=" + t.IsAutomaticallyTranslated);
                }

                if (translated)
                {
                    foreach (var lang in _projectLangs)
                    {
                        var want = _grid[_langCol[lang], i].Value as string ?? string.Empty;
                        var actual = ReadSafe(c, lang);
                        if (!string.Equals(want, actual, StringComparison.Ordinal))
                        {
                            rowOk = false;
                            AddInLogger.Error("回读不一致 行" + (i + 1) + " " + LangHelper.Code(lang)
                                + " 期望=" + Preview(want) + " 实际=" + Preview(actual));
                        }
                    }
                }
                else
                {
                    var actual = PickClean(
                        Safe(() => c.GetStringToDisplay(_sourceLang)),
                        Safe(() => c.GetString(unknown)),
                        c.InternalString ?? string.Empty);
                    if (!string.Equals(mirrorVal, actual, StringComparison.Ordinal))
                    {
                        rowOk = false;
                        AddInLogger.Error("回读不一致 行" + (i + 1) + " 未翻译 期望=" + Preview(mirrorVal) + " 实际=" + Preview(actual));
                    }
                }
                if (!rowOk) { mismatchRows.Add(i + 1); }
            }

            AddInLogger.Info("ApplyChanges: 完成 对象数=" + changedObjects
                + (mismatchRows.Count > 0 ? " 回读不一致行=[" + string.Join(",", mismatchRows) + "]" : " 回读校验全部一致"));

            var msg = "已写回 " + changedObjects + " 个文本对象。\n可在 EPLAN 中用 Ctrl+Z 撤销本批修改。";
            if (mismatchRows.Count > 0)
            {
                msg = "写回 " + changedObjects + " 个对象，但回读校验发现 " + mismatchRows.Count
                    + " 行与期望不一致（行 " + string.Join(",", mismatchRows.Take(20)) + "）。\n请查看日志，勿假设已全部生效。";
                MessageBox.Show(msg, "批量修改选中文本", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            else
            {
                MessageBox.Show(msg, "批量修改选中文本", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
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

    private static string ReadSafe(MultiLangString c, ISOCode.Language lang)
    {
        try { return c.GetString(lang) ?? string.Empty; }
        catch { return string.Empty; }
    }

    private static string Safe(Func<string?> fn)
    {
        try { return fn() ?? "(null)"; }
        catch (Exception ex) { return "(" + ex.GetType().Name + ")"; }
    }

    private static string Preview(string? s)
    {
        if (s == null) { return "(null)"; }
        var one = s.Replace("\r", "\\r").Replace("\n", "\\n");
        return "[len=" + s.Length + "]" + (one.Length > 100 ? one.Substring(0, 100) + "…" : one);
    }

    private static string PickClean(params string[] candidates)
    {
        foreach (var c in candidates)
        {
            if (!string.IsNullOrEmpty(c) && !c.StartsWith("??_??@")) { return c; }
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
