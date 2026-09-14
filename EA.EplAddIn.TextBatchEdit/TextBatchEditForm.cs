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
    private List<TextBase> _texts;
    private ISOCode.Language _sourceLang;
    private List<ISOCode.Language> _projectLangs; // 固定顺序，首位为源语言
    private Project? _project;

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
    private static readonly System.Drawing.Color InfoHeaderGray = System.Drawing.Color.FromArgb(239, 239, 239);// 结构/坐标只读列（与列头同色）
    private static readonly System.Drawing.Color DirtyYellow = System.Drawing.Color.FromArgb(255, 242, 204);   // 已改未保存
    private static readonly System.Drawing.Color SavedGreen = System.Drawing.Color.FromArgb(221, 244, 223);     // 已改已保存
    private static readonly System.Drawing.Color FocusTint = System.Drawing.Color.FromArgb(232, 242, 251);     // 单元格聚焦：所在行/列的极浅蓝
    private static readonly System.Drawing.Color FocusHeaderTint = System.Drawing.Color.FromArgb(217, 235, 248); // 单元格聚焦：列头/行首用略深浅蓝（在灰底上仍可辨）

    private DataGridView _grid = null!;
    private CheckBox _showOrigChk = null!;
    private CheckBox _showSrcChk = null!;
    private CheckBox _autoSaveChk = null!;
    private CheckBox _focusCellChk = null!;
    private Label _statusLabel = null!;
    private Button _okBtn = null!;
    private Button _cancelBtn = null!;
    private Button _applyBtn = null!;
    private CtrlEnterFilter? _keyFilter;
    private Timer? _autoSaveTimer;
    private bool _saving; // 抑制保存/刷新过程中自动保存的重入

    // 任意单元格下边缘拖拽行高的状态
    private int _dragRow = -1;
    private int _dragStartY;
    private int _dragStartHeight;
    private const int ResizeEdge = 5; // 距单元格下边缘多少像素视为拖行高热区

    // 双击列标题三级排序：默认(结构→X↑→Y↓) → 升 → 降 → 默认
    private int _sortCol = -1;
    private int _sortDir; // 0=默认, 1=升, -1=降
    private readonly List<int> _origIndex = new(); // 每个显示行对应的开窗原始下标（随排序一起重排）
    private readonly List<RowMeta> _meta = new();  // 每个显示行的只读结构/坐标信息（随排序一起重排）

    // 三段结构标识符在“结构标识符管理”中的顺序表：归一化主标识符 -> 顺序号（越小越靠前）
    private readonly Dictionary<string, double> _plantRank = new();
    private readonly Dictionary<string, double> _placeRank = new();
    private readonly Dictionary<string, double> _locationRank = new();

    /// <summary>一行只读结构/坐标信息，同时承担“默认排序”的键。</summary>
    private sealed class RowMeta
    {
        public string Plant = string.Empty;   // 高层代号显示值
        public string Place = string.Empty;   // 安装地点显示值
        public string Location = string.Empty;// 位置代号显示值
        public double X;
        public double Y;
        // 三段结构标识符在“结构标识符管理”中的顺序序号（越小越靠前；未匹配取 double.MaxValue）
        public double PlantRank = double.MaxValue;
        public double PlaceRank = double.MaxValue;
        public double LocationRank = double.MaxValue;
    }

    private const int ColIndex = 0;
    private const int ColType = 1;
    // —— 只读结构/坐标信息列（位于“类型”右侧）——
    private const int ColPlant = 2;        // 高层代号 =
    private const int ColPlace = 3;        // 安装地点 ++
    private const int ColLocation = 4;     // 位置代号 +
    private const int ColX = 5;            // X 坐标
    private const int ColY = 6;            // Y 坐标

    private const int ColOrigMultilang = 7;   // 原值·多语言（只读复选框）
    private const int ColOrigNoAuto = 8;      // 原值·不自动翻译（只读复选框）
    private const int OrigTextCol = 9;        // 原值·文本列

    private const int OrigLangStart = 10;     // 原值·语言列起点
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
        ISOCode.Language sourceLang, List<ISOCode.Language> projectLangs, Project? project = null)
    {
        _texts = texts;
        _sourceLang = sourceLang;
        _projectLangs = projectLangs;
        _project = project;
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
        // 开窗即按默认规则排序：结构标识符管理顺序 → X 升 → Y 降
        ApplyDefaultOrder();
        UpdateApplyEnabled();
        // 应用层消息过滤器：在消息派发前吞掉编辑态的 Ctrl+Enter，防止被 DataGridView 当成“结束编辑”
        _keyFilter = new CtrlEnterFilter(this);
        Application.AddMessageFilter(_keyFilter);
        // 窗体真正显示（grid 句柄已建）后：按两个开关初始化列可见性（内含一次自动列宽）
        Shown += (_, _) => UpdateColumnVisibility();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        DisposeAutoSaveTimer();
        if (_keyFilter != null) { Application.RemoveMessageFilter(_keyFilter); _keyFilter = null; }
        base.OnFormClosed(e);
    }

    /// <summary>
    /// 像 EPLAN 导航器那样：打开时不抢占键盘焦点，用户可继续操作图形编辑器。
    /// 真正的“嵌入停靠”EPLAN 2.9 公开 API 不支持，这里以非模态、属主为主窗的常驻浮动窗实现。
    /// </summary>
    protected override bool ShowWithoutActivation => true;

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
            RowHeadersVisible = true,   // 最左行首列：显示行号，兼作行选择/拖拽行高
            // 强制按最宽行号内容自适应列宽；该模式下行首宽度恒由内容决定，用户拖拽会被自动值覆盖（即禁止手动调宽）
            RowHeadersWidthSizeMode = DataGridViewRowHeadersWidthSizeMode.AutoSizeToAllHeaders,
            AllowUserToResizeRows = true,
            BackgroundColor = System.Drawing.Color.White,
            ClipboardCopyMode = DataGridViewClipboardCopyMode.EnableWithoutHeaderText,
            // 不用自动换行/自动行高：换行以 ¶ 单字符单行显示（与 EPLAN 原生表编辑一致），行高保留手动调整
            ShowCellToolTips = true,
            DefaultCellStyle = new DataGridViewCellStyle { WrapMode = DataGridViewTriState.False },
            EnableHeadersVisualStyles = false, // 允许自定义表头底色
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize, // 适配双行标题
            GridColor = System.Drawing.Color.FromArgb(170, 170, 170), // 加深网格线，表头/数据行分界更清晰
            // 行首列（行号列）底色与列标题一致、无列标题；数字居中，选中箭头仍显示在最右
            RowHeadersDefaultCellStyle = new DataGridViewCellStyle
            {
                Alignment = DataGridViewContentAlignment.MiddleCenter,
                BackColor = System.Drawing.Color.FromArgb(239, 239, 239),
                ForeColor = System.Drawing.Color.FromArgb(80, 80, 80),
                SelectionBackColor = System.Drawing.Color.FromArgb(239, 239, 239),
                SelectionForeColor = System.Drawing.Color.FromArgb(80, 80, 80),
            },
            ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
            {
                Alignment = DataGridViewContentAlignment.BottomCenter, // 标题下部居中
                WrapMode = DataGridViewTriState.True,                  // 支持 \n 双行标题
                BackColor = System.Drawing.Color.FromArgb(239, 239, 239), // 浅灰，比只读列(225)更浅
                ForeColor = System.Drawing.Color.Black,
                SelectionBackColor = System.Drawing.Color.FromArgb(239, 239, 239),
            },
        };

        BuildColumns();

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
            ScheduleAutoSave();
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
        _grid.ColumnHeaderMouseDoubleClick += GridOnHeaderDoubleClick; // 双击表头：顺序/倒序/默认
        _grid.CellMouseDown += GridOnCellMouseDown;
        _grid.CellFormatting += GridOnCellFormatting; // 统一单元格状态着色
        _grid.CurrentCellChanged += (_, _) => { if (_focusCellChk is { Checked: true }) { _grid.Invalidate(); } };
        _grid.CellPainting += GridOnCellPainting;     // 自绘列头排序箭头
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

    /// <summary>创建全部列。首次构建调用；同一窗口/同一项目内刷新选择集时列结构不变，仅重建行。</summary>
    private void BuildColumns()
    {
        _grid.Columns.Clear();
        _origLangCol.Clear();
        _newLangCol.Clear();

        _grid.Columns.Add("idx", "序号");
        _grid.Columns[ColIndex].Width = 50;
        _grid.Columns[ColIndex].ReadOnly = true;
        _grid.Columns[ColIndex].SortMode = DataGridViewColumnSortMode.NotSortable;

        _grid.Columns.Add("type", "对象类型");
        _grid.Columns[ColType].Width = 100;
        _grid.Columns[ColType].ReadOnly = true;
        _grid.Columns[ColType].SortMode = DataGridViewColumnSortMode.NotSortable;

        // —— 只读结构/坐标信息列（类型右侧）——
        AddInfoColumn("plant", "高层代号\n=", ColPlant, 90);
        AddInfoColumn("place", "安装地点\n++", ColPlace, 90);
        AddInfoColumn("location", "位置代号\n+", ColLocation, 90);
        AddInfoColumn("x", "x", ColX, 60);
        AddInfoColumn("y", "y", ColY, 60);

        // —— 原值侧（只读，默认随“显示原值”整体隐藏）——
        _grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "orig_multilang", HeaderText = "原值\n多语言", Width = 70, ReadOnly = true, Visible = false });
        _grid.Columns[ColOrigMultilang].SortMode = DataGridViewColumnSortMode.NotSortable;
        _grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "orig_noAutoTrans", HeaderText = "原值\n不自动翻译", Width = 92, ReadOnly = true, Visible = false });
        _grid.Columns[ColOrigNoAuto].SortMode = DataGridViewColumnSortMode.NotSortable;

        var dnSource = LangHelper.DisplayName(_sourceLang);

        // —— 原值列（只读、默认隐藏，可用“显示原值”展开）——
        _grid.Columns.Add("orig_text", "原值 · 源语言\n" + dnSource);
        _grid.Columns[_origTextCol].Width = 190;
        _grid.Columns[_origTextCol].ReadOnly = true;
        _grid.Columns[_origTextCol].Visible = false;
        _grid.Columns[_origTextCol].SortMode = DataGridViewColumnSortMode.NotSortable;

        for (var i = 0; i < _projectLangs.Count; i++)
        {
            var lang = _projectLangs[i];
            var idx = OrigLangStart + i;
            var title = "原值\n" + LangHelper.DisplayName(lang);
            _grid.Columns.Add("orig_" + LangHelper.Code(lang), title);
            _grid.Columns[idx].Width = 180;
            _grid.Columns[idx].ReadOnly = true;
            _grid.Columns[idx].Visible = false;
            _grid.Columns[idx].SortMode = DataGridViewColumnSortMode.NotSortable;
            _origLangCol[lang] = idx;
        }

        // —— 新值侧复选框（可编辑）——
        _grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "multilang", HeaderText = "多语言", Width = 60 });
        _grid.Columns[ColMultilang].SortMode = DataGridViewColumnSortMode.NotSortable;
        _grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "noAutoTrans", HeaderText = "不自动翻译", Width = 84 });
        _grid.Columns[ColNoAutoTrans].SortMode = DataGridViewColumnSortMode.NotSortable;

        // —— 新值列（可编辑）——
        _grid.Columns.Add("new_text", "源语言\n" + dnSource);
        _grid.Columns[_newTextCol].Width = 200;
        _grid.Columns[_newTextCol].SortMode = DataGridViewColumnSortMode.NotSortable;
        ((DataGridViewTextBoxColumn)_grid.Columns[_newTextCol]).CellTemplate = new LineBreakTextBoxCell();

        for (var i = 0; i < _projectLangs.Count; i++)
        {
            var lang = _projectLangs[i];
            var idx = NewLangStart + i;
            var title = lang == _sourceLang ? "源语言\n" + dnSource : LangHelper.DisplayName(lang);
            _grid.Columns.Add("new_" + LangHelper.Code(lang), title);
            _grid.Columns[idx].Width = 200;
            _grid.Columns[idx].SortMode = DataGridViewColumnSortMode.NotSortable;
            ((DataGridViewTextBoxColumn)_grid.Columns[idx]).CellTemplate = new LineBreakTextBoxCell();
            _newLangCol[lang] = idx;
        }
    }

    /// <summary>统一生成顶部开关：同高、同垂直边距、同基线，便于横向对齐与后续扩展。</summary>
    private static CheckBox MakeToggle(string text, int width, bool isChecked)
    {
        return new CheckBox
        {
            Text = text,
            Width = width,
            Height = 28,
            Checked = isChecked,
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleLeft,
            CheckAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0, 3, 0, 0),
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

        var bar = new Panel { Dock = DockStyle.Top, Height = 34 };
        var barFlow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(10, 0, 0, 0),
        };

        // 所有开关统一经 MakeToggle 创建：同高、同垂直边距、同基线，保证 y 对齐；新增开关只需一行。
        _showOrigChk = MakeToggle("显示原值", 96, false);
        _showOrigChk.CheckedChanged += (_, _) => UpdateColumnVisibility();

        _showSrcChk = MakeToggle("显示源语言", 108, false); // 默认不显示源语言列
        _showSrcChk.CheckedChanged += (_, _) => UpdateColumnVisibility();

        _autoSaveChk = MakeToggle("自动保存", 96, true);     // 默认开启：编辑结束即写回
        _autoSaveChk.CheckedChanged += (_, _) => OnAutoSaveToggled();

        _focusCellChk = MakeToggle("单元格聚焦", 108, true); // 默认开启：当前格所在行/列浅底
        _focusCellChk.CheckedChanged += (_, _) => _grid.Invalidate();

        barFlow.Controls.Add(_showOrigChk);
        barFlow.Controls.Add(_showSrcChk);
        barFlow.Controls.Add(_autoSaveChk);
        barFlow.Controls.Add(_focusCellChk);
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
                "  • 直接双击格子修改文字；默认“自动保存”开启，停顿约半秒即自动写回（一个撤销点）。\n" +
                "  • 关闭“自动保存”后，用“应用”保存、“确定”保存并关闭、“取消”放弃。\n\n" +
                "【两个勾选框】\n" +
                "  • 多语言：勾选后可填写中文、英文等各语言内容；不勾选则只填一份内容。\n" +
                "  • 不自动翻译：勾选后该文本不参与自动翻译。\n\n" +
                "【上方开关】\n" +
                "  • 显示原值：在左侧展开灰色的“原…”列，方便对照修改前的内容。\n" +
                "  • 显示源语言：显示/隐藏源语言那一列（原值侧与新值侧各一列）；默认不显示。\n" +
                "  • 自动保存：开启后编辑停顿即自动写回；关闭后需手动“应用”。默认开启。\n" +
                "  • 单元格聚焦：开启后当前格所在的整行、整列（不含当前格本身）置极浅底色，方便对齐查看。默认开启。\n\n" +
                "【换选中文本 / 多页】\n" +
                "  • 窗口常开时重新框选文本、或在页导航器改选页，再执行命令即可刷新表格；\n" +
                "  • 除图面框选外，也可在页导航器选中一个或多个页（或高层代号等节点）批量编辑这些页的全部文本；\n" +
                "  • 若有未保存修改会先弹窗（保存并刷新/放弃刷新/取消）。\n\n" +
                "【结构/坐标只读列】\n" +
                "  • 最左行首列为行号（随当前显示位置变），其后“序号”列是默认排序下的固定编号；类型右侧为只读信息：高层代号(=)、安装地点(++)、位置代号(+)、X、Y。\n" +
                "  • 结构信息取自文本所在页；默认按“结构标识符管理”里的顺序，再按 X 从小到大、Y 从大到小。\n" +
                "  • 灰色只读列仅显示，不能修改。\n\n" +
                "【换行与排版】\n" +
                "  • 单元格里按 Ctrl+Enter 换行（显示为 ¶，保存后即为真正换行）；也可右键选“换行”。\n" +
                "  • 拖动格子下边缘可改该行高度，拖动表头分隔线可改列宽；右键“调整列宽”自动适配。\n" +
                "  • 双击任意列标题可按该列排序：升序 → 降序 → 恢复默认排序。\n" +
                "  • 可像 Excel 一样框选后复制、粘贴、删除。\n\n" +
                "【窗口用法】\n" +
                "  • 本窗口为浮动常驻窗口，打开时不抢焦点，可一边操作图形编辑器一边编辑；再次执行命令会回到已打开的窗口。\n\n" +
                "【格子颜色】\n" +
                "  • 灰色：只读，不能修改。\n" +
                "  • 黄色：已修改、还没保存（同一行的原值列、行号列也跟随变黄）。\n" +
                "  • 绿色：已修改并保存（同一行的原值列、行号列也跟随变绿）。\n\n" +
                "【日志文件】\n" +
                "  当前日志文件：\n" + AddInLogger.ActiveLogFilePath + "\n" +
                "  解析过程：" + AddInLogger.ResolutionNote,
        };
        tabHelp.Controls.Add(help);

        tabs.TabPages.Add(tabEdit);
        tabs.TabPages.Add(tabHelp);
        Controls.Add(tabs);
    }

    /// <summary>新增一个只读结构/坐标信息列：不可排序、灰底；坐标列右对齐。</summary>
    private void AddInfoColumn(string name, string header, int index, int width)
    {
        var col = new DataGridViewTextBoxColumn
        {
            Name = name,
            HeaderText = header,
            Width = width,
            ReadOnly = true,
            SortMode = DataGridViewColumnSortMode.NotSortable, // 双击排序由本类统一处理，箭头自绘
            DefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = InfoHeaderGray, // 结构/坐标只读列底色与列标题一致
                Alignment = name == "x" || name == "y"
                    ? DataGridViewContentAlignment.MiddleRight
                    : DataGridViewContentAlignment.MiddleLeft,
            },
        };
        _grid.Columns.Insert(index, col);
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
    /// 自动列宽：宽度取“数据内容”与“表头最长一行（多行标题按最长行算）”的较大者，
    /// 再统一为排序箭头预留右侧空间；限制在 60~420px。
    /// </summary>
    private void AutoFitColumns()
    {
        const int minW = 60, maxW = 420;
        const int pad = 12;          // 文字左右内边距合计
        const int arrowReserve = 20; // 给列标题排序箭头预留的右侧宽度
        var flags = TextFormatFlags.SingleLine | TextFormatFlags.Left | TextFormatFlags.NoPadding;

        using var g = _grid.CreateGraphics();
        var cellFont = _grid.Font;
        var headFont = _grid.ColumnHeadersDefaultCellStyle.Font ?? _grid.Font;

        foreach (DataGridViewColumn col in _grid.Columns)
        {
            if (!col.Visible) { continue; }
            var best = 0;

            // 表头：多行标题按最长一行计宽
            foreach (var line in (col.HeaderText ?? string.Empty).Split('\n'))
            {
                best = Math.Max(best, TextRenderer.MeasureText(g, line, headFont, Size.Empty, flags).Width);
            }

            if (col is DataGridViewCheckBoxColumn)
            {
                best = Math.Max(best, 18); // 复选框本体占位
            }

            // 数据单元格内容（¶ 单行显示，不换行）
            foreach (DataGridViewRow row in _grid.Rows)
            {
                var s = row.Cells[col.Index].Value?.ToString();
                if (string.IsNullOrEmpty(s)) { continue; }
                foreach (var line in s!.Split('\n'))
                {
                    best = Math.Max(best, TextRenderer.MeasureText(g, line, cellFont, Size.Empty, flags).Width);
                }
            }

            var w = best + pad + arrowReserve;
            col.Width = Math.Max(minW, Math.Min(maxW, w));
        }
        AddInLogger.Debug("AutoFitColumns 完成（含表头最长行 + 排序箭头余量）");
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

        // 1) 先定基础状态色（结构/坐标等只读信息列不进下面两支，保留列默认灰）
        bool modifiedColor = false; // 本行本格是否承载“修改色”，聚焦高亮不得覆盖它
        if (IsOrigCol(col))
        {
            // 原值列底色完全跟随该行新值侧：有未保存→黄，否则有已保存改动→绿，否则只读灰
            var rowColor = RowModificationColor(row);
            e.CellStyle!.BackColor = rowColor ?? ReadOnlyGray;
            modifiedColor = rowColor.HasValue;
        }
        else if (IsEditableStateCol(col))
        {
            if (CellIsDirty(row, col)) { e.CellStyle!.BackColor = DirtyYellow; modifiedColor = true; }
            else if (CellSavedChanged(row, col)) { e.CellStyle!.BackColor = SavedGreen; modifiedColor = true; }
            else if (_grid[col, row].ReadOnly) { e.CellStyle!.BackColor = ReadOnlyGray; }
            // else 保持默认白底
        }

        // 2) 单元格聚焦：当前格所在行/列（排除当前格本身）整体置极浅蓝；承载修改色的格子不覆盖
        if (!modifiedColor && _focusCellChk is { Checked: true })
        {
            var cur = _grid.CurrentCell;
            if (cur != null)
            {
                var sameRow = cur.RowIndex == row;
                var sameCol = cur.ColumnIndex == col;
                if ((sameRow || sameCol) && !(sameRow && sameCol))
                {
                    e.CellStyle!.BackColor = FocusTint;
                }
            }
        }
    }

    private bool IsOrigCol(int col) =>
        col == ColOrigMultilang || col == ColOrigNoAuto ||
        col == _origTextCol || _origLangCol.ContainsValue(col);

    // —— 自绘：①聚焦时给当前列头/行首上浅蓝；②当前排序列列头右侧叠加 ▲/▼ 字符 ——
    private void GridOnCellPainting(object? sender, DataGridViewCellPaintingEventArgs e)
    {
        var isColHeader = e.RowIndex == -1 && e.ColumnIndex >= 0;
        var isRowHeader = e.ColumnIndex == -1 && e.RowIndex >= 0;
        if (!isColHeader && !isRowHeader) { return; }

        var cur = _grid.CurrentCell;
        var focusOn = _focusCellChk is { Checked: true };

        // 行号列：修改底色完全跟随该行新值侧（黄=有未保存 / 绿=有已保存改动），优先级高于聚焦
        System.Drawing.Color? bg = null;
        if (isRowHeader && e.RowIndex < _baseline.Count)
        {
            bg = RowModificationColor(e.RowIndex);
        }

        // 聚焦：当前列头、当前行首（左上角交叉格不处理）；行首已有修改色则不覆盖
        if (bg == null && focusOn && cur != null
            && ((isColHeader && e.ColumnIndex == cur.ColumnIndex)
                || (isRowHeader && e.RowIndex == cur.RowIndex)))
        {
            bg = FocusHeaderTint;
        }

        if (bg != null)
        {
            // 先自填底色，再让系统绘制边框/内容（含行首箭头、列头文字、行号），但不重绘背景
            using (var b = new System.Drawing.SolidBrush(bg.Value))
            {
                e.Graphics.FillRectangle(b, e.CellBounds);
            }
            e.Paint(e.ClipBounds, DataGridViewPaintParts.All & ~DataGridViewPaintParts.Background);
            DrawSortArrow(e, isColHeader);
            e.Handled = true;
            return;
        }

        // 无自定义底色：仅当前排序列列头需要在系统绘制后叠加箭头
        if (!isColHeader || _sortDir == 0 || e.ColumnIndex != _sortCol) { return; }
        e.Paint(e.ClipBounds, DataGridViewPaintParts.All);
        DrawSortArrow(e, true);
        e.Handled = true;
    }

    /// <summary>在列头右侧绘制当前排序方向箭头（▲/▼）。</summary>
    private void DrawSortArrow(DataGridViewCellPaintingEventArgs e, bool isColHeader)
    {
        if (!isColHeader || _sortDir == 0 || e.ColumnIndex != _sortCol) { return; }
        var arrow = _sortDir > 0 ? "\u25B2" : "\u25BC"; // ▲ / ▼
        var rect = new System.Drawing.Rectangle(e.CellBounds.Right - 18, e.CellBounds.Top, 15, e.CellBounds.Height);
        using var arrowFont = new Font(_grid.Font.FontFamily, 7.5f, FontStyle.Regular);
        TextRenderer.DrawText(e.Graphics, arrow, arrowFont, rect,
            System.Drawing.Color.FromArgb(70, 70, 70),
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
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

    /// <summary>
    /// 该行“新值侧”整体修改色：任一新值格未保存→黄（优先）；否则任一已保存且变了→绿；都没有→null。
    /// 供原值列与行号列“完全跟随新值列”上色。
    /// </summary>
    private System.Drawing.Color? RowModificationColor(int row)
    {
        var saved = false;
        for (var c = 0; c < _grid.Columns.Count; c++)
        {
            if (!IsEditableStateCol(c)) { continue; }
            if (CellIsDirty(row, c)) { return DirtyYellow; }
            if (CellSavedChanged(row, c)) { saved = true; }
        }
        return saved ? SavedGreen : (System.Drawing.Color?)null;
    }

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

        // 左侧：状态提示区（未保存数量 / 已保存 / 自动保存状态）
        _statusLabel = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(12, 0, 0, 0),
            AutoEllipsis = true,
            ForeColor = System.Drawing.Color.FromArgb(90, 90, 90),
        };

        layout.Controls.Add(_statusLabel, 0, 0);
        layout.Controls.Add(btnPanel, 1, 0);
        panel.Controls.Add(layout);
        Controls.Add(panel);
    }

    /// <summary>
    /// 为高层代号/安装地点/位置代号三段分别建立“结构标识符管理”顶层顺序表。
    /// 顶层节点（无父节点）即页“主标识符”可选取值，按其在管理对话框中的 SortId 升序编号。
    /// </summary>
    private void BuildRankMaps()
    {
        _plantRank.Clear();
        _placeRank.Clear();
        _locationRank.Clear();
        if (_project == null) { return; }
        try
        {
            FillRank(_project, Project.Hierarchy.Plant, _plantRank);
            FillRank(_project, Project.Hierarchy.Place, _placeRank);
            FillRank(_project, Project.Hierarchy.Location, _locationRank);
        }
        catch (Exception ex)
        {
            AddInLogger.Warn("读取结构标识符管理顺序失败，默认排序退化为名称序：" + ex.Message);
        }
    }

    private static void FillRank(Project project, Project.Hierarchy h, Dictionary<string, double> map)
    {
        Location[] nodes;
        try { nodes = project.GetLocationObjects(h); }
        catch (Exception ex)
        {
            AddInLogger.Debug("GetLocationObjects(" + h + ") 失败：" + ex.Message);
            return;
        }
        if (nodes == null || nodes.Length == 0) { return; }

        var roots = new List<Location>();
        foreach (var node in nodes)
        {
            bool isRoot;
            try { isRoot = node.ParentNode == null; }
            catch { isRoot = true; } // 取父级异常时按顶层处理，保证不丢排序
            if (isRoot) { roots.Add(node); }
        }
        roots.Sort((a, b) => a.SortId.CompareTo(b.SortId));

        // 按“结构标识符管理”树做先序遍历：顶层按 SortId，进入子层后同样按 SortId，
        // 给每个节点的完整标识符路径编号，多级结构（如 =A.A1）也能正确排序。
        var seq = 0.0;
        foreach (var root in roots)
        {
            seq = WalkLocationDfs(root, map, ref seq);
        }
        AddInLogger.Debug("结构顺序(" + h + ") 节点数=" + map.Count);
    }

    private static double WalkLocationDfs(Location node, Dictionary<string, double> map, ref double seq)
    {
        var key = NormIdent(node.Name);
        if (key.Length > 0 && !map.ContainsKey(key)) { map[key] = seq; seq += 1; }

        Location[]? children = null;
        try { children = node.SubNodes; }
        catch (Exception ex) { AddInLogger.Debug("SubNodes 失败(" + node.Name + ")：" + ex.Message); }
        if (children != null && children.Length > 0)
        {
            var kids = children.ToList();
            kids.Sort((a, b) => a.SortId.CompareTo(b.SortId));
            foreach (var child in kids) { seq = WalkLocationDfs(child, map, ref seq); }
        }
        return seq;
    }

    /// <summary>归一化结构标识符用于排序匹配：去前缀符号/空白/点间空格，大写。兼容 "=A.A1" / "A.A1"。</summary>
    private static string NormIdent(string? s)
    {
        if (string.IsNullOrEmpty(s)) { return string.Empty; }
        var t = s!.Trim();
        while (t.Length > 0 && (t[0] == '=' || t[0] == '+' || t[0] == '&' || t[0] == '#' || t[0] == '@')) { t = t.Substring(1).Trim(); }
        return t.Replace(" ", string.Empty).ToUpperInvariant();
    }

    private double RankOf(Dictionary<string, double> map, string ident)
    {
        return map.TryGetValue(NormIdent(ident), out var r) ? r : double.MaxValue;
    }

    /// <summary>读取一个文本对象所属页的三段完整结构标识符与图形坐标，组装只读信息（并填好排序 rank）。</summary>
    private RowMeta BuildMeta(TextBase t)
    {
        var m = new RowMeta();
        try
        {
            var page = t.Page;
            if (page != null)
            {
                var pp = page.Properties;
                // 用“完整/已解析”属性：继承上级页、含子结构的实际标识；未展开段 DESIGNATION_PLANT 可能为空。
                m.Plant = PageIdent(pp.DESIGNATION_FULLPLANT);
                m.Place = PageIdent(pp.DESIGNATION_FULLPLACEOFINSTALLATION);
                m.Location = PageIdent(pp.DESIGNATION_FULLLOCATION);
                AddInLogger.Debug("页结构 页=" + page.Name
                    + " 高层=[" + m.Plant + "] 安装=[" + m.Place + "] 位置=[" + m.Location + "]");
            }
        }
        catch (Exception ex)
        {
            AddInLogger.Debug("读取结构标识符失败：" + ex.Message);
        }

        try
        {
            var pt = t.Location;
            m.X = pt.X;
            m.Y = pt.Y;
        }
        catch (Exception ex)
        {
            AddInLogger.Debug("读取坐标失败：" + ex.Message);
        }

        m.PlantRank = RankOf(_plantRank, m.Plant);
        m.PlaceRank = RankOf(_placeRank, m.Place);
        m.LocationRank = RankOf(_locationRank, m.Location);
        return m;
    }

    private static string PageIdent(PropertyValue? v)
    {
        if (v == null || v.IsEmpty) { return string.Empty; }
        var s = v.ToString();
        return s == null ? string.Empty : s.Trim();
    }

    /// <summary>项目稳定标识（链接完整路径）；取不到时退化为 null。用于判断是否同一项目。</summary>
    private static string SafeProjectKey(Project? p)
    {
        if (p == null) { return string.Empty; }
        try { return p.ProjectFullName ?? string.Empty; }
        catch { return string.Empty; }
    }

    /// <summary>坐标（mm）显示：整数不带小数点，最多 3 位小数。</summary>
    private static string FormatCoord(double v) => v.ToString("0.###");

    /// <summary>开窗默认排序入口：结构标识符管理顺序 → X 升 → Y 降。</summary>
    private void ApplyDefaultOrder()
    {
        _sortCol = -1;
        _sortDir = 0;
        ApplySort(ColIndex, 0);
    }

    /// <summary>
    /// 窗口已常驻时，用最新选择集刷新表格行。选择集未变化则什么都不做；
    /// 存在未保存修改时弹窗询问（保存并刷新/丢弃刷新/取消）。语言集合或项目变化时连列一起重建。
    /// 返回 true 表示已刷新，false 表示用户取消或选择未变化。
    /// </summary>
    public bool ReloadSelection(List<TextBase> texts, ISOCode.Language sourceLang,
        List<ISOCode.Language> projectLangs, Project? project)
    {
        if (texts.Count == 0) { return false; }

        // 选择集是否与当前相同：同一项目（按项目链接完整路径）+ 同样的对象 DBID 顺序
        var newProjectKey = SafeProjectKey(project);
        var oldProjectKey = SafeProjectKey(_project);
        var sameProject = string.Equals(newProjectKey, oldProjectKey, StringComparison.OrdinalIgnoreCase);
        var sameTexts = sameProject
            && texts.Count == _texts.Count
            && texts.Select(tx => tx.DatabaseIdentifier)
                    .SequenceEqual(_texts.Select(tx => tx.DatabaseIdentifier));
        if (sameTexts) { return false; }

        // 有未保存修改：先问怎么办
        if (_grid.IsCurrentCellInEditMode) { _grid.EndEdit(); }
        var dirtyCount = DirtyRows().Count;
        if (dirtyCount > 0)
        {
            var ans = MessageBox.Show(
                "当前有 " + dirtyCount + " 处修改尚未保存。\n\n" +
                "“是”保存这些修改后用新选择刷新；\n“否”放弃未保存修改并刷新；\n“取消”保持现状。",
                "文本批量编辑", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (ans == DialogResult.Cancel) { return false; }
            if (ans == DialogResult.Yes && !SaveDirty(quietSuccess: true)) { return false; } // 保存失败（如回读不一致）则中止
        }

        // 项目或语言集合变化 → 连列结构一起重建；否则只重建行
        var langChanged = projectLangs.Count != _projectLangs.Count || projectLangs.Except(_projectLangs).Any();
        _texts = texts;
        _project = project;
        _sourceLang = sourceLang;
        _projectLangs = projectLangs;
        Text = "文本批量编辑（源语言：" + LangHelper.Code(sourceLang) + "，共 " + texts.Count + " 个文本）";

        DisposeAutoSaveTimer();
        if (langChanged)
        {
            BuildColumns();
        }
        LoadRows();
        ApplyDefaultOrder();
        UpdateColumnVisibility();
        UpdateApplyEnabled();
        AddInLogger.Info("ReloadSelection: 已用新选择集刷新 count=" + texts.Count);
        return true;
    }

    private void LoadRows()
    {
        AddInLogger.Info("LoadRows: count=" + _texts.Count
            + " 项目语言=[" + string.Join(",", _projectLangs.Select(LangHelper.Code)) + "] 源语言=" + _sourceLang);
        var unknown = ISOCode.Language.L___;

        _baseline.Clear();
        _initial.Clear();
        _origIndex.Clear();
        _meta.Clear();
        BuildRankMaps();
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
                row.Cells[ColIndex].Value = (i + 1).ToString(); // 临时值，ApplyDefaultOrder 后被固定排名覆盖
                row.HeaderCell.Value = (i + 1).ToString();       // 行头行号
                row.Cells[ColType].Value = typeName;

                // 只读结构/坐标信息（坐标排序用原始 double，显示保留 1 位小数）
                var meta = BuildMeta(t);
                row.Cells[ColPlant].Value = meta.Plant;
                row.Cells[ColPlace].Value = meta.Place;
                row.Cells[ColLocation].Value = meta.Location;
                row.Cells[ColX].Value = FormatCoord(meta.X);
                row.Cells[ColY].Value = FormatCoord(meta.Y);
                _meta.Add(meta);

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
                _origIndex.Add(i);
            }
        }
        finally
        {
            _syncing = false;
        }
    }

    // —— 双击列标题：顺序 → 倒序 → 默认（恢复开窗原始顺序）——
    private void GridOnHeaderDoubleClick(object? sender, DataGridViewCellMouseEventArgs e)
    {
        if (e.ColumnIndex < 0) { return; }
        // 三级循环：同列 1(升) → -1(降) → 0(默认)；换新列则从升序开始
        if (e.ColumnIndex == _sortCol && _sortDir != 0) { _sortDir = _sortDir == 1 ? -1 : 0; }
        else { _sortCol = e.ColumnIndex; _sortDir = 1; }
        if (_sortDir == 0) { _sortCol = -1; }
        ApplySort(e.ColumnIndex, _sortDir);
    }

    private sealed class SortView
    {
        public object?[] Values = null!;
        public int Height;
        public TextBase T = null!;
        public RowState Base = null!;
        public RowState Init = null!;
        public int Orig;
        public RowMeta Meta = null!;
    }

    private void ApplySort(int col, int dir)
    {
        if (_grid.IsCurrentCellInEditMode) { _grid.EndEdit(); }
        var n = _grid.Rows.Count;
        if (n == 0) { return; }

        var colCount = _grid.Columns.Count;
        var views = new List<SortView>(n);
        for (var i = 0; i < n; i++)
        {
            var vals = new object?[colCount];
            for (var c = 0; c < colCount; c++) { vals[c] = _grid[c, i].Value; }
            views.Add(new SortView
            {
                Values = vals,
                Height = _grid.Rows[i].Height,
                T = _texts[i],
                Base = _baseline[i],
                Init = _initial[i],
                Orig = _origIndex[i],
                Meta = _meta[i],
            });
        }

        // 稳定排序：
        //   dir=0（默认/第三档复位）→ 结构标识符管理顺序(高层→安装→位置) → X 升 → Y 降 → 开窗序号
        //   dir≠0 → 该列值（# 按整数、复选框按布尔、坐标按数值、其余按文本），同值回落到默认规则
        IEnumerable<SortView> q;
        if (dir == 0)
        {
            q = DefaultOrdered(views);
        }
        else
        {
            var isCheck = _grid.Columns[col] is DataGridViewCheckBoxColumn;
            IOrderedEnumerable<SortView> primary;
            if (col == ColIndex)
            {
                // 序号列承载“默认排序固定排名”，按数值升=默认序；双击可看倒序
                double RankNum(SortView v) => double.TryParse(v.Values[col] as string, out var x) ? x : 0;
                primary = dir > 0 ? views.OrderBy(RankNum) : views.OrderByDescending(RankNum);
            }
            else if (col == ColX)
            {
                primary = dir > 0 ? views.OrderBy(v => v.Meta.X) : views.OrderByDescending(v => v.Meta.X);
            }
            else if (col == ColY)
            {
                // Y 单列排序也尊重用户点击方向；默认规则里的“Y 降”在 DefaultOrdered 内
                primary = dir > 0 ? views.OrderBy(v => v.Meta.Y) : views.OrderByDescending(v => v.Meta.Y);
            }
            else if (isCheck)
            {
                primary = dir > 0
                    ? views.OrderBy(v => Convert.ToBoolean(v.Values[col] ?? false) ? 1 : 0)
                    : views.OrderByDescending(v => Convert.ToBoolean(v.Values[col] ?? false) ? 1 : 0);
            }
            else
            {
                primary = dir > 0
                    ? views.OrderBy(v => (v.Values[col] as string) ?? string.Empty)
                    : views.OrderByDescending(v => (v.Values[col] as string) ?? string.Empty);
            }
            // 同值时回落到默认结构顺序，保证排列确定、不随原始网格顺序漂移
            q = primary
                .ThenBy(v => v.Meta.PlantRank).ThenBy(v => v.Meta.PlaceRank).ThenBy(v => v.Meta.LocationRank)
                .ThenBy(v => v.Meta.X).ThenByDescending(v => v.Meta.Y).ThenBy(v => v.Orig);
        }
        var sorted = q.ToList();

        // 同步重排并行列表（显示行 i 始终对应 _texts[i]，写回/着色逻辑不变）
        for (var i = 0; i < n; i++)
        {
            _texts[i] = sorted[i].T;
            _baseline[i] = sorted[i].Base;
            _initial[i] = sorted[i].Init;
            _origIndex[i] = sorted[i].Orig;
            _meta[i] = sorted[i].Meta;
        }

        // 重建网格行（带回原值与手调行高）
        _syncing = true;
        try
        {
            _grid.Rows.Clear();
            var ri = 0;
            foreach (var v in sorted)
            {
                var row = _grid.Rows[_grid.Rows.Add()];
                for (var c = 0; c < colCount; c++) { row.Cells[c].Value = v.Values[c]; }
                // 行头 = 当前显示行号（随排序即时变）；底色与列标题一致、无列标题
                row.HeaderCell.Value = (ri + 1).ToString();
                // 序号列：仅在“默认排序”时盖章为固定排名 1..n；按其它列排序时沿用已盖章值（随文本一起搬运）
                if (dir == 0) { row.Cells[ColIndex].Value = (ri + 1).ToString(); }
                row.Height = v.Height;
                SetRowEditable(ri, Convert.ToBoolean(v.Values[ColMultilang] ?? false));
                ri++;
            }
        }
        finally
        {
            _syncing = false;
        }

        // 触发列头重绘，由 CellPainting 在当前排序列表头叠加箭头字符
        _grid.AutoResizeRowHeadersWidth(DataGridViewRowHeadersWidthSizeMode.AutoSizeToAllHeaders); // 行号位数变化时列宽跟随
        _grid.Invalidate(_grid.DisplayRectangle);
        _grid.Refresh();

        UpdateApplyEnabled();
        AddInLogger.Debug("排序：列=" + col + " 方向=" + (dir == 0 ? "默认(结构→X↑→Y↓)" : dir > 0 ? "升序" : "降序"));
    }

    /// <summary>默认顺序：结构标识符管理顺序（高层代号→安装地点→位置代号）→ X 升 → Y 降 → 开窗序号。</summary>
    private static IOrderedEnumerable<SortView> DefaultOrdered(IEnumerable<SortView> views) =>
        views.OrderBy(v => v.Meta.PlantRank)
             .ThenBy(v => v.Meta.PlaceRank)
             .ThenBy(v => v.Meta.LocationRank)
             .ThenBy(v => v.Meta.X)
             .ThenByDescending(v => v.Meta.Y)
             .ThenBy(v => v.Orig);

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
        var dirty = DirtyRows().Count;
        if (_applyBtn != null) { _applyBtn.Enabled = dirty > 0; }
        UpdateStatus(dirty);
        if (_grid != null) { _grid.Invalidate(); } // 触发 CellFormatting 重算脏/已保存底色
    }

    /// <summary>底部左侧状态提示：未保存数量 / 全部已保存 / 自动保存状态。</summary>
    private void UpdateStatus(int dirtyCount)
    {
        if (_statusLabel == null) { return; }
        var auto = _autoSaveChk != null && _autoSaveChk.Checked;
        if (dirtyCount > 0)
        {
            _statusLabel.Text = "● " + dirtyCount + " 处未保存" + (auto ? "（将自动保存）" : string.Empty);
            _statusLabel.ForeColor = System.Drawing.Color.FromArgb(190, 90, 0); // 醒目橙
        }
        else
        {
            _statusLabel.Text = auto ? "自动保存已开启 · 全部已保存" : "已全部保存";
            _statusLabel.ForeColor = System.Drawing.Color.FromArgb(60, 130, 70); // 绿
        }
    }

    /// <summary>开关“自动保存”：开启时把当前未保存内容安排一次写回；始终刷新状态。</summary>
    private void OnAutoSaveToggled()
    {
        var on = _autoSaveChk.Checked;
        AddInLogger.Debug("自动保存开关=" + on);
        if (on) { ScheduleAutoSave(); } else { DisposeAutoSaveTimer(); }
        UpdateApplyEnabled();
    }

    private void ScheduleAutoSave()
    {
        if (_saving || _autoSaveChk == null || !_autoSaveChk.Checked) { return; }
        if (IsDisposed) { return; }
        if (_autoSaveTimer == null)
        {
            _autoSaveTimer = new System.Windows.Forms.Timer { Interval = 600 };
            _autoSaveTimer.Tick += (_, _) =>
            {
                _autoSaveTimer.Stop();
                if (IsDisposed || _saving || _autoSaveChk == null || !_autoSaveChk.Checked) { return; }
                if (DirtyRows().Count == 0) { return; }
                _saving = true;
                try
                {
                    AddInLogger.Debug("自动保存触发");
                    SaveDirty(quietSuccess: true);
                }
                finally
                {
                    _saving = false;
                }
            };
        }
        _autoSaveTimer.Stop();
        _autoSaveTimer.Start(); // 600ms 防抖：连续编辑合并为一次写回/一个撤销点
    }

    private void DisposeAutoSaveTimer()
    {
        if (_autoSaveTimer != null) { _autoSaveTimer.Stop(); _autoSaveTimer.Dispose(); _autoSaveTimer = null; }
    }

    private void EditingTextChanged(object? sender, EventArgs e)
    {
        if (_applyBtn is { Enabled: false }) { _applyBtn.Enabled = true; }
        ScheduleAutoSave(); // 当前格未提交也计时；SaveDirty 开头会先 EndEdit 提交
    }

    /// <summary>
    /// 右键在 MouseUp 才弹出菜单，故用 MouseDown 提前校正目标：
    /// 右键点中的格子若不在当前选区（如已选 A1 却右键 B1），先清空多选并只选中该格，
    /// 随后弹出的复制/剪切/粘贴/清除等即以该格（新选区）为目标；右键本就在选区内则保留多选。
    /// </summary>
    private void GridOnCellMouseDown(object? s, DataGridViewCellMouseEventArgs e)
    {
        if (e.Button != MouseButtons.Right || e.RowIndex < 0 || e.ColumnIndex < 0) { return; }
        var target = _grid[e.ColumnIndex, e.RowIndex];
        if (!target.Selected)
        {
            _grid.ClearSelection();
            target.Selected = true;
            _grid.CurrentCell = target;
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
