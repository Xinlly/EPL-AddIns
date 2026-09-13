using Eplan.EplApi.Base;
using Eplan.EplApi.DataModel;
using Eplan.EplApi.DataModel.Graphics;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace EA.EplAddIn.TextBatchEdit;

public class TextBatchEditForm : Form
{
    private readonly List<TextBase> _texts;
    private readonly ISOCode.Language _sourceLang;
    private readonly List<ISOCode.Language> _projectLangs; // 固定顺序，首位为源语言

    // 每组都有“一个文本/单语言列 + N 个语言列（首位是源语言）”。
    // 文本列与源语言列始终同值、双向同步（未翻译文本的值即落在这对列上）。
    private int _origTextCol;                                  // 原文本（中文）
    private readonly Dictionary<ISOCode.Language, int> _origLangCol = new(); // 原中文/原英文…
    private int _newTextCol;                                   // 源语言（中文）——单语言文本编辑列
    private readonly Dictionary<ISOCode.Language, int> _newLangCol = new();  // 中文/英文…
    // 每行“已保存”基线，用于脏检查
    private readonly List<RowState> _baseline = new();

    private DataGridView _grid = null!;
    private CheckBox _showOrigChk = null!;
    private Button _okBtn = null!;
    private Button _cancelBtn = null!;
    private Button _applyBtn = null!;

    private const int ColIndex = 0;
    private const int ColType = 1;
    private const int ColMultilang = 2;   // “多语言”复选框
    private const int ColNoAutoTrans = 3; // “不自动翻译”复选框（勾选=IsAutomaticallyTranslated=false）
    private const int OrigTextCol = 4;    // 原值·文本列
    private int OrigLangStart => 5;                          // 原值·语言列起点
    private int NewTextColIdx => OrigLangStart + _projectLangs.Count;        // 新值·文本列
    private int NewLangStart => NewTextColIdx + 1;                           // 新值·语言列起点

    private bool _syncing; // 程序化填充/双向同步时抑制事件联动

    public TextBatchEditForm(List<TextBase> texts,
        ISOCode.Language sourceLang, List<ISOCode.Language> projectLangs)
    {
        _texts = texts;
        _sourceLang = sourceLang;
        _projectLangs = projectLangs;
        _origTextCol = OrigTextCol;
        _newTextCol = NewTextColIdx;

        Text = "文本批量编辑（源语言：" + LangHelper.Code(sourceLang) + "，共 " + texts.Count + " 个文本）";
        StartPosition = FormStartPosition.CenterScreen;
        Width = 1180;
        Height = 560;
        MinimumSize = new Size(720, 360);

        BuildGrid();
        BuildTabs();
        BuildBottomBar();
        LoadRows();
        UpdateApplyEnabled();
        // 窗体真正显示（grid 句柄已建）后，再按复选框状态同步一次，确保原值列默认隐藏
        Shown += (_, _) => SetOrigColumnsVisible(_showOrigChk.Checked);
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
        _grid.Columns[ColType].Width = 100;
        _grid.Columns[ColType].ReadOnly = true;
        _grid.Columns[ColType].SortMode = DataGridViewColumnSortMode.NotSortable;

        _grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "multilang", HeaderText = "多语言", Width = 60 });
        _grid.Columns[ColMultilang].SortMode = DataGridViewColumnSortMode.NotSortable;

        _grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "noAutoTrans", HeaderText = "不自动翻译", Width = 84 });
        _grid.Columns[ColNoAutoTrans].SortMode = DataGridViewColumnSortMode.NotSortable;

        var dnSource = LangHelper.DisplayName(_sourceLang);

        // —— 原值列（只读、默认隐藏，可用“显示原值”展开）——
        _grid.Columns.Add("orig_text", "原文本（" + dnSource + "）");
        _grid.Columns[_origTextCol].Width = 190;
        _grid.Columns[_origTextCol].ReadOnly = true;
        _grid.Columns[_origTextCol].Visible = false; // 默认隐藏，与其余原值列一致，由“显示原值”统一展开
        _grid.Columns[_origTextCol].SortMode = DataGridViewColumnSortMode.NotSortable;
        _grid.Columns[_origTextCol].DefaultCellStyle.BackColor = System.Drawing.Color.FromArgb(245, 245, 245);

        for (var i = 0; i < _projectLangs.Count; i++)
        {
            var lang = _projectLangs[i];
            var idx = OrigLangStart + i;
            var title = "原" + LangHelper.DisplayName(lang); // 原中文 / 原英文…
            _grid.Columns.Add("orig_" + LangHelper.Code(lang), title);
            _grid.Columns[idx].Width = 180;
            _grid.Columns[idx].ReadOnly = true;
            _grid.Columns[idx].Visible = false; // 默认隐藏，由“显示原值”统一展开
            _grid.Columns[idx].SortMode = DataGridViewColumnSortMode.NotSortable;
            _grid.Columns[idx].DefaultCellStyle.BackColor = System.Drawing.Color.FromArgb(245, 245, 245);
            _origLangCol[lang] = idx;
        }

        // —— 新值列（可编辑）——
        _grid.Columns.Add("new_text", "源语言（" + dnSource + "）");
        _grid.Columns[_newTextCol].Width = 200;
        _grid.Columns[_newTextCol].SortMode = DataGridViewColumnSortMode.NotSortable;

        for (var i = 0; i < _projectLangs.Count; i++)
        {
            var lang = _projectLangs[i];
            var idx = NewLangStart + i;
            var title = LangHelper.DisplayName(lang); // 中文 / 英文…
            _grid.Columns.Add("new_" + LangHelper.Code(lang), title);
            _grid.Columns[idx].Width = 200;
            _grid.Columns[idx].SortMode = DataGridViewColumnSortMode.NotSortable;
            _newLangCol[lang] = idx;
        }

        // 复选框即时提交
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
            if (e.RowIndex < 0 || _syncing) { return; }
            if (e.ColumnIndex == ColMultilang)
            {
                SetRowEditable(e.RowIndex, Convert.ToBoolean(_grid[ColMultilang, e.RowIndex].Value ?? false));
                AddInLogger.Debug("行" + (e.RowIndex + 1) + " 多语言复选框="
                    + Convert.ToBoolean(_grid[ColMultilang, e.RowIndex].Value ?? false));
            }
            else if (e.ColumnIndex == ColNoAutoTrans)
            {
                var noAuto = Convert.ToBoolean(_grid[ColNoAutoTrans, e.RowIndex].Value ?? false);
                AddInLogger.Debug("行" + (e.RowIndex + 1) + " 不自动翻译=" + noAuto + "（IsAutomaticallyTranslated=" + !noAuto + "）");
            }
            else if (e.ColumnIndex == _newTextCol)
            {
                SyncPair(e.RowIndex, _newTextCol, _newLangCol[_sourceLang]); // 文本列 → 源语言列
            }
            else if (e.ColumnIndex == _newLangCol[_sourceLang])
            {
                SyncPair(e.RowIndex, _newLangCol[_sourceLang], _newTextCol); // 源语言列 → 文本列
            }
            UpdateApplyEnabled();
        };

        // 右键菜单
        var menu = new ContextMenuStrip();
        menu.Items.Add("复制(&C)", null, (_, _) => CopySelection());
        menu.Items.Add("剪切(&X)", null, (_, _) => CutSelection());
        menu.Items.Add("粘贴(&V)", null, (_, _) => PasteClipboard());
        menu.Items.Add("清除内容(&D)", null, (_, _) => ClearSelection());
        _grid.ContextMenuStrip = menu;

        _grid.KeyDown += GridOnKeyDown;
        _grid.CellMouseClick += GridOnCellMouseClick;
        // 正在编辑最后一格（焦点未离开、CellValueChanged 未触发）时，一旦有输入即乐观点亮应用；
        // 是否真有改动仍以保存前 EndEdit 后的精确脏检查为准，避免要点两次。
        _grid.EditingControlShowing += (_, e) =>
        {
            if (e.Control is TextBox tb)
            {
                tb.TextChanged -= EditingTextChanged;
                tb.TextChanged += EditingTextChanged;
            }
        };
        _grid.DataError += (_, e) =>
        {
            AddInLogger.Warn("网格 DataError: ctx=" + e.Context + " " + (e.Exception?.Message ?? ""));
            e.ThrowException = false;
        };
    }

    /// <summary>把源列当前值同步到配对列（两列承载同一份源语言/单语言文本）。</summary>
    private void SyncPair(int row, int fromCol, int toCol)
    {
        _syncing = true;
        try { _grid[toCol, row].Value = _grid[fromCol, row].Value; }
        finally { _syncing = false; }
    }

    private void BuildTabs()
    {
        var tabs = new TabControl { Dock = DockStyle.Fill };

        // 标签页 1：编辑（顶部开关 + 表格）
        var tabEdit = new TabPage("编辑");

        var bar = new Panel { Dock = DockStyle.Top, Height = 30 };
        _showOrigChk = new CheckBox
        {
            Text = "显示原值",
            Dock = DockStyle.Left,
            Width = 120,
            Checked = false,
            TextAlign = ContentAlignment.MiddleLeft,
        };
        _showOrigChk.CheckedChanged += (_, _) => SetOrigColumnsVisible(_showOrigChk.Checked);
        bar.Controls.Add(_showOrigChk);

        tabEdit.Controls.Add(_grid); // 先加：Fill 占满
        tabEdit.Controls.Add(bar);   // 再加：Top 压在上方

        // 标签页 2：说明
        var tabHelp = new TabPage("说明");
        var help = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.TopLeft,
            Padding = new Padding(12),
            Font = new Font(SystemFonts.MessageBoxFont.FontFamily, 9.5f),
            Text =
                "【列说明】\n" +
                "  • 多语言：勾选=多语言文本（各语言新值列可编辑）；不勾选=单语言/语言无关串（仅源语言内容可编辑）。\n" +
                "  • 不自动翻译：对应文本对象属性 “Do not translate automatically”，与是否多语言相互独立。\n" +
                "  • 每组都有一个“文本列”和各“语言列”：原值侧为“原文本（中文）/原中文/原英文…”，新值侧为“源语言（中文）/中文/英文…”。\n" +
                "  • 文本列与源语言列（中文）是同一份内容、始终同步：改任一格另一格跟随；单语言文本的值就写在这对列上。\n" +
                "  • 原值列只读，用顶部“显示原值”展开/隐藏，便于逐列对照。\n\n" +
                "【写回规则】\n" +
                "  • 只写发生改动的对象，未改的对象不写、不产生撤销点。\n" +
                "  • 应用：有未保存修改时才可点，写回后保留窗口继续编辑；无修改时按钮置灰。\n" +
                "  • 确定：等同先应用一次，成功后关闭窗口；无修改则直接关闭。\n" +
                "  • 取消：放弃未保存修改并关闭。\n" +
                "  • 每次“应用/确定”若确有改动，整批合并为一个撤销点，可在 EPLAN 中 Ctrl+Z 一次撤销。\n\n" +
                "【编辑操作】\n" +
                "  • 框选多格后 Ctrl+C / Ctrl+X / Ctrl+V 块复制粘贴（Tab 分列、换行分行，可与 Excel 互贴），Delete 清除。",
        };
        tabHelp.Controls.Add(help);

        tabs.TabPages.Add(tabEdit);
        tabs.TabPages.Add(tabHelp);
        Controls.Add(tabs);
    }

    private void SetOrigColumnsVisible(bool visible)
    {
        _grid.Columns[_origTextCol].Visible = visible;
        foreach (var idx in _origLangCol.Values) { _grid.Columns[idx].Visible = visible; }
    }

    /// <summary>
    /// 可编辑性：文本列与源语言（中文）列始终可编辑且互相同步；
    /// 其余语言列仅“多语言”时可编辑。原值列恒只读。
    /// </summary>
    private void SetRowEditable(int row, bool translated)
    {
        foreach (var lang in _projectLangs)
        {
            var c = _newLangCol[lang];
            var editable = translated || lang == _sourceLang;
            _grid[c, row].ReadOnly = !editable;
            _grid[c, row].Style.BackColor = editable
                ? System.Drawing.Color.White
                : System.Drawing.Color.FromArgb(245, 245, 245);
        }
        // 文本列（单语言内容）始终可编辑
        _grid[_newTextCol, row].ReadOnly = false;
        _grid[_newTextCol, row].Style.BackColor = System.Drawing.Color.White;
    }

    private void BuildBottomBar()
    {
        var panel = new BufferedPanel { Dock = DockStyle.Bottom, Height = 46 };

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 1, ColumnCount = 2 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));

        var btnPanel = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Anchor = AnchorStyles.Right,
            Margin = new Padding(0),
        };

        _okBtn = new Button { Text = "确定", Width = 90, Height = 30, Margin = new Padding(0, 6, 8, 0) };
        _okBtn.Click += (_, _) => { if (SaveDirty(quietSuccess: true)) { Close(); } };

        _cancelBtn = new Button { Text = "取消", Width = 90, Height = 30, Margin = new Padding(0, 6, 8, 0) };
        _cancelBtn.Click += (_, _) => Close();

        _applyBtn = new Button { Text = "应用", Width = 90, Height = 30, Margin = new Padding(0, 6, 12, 0) };
        _applyBtn.Click += (_, _) => SaveDirty(quietSuccess: true);

        btnPanel.Controls.Add(_okBtn);
        btnPanel.Controls.Add(_cancelBtn);
        btnPanel.Controls.Add(_applyBtn);

        layout.Controls.Add(new Panel(), 0, 0);
        layout.Controls.Add(btnPanel, 1, 0);
        panel.Controls.Add(layout);
        Controls.Add(panel);
    }

    private void LoadRows()
    {
        AddInLogger.Info("LoadRows: count=" + _texts.Count
            + " 项目语言=[" + string.Join(",", _projectLangs.Select(LangHelper.Code)) + "] 源语言=" + _sourceLang);
        var unknown = ISOCode.Language.L___;

        _baseline.Clear();
        _syncing = true;
        _grid.Rows.Clear();
        try
        {
            for (var i = 0; i < _texts.Count; i++)
            {
                var t = _texts[i];
                var typeName = t.GetType().Name;
                var isTranslated = true;
                var noAutoTrans = false;
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

                // 源语言内容：多语言取源语言翻译，单语言取语言无关串
                var srcVal = isTranslated
                    ? (values.TryGetValue(_sourceLang, out var sval) ? sval : string.Empty)
                    : mirrorVal;

                // 文本列与源语言列同值（原值 / 新值两侧都同步）
                row.Cells[_origTextCol].Value = srcVal;
                row.Cells[_newTextCol].Value = srcVal;
                row.Cells[_origLangCol[_sourceLang]].Value = srcVal;
                row.Cells[_newLangCol[_sourceLang]].Value = srcVal;

                foreach (var lang in _projectLangs)
                {
                    if (lang == _sourceLang) { continue; }
                    var v = isTranslated && values.TryGetValue(lang, out var x) ? x : string.Empty;
                    row.Cells[_origLangCol[lang]].Value = v;
                    row.Cells[_newLangCol[lang]].Value = v;
                }

                SetRowEditable(rowIdx, isTranslated);
                _baseline.Add(SnapshotRow(rowIdx));
            }
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <summary>抓取该行当前可写状态（标志 + 各可编辑语言值）作为已保存基线。源语言内容以“中文列”为准（与文本列同步）。</summary>
    private RowState SnapshotRow(int row)
    {
        var s = new RowState
        {
            Multilang = Convert.ToBoolean(_grid[ColMultilang, row].Value ?? false),
            NoAuto = Convert.ToBoolean(_grid[ColNoAutoTrans, row].Value ?? false),
        };
        foreach (var lang in _projectLangs)
        {
            if (!s.Multilang && lang != _sourceLang) { continue; } // 单语言只跟踪源语言
            s.V[lang] = _grid[_newLangCol[lang], row].Value as string ?? string.Empty;
        }
        return s;
    }

    private bool IsRowDirty(int row, RowState baseLine)
    {
        var cur = SnapshotRow(row);
        if (cur.Multilang != baseLine.Multilang || cur.NoAuto != baseLine.NoAuto) { return true; }
        if (cur.V.Count != baseLine.V.Count) { return true; }
        foreach (var kv in cur.V)
        {
            baseLine.V.TryGetValue(kv.Key, out var oldV);
            if (!string.Equals(kv.Value, oldV ?? string.Empty, StringComparison.Ordinal)) { return true; }
        }
        return false;
    }

    private List<int> DirtyRows()
    {
        var list = new List<int>();
        for (var i = 0; i < _grid.Rows.Count && i < _baseline.Count; i++)
        {
            if (IsRowDirty(i, _baseline[i])) { list.Add(i); }
        }
        return list;
    }

    private void UpdateApplyEnabled()
    {
        if (_applyBtn != null) { _applyBtn.Enabled = DirtyRows().Count > 0; }
    }

    private void EditingTextChanged(object? sender, EventArgs e)
    {
        if (_applyBtn is { Enabled: false }) { _applyBtn.Enabled = true; }
    }

    private void GridOnCellMouseClick(object? s, DataGridViewCellMouseEventArgs e)
    {
        if (e.Button != MouseButtons.Right || e.RowIndex < 0 || e.ColumnIndex < 0) { return; }
        if (!_grid[e.ColumnIndex, e.RowIndex].Selected)
        {
            _grid.CurrentCell = _grid[e.ColumnIndex, e.RowIndex];
        }
    }

    private void GridOnKeyDown(object? sender, KeyEventArgs e)
    {
        if (IsEditing()) { return; }
        if (e.Control && e.KeyCode == Keys.C) { CopySelection(); e.Handled = true; }
        else if (e.Control && e.KeyCode == Keys.X) { CutSelection(); e.Handled = true; }
        else if (e.Control && e.KeyCode == Keys.V) { PasteClipboard(); e.Handled = true; }
        else if (e.KeyCode == Keys.Delete) { ClearSelection(); e.Handled = true; }
    }

    private bool IsEditing() => _grid.IsCurrentCellInEditMode || _grid.EditingControl != null;

    private bool IsNewValueCol(int col) =>
        col == _newTextCol || (col >= NewLangStart && col < NewLangStart + _projectLangs.Count);

    private List<DataGridViewCell> EditableSelectedCells() =>
        _grid.SelectedCells.Cast<DataGridViewCell>()
            .Where(c => c.RowIndex >= 0 && !c.ReadOnly && IsNewValueCol(c.ColumnIndex))
            .ToList();

    private void CopySelection()
    {
        var cells = _grid.SelectedCells.Cast<DataGridViewCell>().Where(c => c.RowIndex >= 0).ToList();
        if (cells.Count == 0) { return; }
        var content = _grid.GetClipboardContent();
        if (content == null) { AddInLogger.Warn("CopySelection: GetClipboardContent 返回 null"); return; }
        Clipboard.SetDataObject(content);
    }

    private void CutSelection()
    {
        var cells = EditableSelectedCells();
        if (cells.Count == 0) { return; }
        CopySelection();
        foreach (var c in cells) { c.Value = string.Empty; }
        UpdateApplyEnabled();
    }

    private void ClearSelection()
    {
        var cells = EditableSelectedCells();
        if (cells.Count == 0) { return; }
        foreach (var c in cells) { c.Value = string.Empty; }
        UpdateApplyEnabled();
    }

    private void PasteClipboard()
    {
        var cells = EditableSelectedCells();
        if (cells.Count == 0)
        {
            MessageBox.Show("请先选择要粘贴的目标单元格（新值列）。", "文本批量编辑",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var text = Clipboard.GetText(TextDataFormat.Text);
        if (string.IsNullOrEmpty(text)) { return; }
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
                if (!IsNewValueCol(colIdx)) { continue; }
                var cell = _grid[colIdx, rowIdx];
                if (cell.ReadOnly) { continue; }
                cell.Value = fields[c];
                pasted++;
            }
        }
        UpdateApplyEnabled();
        AddInLogger.Debug("Paste: 写入 " + pasted + " 格");
    }

    private DataGridViewCell? SelectionAnchorCell()
    {
        var cells = _grid.SelectedCells.Cast<DataGridViewCell>().Where(c => c.RowIndex >= 0).ToList();
        if (cells.Count == 0) { return null; }
        if (cells.Count == 1) { return _grid.CurrentCell; }
        var minRow = cells.Min(c => c.RowIndex);
        var minCol = cells.Where(c => c.RowIndex == minRow).Min(c => c.ColumnIndex);
        return _grid[minCol, minRow];
    }

    /// <summary>
    /// 增量保存：无脏行直接返回成功且不建撤销点；有脏行才开启一个 UndoStep+Transaction，
    /// 只写脏对象、且只动变化的字段（Contents / IsAutomaticallyTranslated）。确定与应用共用本方法。
    /// </summary>
    /// <returns>true=可关窗（无修改或写回并校验一致）；false=异常或回读不一致，保留窗口。</returns>
    private bool SaveDirty(bool quietSuccess = false)
    {
        // 先结束正在进行的单元格编辑，避免最后一格输入未提交
        if (_grid.IsCurrentCellInEditMode) { _grid.EndEdit(); }

        var dirty = DirtyRows();
        if (dirty.Count == 0)
        {
            AddInLogger.Info("SaveDirty: 无未保存修改，跳过写回（不产生撤销点）");
            return true;
        }

        AddInLogger.Info("SaveDirty: 脏行=[" + string.Join(",", dirty.Select(r => r + 1)) + "]");
        var unknown = ISOCode.Language.L___;
        var mismatchRows = new List<int>();

        UndoStep? undo = null;
        Transaction? txn = null;
        var written = new List<int>();
        try
        {
            undo = new UndoManager().CreateUndoStep();
            undo.SetUndoDescription("文本批量编辑");
            txn = new TransactionManager().CreateTransaction();

            foreach (var i in dirty)
            {
                var t = _texts[i];
                if (!t.IsValid)
                {
                    AddInLogger.Warn("SaveDirty: 行 " + (i + 1) + " 对象已失效，跳过");
                    mismatchRows.Add(i + 1);
                    continue;
                }

                var baseLine = _baseline[i];
                var translated = Convert.ToBoolean(_grid[ColMultilang, i].Value ?? false);
                var wantAutoTrans = !Convert.ToBoolean(_grid[ColNoAutoTrans, i].Value ?? false);
                var mirrorVal = _grid[_newLangCol[_sourceLang], i].Value as string ?? string.Empty;

                // 内容是否真的变化（相对已保存基线）
                var contentChanged = false;
                var expected = new Dictionary<ISOCode.Language, string>();
                if (translated)
                {
                    foreach (var lang in _projectLangs)
                    {
                        var v = _grid[_newLangCol[lang], i].Value as string ?? string.Empty;
                        if (v.Length > 0) { expected[lang] = v; }
                        baseLine.V.TryGetValue(lang, out var oldV);
                        if (!string.Equals(v, oldV ?? string.Empty, StringComparison.Ordinal)) { contentChanged = true; }
                    }
                }
                else
                {
                    expected[unknown] = mirrorVal;
                    baseLine.V.TryGetValue(_sourceLang, out var oldSrc);
                    if (!string.Equals(mirrorVal, oldSrc ?? string.Empty, StringComparison.Ordinal)) { contentChanged = true; }
                }
                // 多语言开关切换本身会改变内容形态
                if (translated != baseLine.Multilang) { contentChanged = true; }

                var flagChanged = wantAutoTrans != (!baseLine.NoAuto);

                if (!contentChanged && !flagChanged) { continue; }

                var beforeAuto = t.IsAutomaticallyTranslated;
                if (contentChanged)
                {
                    var mls = new MultiLangString();
                    foreach (var kv in expected) { mls.AddString(kv.Key, kv.Value); }
                    t.Contents = mls; // 必须经 setter 赋回才落库
                }
                if (flagChanged)
                {
                    t.IsAutomaticallyTranslated = wantAutoTrans;
                }
                written.Add(i + 1);

                AddInLogger.Debug("写回 行" + (i + 1)
                    + " 内容" + (contentChanged ? "[改]" : "[不变]")
                    + " 自动翻译 " + beforeAuto + "→" + wantAutoTrans + (flagChanged ? "[改]" : "[不变]")
                    + " : " + string.Join(" ", expected.Select(kv => LangHelper.Code(kv.Key) + "=" + Preview(kv.Value))));
            }

            txn.Commit();
            txn.Dispose();
            txn = null;
            undo.CloseOpenUndo();
            undo.Dispose();
            undo = null;

            // 回读校验：仅校验本次脏行
            foreach (var i in dirty)
            {
                var t = _texts[i];
                if (!t.IsValid) { continue; }
                var translated = Convert.ToBoolean(_grid[ColMultilang, i].Value ?? false);
                var wantAutoTrans = !Convert.ToBoolean(_grid[ColNoAutoTrans, i].Value ?? false);
                var mirrorVal = _grid[_newLangCol[_sourceLang], i].Value as string ?? string.Empty;
                var c = t.Contents;
                bool rowOk = true;

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
                        var want = _grid[_newLangCol[lang], i].Value as string ?? string.Empty;
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
                        AddInLogger.Error("回读不一致 行" + (i + 1) + " 单语言 期望=" + Preview(mirrorVal) + " 实际=" + Preview(actual));
                    }
                }
                if (!rowOk) { mismatchRows.Add(i + 1); }
            }

            if (mismatchRows.Count > 0)
            {
                AddInLogger.Error("SaveDirty: 回读不一致行=[" + string.Join(",", mismatchRows) + "]");
                MessageBox.Show("写回存在回读不一致的行：" + string.Join(",", mismatchRows.Take(30))
                    + "。\n请查看日志，未将这些行标记为已保存。", "文本批量编辑",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                // 仅把校验通过的脏行更新为已保存基线；失败行保留脏状态，可修正后再次应用
                foreach (var i in dirty)
                {
                    if (!mismatchRows.Contains(i + 1)) { _baseline[i] = SnapshotRow(i); }
                }
                UpdateApplyEnabled();
                return false;
            }

            // 全部成功：刷新基线（原值列保留为开窗时的原值，作为本会话对照）
            foreach (var i in dirty) { _baseline[i] = SnapshotRow(i); }
            UpdateApplyEnabled();

            AddInLogger.Info("SaveDirty: 成功 写回行=[" + string.Join(",", written) + "]，合并为 1 个撤销点");
            if (!quietSuccess)
            {
                MessageBox.Show("已写回 " + written.Count + " 个文本对象（一个撤销点，可 Ctrl+Z 撤销）。",
                    "文本批量编辑", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            return true;
        }
        catch (Exception ex)
        {
            AddInLogger.Error("SaveDirty 异常，尝试中止事务", ex);
            try { txn?.Abort(); } catch (Exception abortEx) { AddInLogger.Error("事务 Abort 失败", abortEx); }
            MessageBox.Show("写回失败：" + ex.Message, "文本批量编辑",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
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

    private sealed class RowState
    {
        public bool Multilang;
        public bool NoAuto;
        public Dictionary<ISOCode.Language, string> V = new();
    }

    private class BufferedPanel : Panel
    {
        public BufferedPanel() { DoubleBuffered = true; }
    }
}
