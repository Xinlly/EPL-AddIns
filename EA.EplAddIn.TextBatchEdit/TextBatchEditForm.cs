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
    // 开窗时的初始值，用于标识“已保存但相对原值有改动”的单元格
    private readonly List<RowState> _initial = new();

    // 单元格状态配色
    private static readonly System.Drawing.Color ReadOnlyGray = System.Drawing.Color.FromArgb(225, 225, 225);  // 只读原值（加深）
    private static readonly System.Drawing.Color DirtyYellow = System.Drawing.Color.FromArgb(255, 242, 204);   // 已改未保存
    private static readonly System.Drawing.Color SavedGreen = System.Drawing.Color.FromArgb(221, 244, 223);     // 已改已保存
    private static readonly System.Drawing.Color OrigChangedBlue = System.Drawing.Color.FromArgb(213, 232, 246);// 原值：对应新值已保存改动

    private DataGridView _grid = null!;
    private CheckBox _showOrigChk = null!;
    private CheckBox _showSrcChk = null!;
    private Button _okBtn = null!;
    private Button _cancelBtn = null!;
    private Button _applyBtn = null!;
    private CtrlEnterFilter? _keyFilter;

    // 任意单元格下边缘拖拽行高的状态
    private int _dragRow = -1;
    private int _dragStartY;
    private int _dragStartHeight;
    private const int ResizeEdge = 5; // 距单元格下边缘多少像素视为拖行高热区

    private const int ColIndex = 0;
    private const int ColType = 1;
    private const int ColOrigMultilang = 2;   // 原值·多语言（只读复选框）
    private const int ColOrigNoAuto = 3;      // 原值·不自动翻译（只读复选框）
    private const int OrigTextCol = 4;        // 原值·文本列

    private const int OrigLangStart = 5;      // 原值·语言列起点
    private int NewCheckStart => OrigLangStart + _projectLangs.Count;       // 新值复选框起点
    private int ColMultilang => NewCheckStart;      // 新值·多语言（可编辑）
    private int ColNoAutoTrans => NewCheckStart + 1; // 新值·不自动翻译（可编辑）
    private int NewTextColIdx => NewCheckStart + 2;  // 新值·文本列
    private int NewLangStart => NewTextColIdx + 1;   // 新值·语言列起点

    // 换行表示（对齐 EPLAN 原生表编辑）：界面/表格内一律用 ¶ 单字符单行显示；
    // 写回 EPLAN 时替换为真正换行符，读取时再还原为 ¶。
    private const string LineMarker = "¶";
    private const string LineBreak = "\n";

    /// <summary>EPLAN 真实换行（\r\n / \r / \n）→ 表格显示用 ¶。</summary>
    private static string ToGrid(string s)
    {
        if (string.IsNullOrEmpty(s)) { return string.Empty; }
        return s.Replace("\r\n", LineBreak).Replace('\r', '\n').Replace(LineBreak, LineMarker);
    }

    /// <summary>表格显示用 ¶ → EPLAN 真实换行。</summary>
    private static string ToEplan(string s)
    {
        if (string.IsNullOrEmpty(s)) { return string.Empty; }
        return s.Replace(LineMarker, LineBreak);
    }

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
        // 应用层消息过滤器：在消息派发前吞掉编辑态的 Ctrl+Enter，防止被 DataGridView 当成“结束编辑”
        _keyFilter = new CtrlEnterFilter(this);
        Application.AddMessageFilter(_keyFilter);
        // 窗体真正显示（grid 句柄已建）后：按两个开关初始化列可见性（内含一次自动列宽）
        Shown += (_, _) => UpdateColumnVisibility();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        if (_keyFilter != null) { Application.RemoveMessageFilter(_keyFilter); _keyFilter = null; }
        base.OnFormClosed(e);
    }

    /// <summary>由消息过滤器调用：当前正编辑单元格时，在其中插入换行标记 ¶（不退出编辑态）。</summary>
    private bool TryInsertLineBreakAtEditing()
    {
        if (_grid == null || !_grid.IsCurrentCellInEditMode) { return false; }
        if (_grid.EditingControl is not TextBox tb || tb.IsDisposed) { return false; }
        var caret = tb.SelectionStart;
        var sel = tb.SelectionLength;
        tb.Text = tb.Text.Remove(caret, sel).Insert(caret, LineMarker);
        tb.SelectionStart = caret + LineMarker.Length;
        tb.SelectionLength = 0;
        if (_applyBtn is { Enabled: false }) { _applyBtn.Enabled = true; }
        return true;
    }

    private sealed class CtrlEnterFilter : IMessageFilter
    {
        private const int WM_KEYDOWN = 0x0100;
        private const int VK_RETURN = 0x0D;
        private readonly TextBatchEditForm _form;

        public CtrlEnterFilter(TextBatchEditForm form) { _form = form; }

        public bool PreFilterMessage(ref Message m)
        {
            if (m.Msg != WM_KEYDOWN || (int)m.WParam != VK_RETURN) { return false; }
            if ((Control.ModifierKeys & Keys.Control) == 0) { return false; }
            // 仅在本窗为活动窗、且焦点确实在编辑控件上时拦截
            if (Form.ActiveForm != _form) { return false; }
            var ec = _form._grid?.EditingControl;
            if (ec == null || !ec.IsHandleCreated || ec.Handle != m.HWnd) { return false; }
            return _form.TryInsertLineBreakAtEditing(); // true=吞掉该按键，DataGridView 收不到
        }
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
            RowHeadersVisible = true,   // 显示窄行首列，供拖拽调整行高（兼作行选择）
            RowHeadersWidth = 22,
            AllowUserToResizeRows = true,
            BackgroundColor = System.Drawing.Color.White,
            ClipboardCopyMode = DataGridViewClipboardCopyMode.EnableWithoutHeaderText,
            // 不用自动换行/自动行高：换行以 ¶ 单字符单行显示（与 EPLAN 原生表编辑一致），行高保留手动调整
            ShowCellToolTips = true,
            DefaultCellStyle = new DataGridViewCellStyle { WrapMode = DataGridViewTriState.False },
        };

        _grid.Columns.Add("idx", "#");
        _grid.Columns[ColIndex].Width = 42;
        _grid.Columns[ColIndex].ReadOnly = true;
        _grid.Columns[ColIndex].SortMode = DataGridViewColumnSortMode.NotSortable;

        _grid.Columns.Add("type", "对象类型");
        _grid.Columns[ColType].Width = 100;
        _grid.Columns[ColType].ReadOnly = true;
        _grid.Columns[ColType].SortMode = DataGridViewColumnSortMode.NotSortable;

        // —— 原值侧（只读，默认随“显示原值”整体隐藏）——
        _grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "orig_multilang", HeaderText = "原多语言", Width = 70, ReadOnly = true, Visible = false });
        _grid.Columns[ColOrigMultilang].SortMode = DataGridViewColumnSortMode.NotSortable;
        _grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "orig_noAutoTrans", HeaderText = "原不自动翻译", Width = 92, ReadOnly = true, Visible = false });
        _grid.Columns[ColOrigNoAuto].SortMode = DataGridViewColumnSortMode.NotSortable;

        var dnSource = LangHelper.DisplayName(_sourceLang);

        // —— 原值列（只读、默认隐藏，可用“显示原值”展开）——
        _grid.Columns.Add("orig_text", "原文本（" + dnSource + "）");
        _grid.Columns[_origTextCol].Width = 190;
        _grid.Columns[_origTextCol].ReadOnly = true;
        _grid.Columns[_origTextCol].Visible = false; // 默认隐藏，与其余原值列一致，由“显示原值”统一展开
        _grid.Columns[_origTextCol].SortMode = DataGridViewColumnSortMode.NotSortable;

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
            _origLangCol[lang] = idx;
        }

        // —— 新值侧复选框（可编辑），位于原值列右边、新值文本列左边 ——
        _grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "multilang", HeaderText = "多语言", Width = 60 });
        _grid.Columns[ColMultilang].SortMode = DataGridViewColumnSortMode.NotSortable;
        _grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "noAutoTrans", HeaderText = "不自动翻译", Width = 84 });
        _grid.Columns[ColNoAutoTrans].SortMode = DataGridViewColumnSortMode.NotSortable;

        // —— 新值列（可编辑）——
        _grid.Columns.Add("new_text", "源语言（" + dnSource + "）");
        _grid.Columns[_newTextCol].Width = 200;
        _grid.Columns[_newTextCol].SortMode = DataGridViewColumnSortMode.NotSortable;
        ((DataGridViewTextBoxColumn)_grid.Columns[_newTextCol]).CellTemplate = new LineBreakTextBoxCell();

        for (var i = 0; i < _projectLangs.Count; i++)
        {
            var lang = _projectLangs[i];
            var idx = NewLangStart + i;
            var title = LangHelper.DisplayName(lang); // 中文 / 英文…
            _grid.Columns.Add("new_" + LangHelper.Code(lang), title);
            _grid.Columns[idx].Width = 200;
            _grid.Columns[idx].SortMode = DataGridViewColumnSortMode.NotSortable;
            ((DataGridViewTextBoxColumn)_grid.Columns[idx]).CellTemplate = new LineBreakTextBoxCell();
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
        menu.Items.Add("换行(&L)", null, (_, _) => InsertLineBreakIntoCurrent()); // 快捷键不可用时的兜底入口
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("调整列宽(&A)", null, (_, _) => AutoFitColumns());
        _grid.ContextMenuStrip = menu;

        _grid.KeyDown += GridOnKeyDown;
        _grid.CellMouseClick += GridOnCellMouseClick;
        _grid.CellFormatting += GridOnCellFormatting; // 统一单元格状态着色
        // 任意单元格下边缘拖拽调行高
        _grid.MouseDown += GridOnMouseDownForRowResize;
        _grid.MouseMove += GridOnMouseMoveForRowResize;
        _grid.MouseUp += GridOnMouseUpForRowResize;
        _grid.MouseLeave += (_, _) => { if (_dragRow < 0) { _grid.Cursor = Cursors.Default; } };
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
        var barFlow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(8, 5, 0, 0),
        };

        _showOrigChk = new CheckBox
        {
            Text = "显示原值",
            Width = 100,
            Checked = false,
            TextAlign = ContentAlignment.MiddleLeft,
        };
        _showOrigChk.CheckedChanged += (_, _) => UpdateColumnVisibility();

        _showSrcChk = new CheckBox
        {
            Text = "显示源语言",
            Width = 110,
            Checked = true, // 默认显示源语言两列（含可编辑的新值源语言列）
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(12, 0, 0, 0),
        };
        _showSrcChk.CheckedChanged += (_, _) => UpdateColumnVisibility();

        barFlow.Controls.Add(_showOrigChk);
        barFlow.Controls.Add(_showSrcChk);
        bar.Controls.Add(barFlow);

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
                "【怎么用】\n" +
                "  • 直接双击格子修改文字；改完点“应用”保存，或点“确定”保存并关闭。\n" +
                "  • 想放弃本次修改点“取消”；“应用”后窗口不关闭，可继续修改。\n\n" +
                "【两个勾选框】\n" +
                "  • 多语言：勾选后可填写中文、英文等各语言内容；不勾选则只填一份内容。\n" +
                "  • 不自动翻译：勾选后该文本不参与自动翻译。\n\n" +
                "【上方两个开关】\n" +
                "  • 显示原值：在左侧展开灰色的“原…”列，方便对照修改前的内容。\n" +
                "  • 显示源语言：显示/隐藏源语言那一列（原值侧与新值侧各一列）。\n\n" +
                "【换行与排版】\n" +
                "  • 单元格里按 Ctrl+Enter 换行（显示为 ¶，保存后即为真正换行）；也可右键选“换行”。\n" +
                "  • 拖动格子下边缘可改该行高度，拖动表头分隔线可改列宽；右键“调整列宽”自动适配。\n" +
                "  • 可像 Excel 一样框选后复制、粘贴、删除。\n\n" +
                "【格子颜色】\n" +
                "  • 灰色：只读，不能修改。\n" +
                "  • 黄色：已修改、还没保存。\n" +
                "  • 绿色：已修改并保存。\n" +
                "  • 蓝色（原值列）：对应的新内容已保存且与原值不同。",
        };
        tabHelp.Controls.Add(help);

        tabs.TabPages.Add(tabEdit);
        tabs.TabPages.Add(tabHelp);
        Controls.Add(tabs);
    }

    /// <summary>
    /// 列可见性 = 两个开关的组合：
    ///   原多语言/原不自动翻译/各原语言列(原中文/原英文…)：仅受“显示原值”控制；
    ///   原文本（源语言文本列）：受两者共同控制 = 显示原值 AND 显示源语言；
    ///   源语言（新值侧源语言文本列）：仅受“显示源语言”控制；
    ///   其余新值列（多语言/不自动翻译/中文/英文…）：常显。
    /// </summary>
    private void UpdateColumnVisibility()
    {
        var showOrig = _showOrigChk.Checked;
        var showSrc = _showSrcChk.Checked;

        // 若正编辑的列即将被隐藏，先提交编辑，避免 DataGridView 因 CurrentCell 落到隐藏列报错
        if (_grid.IsCurrentCellInEditMode) { _grid.EndEdit(); }

        // 原值侧复选框、各原语言列：只看“显示原值”
        _grid.Columns[ColOrigMultilang].Visible = showOrig;
        _grid.Columns[ColOrigNoAuto].Visible = showOrig;
        foreach (var idx in _origLangCol.Values) { _grid.Columns[idx].Visible = showOrig; }

        // 原值·源语言文本列：两个开关同时为真才显示
        _grid.Columns[_origTextCol].Visible = showOrig && showSrc;

        // 新值·源语言文本列：只看“显示源语言”
        _grid.Columns[_newTextCol].Visible = showSrc;

        AutoFitColumns();
    }

    /// <summary>
    /// 按当前单元格内容自动调整各可见列宽（含表头），并限制在 60~420px，避免多行长文本把列撑爆。
    /// </summary>
    private void AutoFitColumns()
    {
        const int minW = 60, maxW = 420;
        foreach (DataGridViewColumn col in _grid.Columns)
        {
            if (!col.Visible) { continue; }
            _grid.AutoResizeColumn(col.Index, DataGridViewAutoSizeColumnMode.AllCells);
            var w = col.Width;
            if (w < minW) { w = minW; }
            if (w > maxW) { w = maxW; }
            col.Width = w;
        }
        AddInLogger.Debug("AutoFitColumns 完成");
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
        }
        // 文本列（单语言内容）始终可编辑
        _grid[_newTextCol, row].ReadOnly = false;
    }

    // —— 右键菜单“换行”：快捷键不可用时的兜底入口 ——
    private void InsertLineBreakIntoCurrent()
    {
        var cell = _grid.CurrentCell;
        if (cell == null || cell.RowIndex < 0 || !IsNewValueCol(cell.ColumnIndex) || cell.ReadOnly)
        {
            MessageBox.Show("请先选中一个可编辑的文本单元格（新值侧）。", "换行",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (!_grid.IsCurrentCellInEditMode)
        {
            _grid.BeginEdit(false);
            if (_grid.EditingControl is TextBox tb0) { tb0.SelectionStart = tb0.TextLength; tb0.SelectionLength = 0; }
        }
        TryInsertLineBreakAtEditing();
    }

    private bool IsEditableStateCol(int col) =>
        col == ColMultilang || col == ColNoAutoTrans || IsNewValueCol(col);

    // —— 单元格状态着色（只读灰 / 已改未保存黄 / 已改已保存绿 / 原值随已保存改动变蓝）——
    private void GridOnCellFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        var row = e.RowIndex;
        var col = e.ColumnIndex;
        if (row < 0 || row >= _baseline.Count) { return; }

        // 原值侧
        if (IsOrigCol(col))
        {
            var newCol = CorrespondingNewCol(col);
            e.CellStyle!.BackColor =
                (!CellIsDirty(row, newCol) && CellSavedChanged(row, newCol)) ? OrigChangedBlue : ReadOnlyGray;
            return;
        }

        // 新值侧复选框 / 文本
        if (IsEditableStateCol(col))
        {
            if (CellIsDirty(row, col)) { e.CellStyle!.BackColor = DirtyYellow; }
            else if (CellSavedChanged(row, col)) { e.CellStyle!.BackColor = SavedGreen; }
            else if (_grid[col, row].ReadOnly) { e.CellStyle!.BackColor = ReadOnlyGray; }
            // else 保持默认白底
        }
    }

    private bool IsOrigCol(int col) =>
        col == ColOrigMultilang || col == ColOrigNoAuto ||
        col == _origTextCol || _origLangCol.ContainsValue(col);

    private int CorrespondingNewCol(int origCol)
    {
        if (origCol == ColOrigMultilang) { return ColMultilang; }
        if (origCol == ColOrigNoAuto) { return ColNoAutoTrans; }
        if (origCol == _origTextCol) { return _newTextCol; }
        foreach (var kv in _origLangCol)
        {
            if (kv.Value == origCol) { return _newLangCol[kv.Key]; }
        }
        return -1;
    }

    /// <summary>把某一“新值状态列”归约为可比较的字符串（标志位或某语言文本）。</summary>
    private string StateValue(RowState s, int col)
    {
        if (col == ColMultilang) { return s.Multilang ? "1" : "0"; }
        if (col == ColNoAutoTrans) { return s.NoAuto ? "1" : "0"; }
        var lang = col == _newTextCol ? _sourceLang : _newLangCol.First(kv => kv.Value == col).Key;
        return s.V.TryGetValue(lang, out var v) ? v : string.Empty;
    }

    private string CurrentCellValue(int row, int col)
    {
        if (col == ColMultilang || col == ColNoAutoTrans)
        {
            return Convert.ToBoolean(_grid[col, row].Value ?? false) ? "1" : "0";
        }
        return _grid[col, row].Value as string ?? string.Empty;
    }

    private bool CellIsDirty(int row, int col) =>
        CurrentCellValue(row, col) != StateValue(_baseline[row], col);

    private bool CellSavedChanged(int row, int col) =>
        CurrentCellValue(row, col) != StateValue(_initial[row], col);

    private static RowState CloneRow(RowState s) => new()
    {
        Multilang = s.Multilang,
        NoAuto = s.NoAuto,
        V = new Dictionary<ISOCode.Language, string>(s.V),
    };

    // —— 任意列下边缘拖拽调整行高 ——
    private void GridOnMouseDownForRowResize(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || _grid.IsCurrentCellInEditMode) { return; }
        var hit = _grid.HitTest(e.X, e.Y);
        if (hit.Type != DataGridViewHitTestType.Cell || hit.RowIndex < 0) { return; }
        var rect = _grid.GetCellDisplayRectangle(hit.ColumnIndex, hit.RowIndex, false);
        if (Math.Abs(e.Y - rect.Bottom) <= ResizeEdge)
        {
            _dragRow = hit.RowIndex;
            _dragStartY = e.Y;
            _dragStartHeight = _grid.Rows[hit.RowIndex].Height;
            _grid.Cursor = Cursors.SizeNS;
        }
    }

    private void GridOnMouseMoveForRowResize(object? sender, MouseEventArgs e)
    {
        if (_dragRow >= 0)
        {
            var h = Math.Max(18, _dragStartHeight + (e.Y - _dragStartY));
            _grid.Rows[_dragRow].Height = h;
            return;
        }
        if (_grid.IsCurrentCellInEditMode) { return; }
        var hit = _grid.HitTest(e.X, e.Y);
        if (hit.Type == DataGridViewHitTestType.Cell && hit.RowIndex >= 0)
        {
            var rect = _grid.GetCellDisplayRectangle(hit.ColumnIndex, hit.RowIndex, false);
            _grid.Cursor = Math.Abs(e.Y - rect.Bottom) <= ResizeEdge ? Cursors.SizeNS : Cursors.Default;
        }
        else if (hit.Type != DataGridViewHitTestType.ColumnHeader)
        {
            _grid.Cursor = Cursors.Default; // 列标题边缘交给 DataGridView 自己的列宽光标
        }
    }

    private void GridOnMouseUpForRowResize(object? sender, MouseEventArgs e)
    {
        if (_dragRow >= 0) { _dragRow = -1; _grid.Cursor = Cursors.Default; }
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
                // 原值侧复选框：打开窗口时的状态（只读，仅对照）
                row.Cells[ColOrigMultilang].Value = isTranslated;
                row.Cells[ColOrigNoAuto].Value = noAutoTrans;
                // 新值侧复选框：初始同原值，可编辑
                row.Cells[ColMultilang].Value = isTranslated;
                row.Cells[ColNoAutoTrans].Value = noAutoTrans;

                // 源语言内容：多语言取源语言翻译，单语言取语言无关串
                var srcVal = isTranslated
                    ? (values.TryGetValue(_sourceLang, out var sval) ? sval : string.Empty)
                    : mirrorVal;
                srcVal = ToGrid(srcVal); // 真实换行 → ¶，单元格单行显示

                // 文本列与源语言列同值（原值 / 新值两侧都同步）
                row.Cells[_origTextCol].Value = srcVal;
                row.Cells[_newTextCol].Value = srcVal;
                row.Cells[_origLangCol[_sourceLang]].Value = srcVal;
                row.Cells[_newLangCol[_sourceLang]].Value = srcVal;

                foreach (var lang in _projectLangs)
                {
                    if (lang == _sourceLang) { continue; }
                    var v = isTranslated && values.TryGetValue(lang, out var x) ? x : string.Empty;
                    var gv = ToGrid(v);
                    row.Cells[_origLangCol[lang]].Value = gv;
                    row.Cells[_newLangCol[lang]].Value = gv;
                }

                SetRowEditable(rowIdx, isTranslated);
                var snap = SnapshotRow(rowIdx);
                _baseline.Add(snap);
                _initial.Add(CloneRow(snap));
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
        if (_grid != null) { _grid.Invalidate(); } // 触发 CellFormatting 重算脏/已保存底色
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
        // 按选区矩形自建 TSV：含换行(¶)的单元格转成真实换行并用双引号包裹，符合 Excel 复制格式
        var minRow = cells.Min(c => c.RowIndex);
        var maxRow = cells.Max(c => c.RowIndex);
        var minCol = cells.Min(c => c.ColumnIndex);
        var maxCol = cells.Max(c => c.ColumnIndex);
        var sb = new System.Text.StringBuilder();
        for (var r = minRow; r <= maxRow; r++)
        {
            for (var c = minCol; c <= maxCol; c++)
            {
                if (c > minCol) { sb.Append('\t'); }
                var raw = ToEplan(_grid[c, r].FormattedValue?.ToString() ?? string.Empty); // ¶ → 真实换行
                sb.Append(QuoteTsv(raw));
            }
            if (r < maxRow) { sb.Append("\r\n"); }
        }
        Clipboard.SetText(sb.ToString());
    }

    /// <summary>按 Excel 规则引用字段：含 Tab/换行/引号时用双引号包裹，内部引号双写。</summary>
    private static string QuoteTsv(string s)
    {
        if (s.IndexOfAny(new[] { '\t', '\r', '\n', '"' }) < 0) { return s; }
        return "\"" + s.Replace("\"", "\"\"") + "\"";
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

        // 按 Excel/TSV 语义解析：引号内的 Tab/换行属于“单元格内”内容，不拆列/拆行。
        var rows = ParseTsv(text!);
        var pasted = 0;
        for (var r = 0; r < rows.Count; r++)
        {
            var rowIdx = anchor.RowIndex + r;
            if (rowIdx >= _grid.Rows.Count) { break; }
            var fields = rows[r];
            for (var c = 0; c < fields.Count; c++)
            {
                var colIdx = anchor.ColumnIndex + c;
                if (!IsNewValueCol(colIdx)) { continue; }
                var cell = _grid[colIdx, rowIdx];
                if (cell.ReadOnly) { continue; }
                cell.Value = ToGrid(fields[c]); // 格内真实换行 → ¶，不产生新表行
                pasted++;
            }
        }
        UpdateApplyEnabled();
        AddInLogger.Debug("Paste: 写入 " + pasted + " 格（" + rows.Count + " 行）");
    }

    /// <summary>
    /// 解析 Excel 风格 TSV：Tab 分列、CRLF/LF 分行；被双引号包裹的单元格内，Tab/换行均为格内内容，
    /// “”“”转义为一个引号。无引号文本按普通 Tab/换行拆分（兼容记事本等来源）。
    /// </summary>
    private static List<List<string>> ParseTsv(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new System.Text.StringBuilder();
        var inQuotes = false;
        var anyQuote = false;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                    else { inQuotes = false; }
                }
                else { field.Append(ch); }
                continue;
            }
            if (ch == '"' && field.Length == 0) { inQuotes = true; anyQuote = true; }
            else if (ch == '\t') { row.Add(field.ToString()); field.Clear(); anyQuote = false; }
            else if (ch == '\n')
            {
                row.Add(field.ToString()); field.Clear(); anyQuote = false;
                rows.Add(row); row = new List<string>();
                if (i + 1 < text.Length && text[i + 1] == '\n') { /* 罕见：连续换行视为空行 */ }
            }
            else if (ch == '\r')
            {
                // 可能是 CRLF 或单独 CR
                row.Add(field.ToString()); field.Clear(); anyQuote = false;
                rows.Add(row); row = new List<string>();
                if (i + 1 < text.Length && text[i + 1] == '\n') { i++; }
            }
            else { field.Append(ch); }
        }
        // 末尾最后一格/最后一行
        if (field.Length > 0 || row.Count > 0 || anyQuote)
        {
            row.Add(field.ToString());
            rows.Add(row);
        }
        // 去掉因尾部换行产生的空行
        while (rows.Count > 0 && rows[rows.Count - 1].Count == 1 && rows[rows.Count - 1][0].Length == 0)
        {
            rows.RemoveAt(rows.Count - 1);
        }
        return rows;
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
                    foreach (var kv in expected) { mls.AddString(kv.Key, ToEplan(kv.Value)); } // ¶ → 真实换行
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
                        var actual = ToGrid(ReadSafe(c, lang)); // 真实换行 → ¶ 再比对
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
                    var actual = ToGrid(PickClean(
                        Safe(() => c.GetStringToDisplay(_sourceLang)),
                        Safe(() => c.GetString(unknown)),
                        c.InternalString ?? string.Empty));
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

    /// <summary>
    /// 文本编辑控件：声明自己要消费 Ctrl+Enter，避免按键被 DataGridView 提前解释为“结束编辑”；
    /// 在 OnKeyDown 中插入换行标记 ¶（写回时再替换为真实换行）。普通 Enter 仍交给表格提交/下移。
    /// </summary>
    internal class LineBreakTextBox : DataGridViewTextBoxEditingControl
    {
        private const string Marker = "¶";

        public override bool EditingControlWantsInputKey(Keys keyData, bool dataGridViewWantsInputKey)
        {
            if ((keyData & Keys.KeyCode) == Keys.Enter && (keyData & Keys.Control) != 0)
            {
                return true; // 自己消费 Ctrl+Enter
            }
            return base.EditingControlWantsInputKey(keyData, dataGridViewWantsInputKey);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.Control && e.KeyCode == Keys.Enter)
            {
                var caret = SelectionStart;
                var sel = SelectionLength;
                Text = Text.Remove(caret, sel).Insert(caret, Marker);
                SelectionStart = caret + Marker.Length;
                SelectionLength = 0;
                e.Handled = true;
                e.SuppressKeyPress = true;
                return;
            }
            base.OnKeyDown(e);
        }
    }

    internal class LineBreakTextBoxCell : DataGridViewTextBoxCell
    {
        public override Type EditType => typeof(LineBreakTextBox);
    }
}
