using System;
using System.Drawing;
using System.Windows.Forms;

namespace EA.EplAddIn.Test;

/// <summary>
/// 表格式编辑窗口（DataGridView）。
/// 支持：单元格就地编辑、矩形区域 Ctrl+C/Ctrl+X/Ctrl+V 块复制粘贴（与 Excel 剪贴板互通）、
/// 右键菜单（剪切/复制/粘贴/清除/全选）、底部空行新增、Delete 清空选区。
/// 暂不接 EPLAN 数据。
/// </summary>
public class TableEditorForm : Form
{
    private readonly DataGridView _grid;
    private ContextMenuStrip? _menu;

    public TableEditorForm()
    {
        AddInLogger.Debug("TableEditorForm..ctor: begin");
        Text = "表格式编辑（演示）";
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(720, 460);
        MinimumSize = new Size(460, 280);
        // 兜底：让窗体先于内部控件收到按键（部分宿主消息循环下 ProcessCmdKey 行为可能不同）
        KeyPreview = true;

        _grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            AllowUserToAddRows = true,
            AllowUserToDeleteRows = true,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            BackgroundColor = Color.White,
            SelectionMode = DataGridViewSelectionMode.CellSelect,
            // 复制时不带行列头，粘贴出来的是纯数据（Excel 风格）
            ClipboardCopyMode = DataGridViewClipboardCopyMode.EnableWithoutHeaderText,
            EditMode = DataGridViewEditMode.EditOnKeystrokeOrF2,
            RowHeadersWidth = 32,
        };

        _grid.Columns.Add("ColId", "标识");
        _grid.Columns.Add("ColName", "名称");
        _grid.Columns.Add("ColQty", "数量");
        _grid.Columns.Add("ColRemark", "备注");

        _grid.Rows.Add("1", "示例元件 A", "2", "可直接双击单元格编辑");
        _grid.Rows.Add("2", "示例元件 B", "5", "支持框选多格复制粘贴");

        BuildContextMenu();
        HookGridEvents();

        var hintLabel = new Label
        {
            Text = "Ctrl+C/X/V 块复制粘贴（可与 Excel 互贴） · Delete 清空 · 右键更多操作",
            AutoSize = true,
            ForeColor = Color.Gray,
            Location = new Point(12, 14),
            Anchor = AnchorStyles.Bottom | AnchorStyles.Left,
        };

        var closeButton = new Button
        {
            Text = "关闭",
            Width = 90,
            Height = 28,
            Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
        };
        closeButton.Click += (_, _) => Close();

        var buttonPanel = new BufferedPanel
        {
            Dock = DockStyle.Bottom,
            Height = 44,
        };

        // 拖动列宽时浮动线可能在底部栏留下残迹，列宽变化后强制整栏重绘清除
        _grid.ColumnWidthChanged += (_, _) => buttonPanel.Invalidate(true);

        void LayoutCloseButton() =>
            closeButton.Location = new Point(buttonPanel.Width - closeButton.Width - 12, 8);
        buttonPanel.Resize += (_, _) => LayoutCloseButton();
        LayoutCloseButton();

        buttonPanel.Controls.Add(hintLabel);
        buttonPanel.Controls.Add(closeButton);

        // Dock 布局按 z-order：先加边缘停靠面板，Fill 最后加才会占据剩余空间
        Controls.Add(buttonPanel);
        Controls.Add(_grid);

        AcceptButton = closeButton;
        CancelButton = closeButton;

        Load += (_, _) => AddInLogger.Debug("TableEditorForm.Load: form shown, grid rows="
            + (_grid.Rows.Count - (_grid.AllowUserToAddRows ? 1 : 0)));
        AddInLogger.Debug("TableEditorForm..ctor: end");
    }

    // ---------- 网格事件（诊断用） ----------

    private void HookGridEvents()
    {
        _grid.CellMouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Right)
            {
                AddInLogger.Debug($"CellMouseClick(Right): row={e.RowIndex} col={e.ColumnIndex}");
            }
        };

        _grid.KeyDown += (_, e) =>
        {
            AddInLogger.Debug("grid.KeyDown: key=" + e.KeyCode + " ctrl=" + e.Control
                + " editing=" + _grid.IsCurrentCellInEditMode);
        };
    }

    // ---------- 右键菜单 ----------

    private void BuildContextMenu()
    {
        _menu = new ContextMenuStrip();

        ToolStripMenuItem Item(string text, string shortcutDisplay, Action action)
        {
            var item = new ToolStripMenuItem(text);
            // 仅显示快捷键文字，不注册 ShortcutKeys，避免与 ProcessCmdKey 双重触发
            item.ShortcutKeyDisplayString = shortcutDisplay;
            item.Click += (_, _) =>
            {
                AddInLogger.Info("ContextMenu click: " + text);
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    AddInLogger.Error("ContextMenu action failed: " + text, ex);
                }
            };
            return item;
        }

        _menu.Items.Add(Item("剪切(&T)", "Ctrl+X", CutSelection));
        _menu.Items.Add(Item("复制(&C)", "Ctrl+C", CopySelection));
        _menu.Items.Add(Item("粘贴(&P)", "Ctrl+V", PasteClipboard));
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(Item("清除内容(&D)", "Delete", ClearSelectionValues));
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(Item("全选(&A)", "Ctrl+A", () => _grid.SelectAll()));

        _menu.Opening += (_, e) =>
        {
            AddInLogger.Info("ContextMenuStrip.Opening: selectedCells=" + _grid.SelectedCells.Count
                + " current=" + DescribeCurrentCell());
        };

        _grid.ContextMenuStrip = _menu;
        AddInLogger.Debug("BuildContextMenu: ContextMenuStrip attached to grid");
    }

    // ---------- 键盘快捷键（ProcessCmdKey 主通道 + KeyDown 兜底） ----------

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        AddInLogger.Debug("ProcessCmdKey: " + keyData + " gridFocused=" + _grid.Focused
            + " editing=" + _grid.IsCurrentCellInEditMode);

        if (_grid.Focused && !_grid.IsCurrentCellInEditMode)
        {
            if (HandleShortcut(keyData))
            {
                return true;
            }
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        // KeyPreview 兜底通道
        if (!_grid.IsCurrentCellInEditMode && e.Control)
        {
            if (HandleShortcut(e.KeyCode | Keys.Control))
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                return;
            }
        }
        base.OnKeyDown(e);
    }

    /// <summary>返回 true 表示已处理。</summary>
    private bool HandleShortcut(Keys keyData)
    {
        switch (keyData)
        {
            case Keys.Control | Keys.C:
                CopySelection();
                return true;
            case Keys.Control | Keys.X:
                CutSelection();
                return true;
            case Keys.Control | Keys.V:
                PasteClipboard();
                return true;
            case Keys.Control | Keys.A:
                _grid.SelectAll();
                return true;
            case Keys.Delete:
                ClearSelectionValues();
                return true;
            default:
                return false;
        }
    }

    // ---------- 剪贴板操作 ----------

    private string DescribeCurrentCell()
    {
        var c = _grid.CurrentCell;
        return c == null ? "<none>" : ("r" + c.RowIndex + "c" + c.ColumnIndex);
    }

    private void CopySelection()
    {
        var content = _grid.GetClipboardContent();
        if (content == null)
        {
            AddInLogger.Warn("CopySelection: GetClipboardContent() returned null, selectedCells="
                + _grid.SelectedCells.Count);
            return;
        }

        var text = content.GetText(TextDataFormat.Text);
        Clipboard.SetText(text);
        AddInLogger.Info($"CopySelection: cells={_grid.SelectedCells.Count} chars={text.Length}");
    }

    private void CutSelection()
    {
        AddInLogger.Info("CutSelection begin");
        CopySelection();
        ClearSelectionValues();
    }

    /// <summary>选区左上角单元格（框选后 CurrentCell 是鼠标抬起格，不能作为粘贴起点）。</summary>
    private DataGridViewCell? SelectionAnchorCell
    {
        get
        {
            if (_grid.SelectedCells.Count == 0)
            {
                return _grid.CurrentCell;
            }

            var minRow = int.MaxValue;
            var minCol = int.MaxValue;
            foreach (DataGridViewCell cell in _grid.SelectedCells)
            {
                if (cell.RowIndex < minRow)
                {
                    minRow = cell.RowIndex;
                }
                if (cell.ColumnIndex < minCol)
                {
                    minCol = cell.ColumnIndex;
                }
            }
            return _grid[minCol, minRow];
        }
    }

    private void PasteClipboard()
    {
        var anchor = SelectionAnchorCell;
        if (anchor == null)
        {
            AddInLogger.Warn("PasteClipboard: no anchor cell, abort");
            return;
        }
        if (anchor.ReadOnly)
        {
            AddInLogger.Warn("PasteClipboard: anchor cell readonly, abort");
            return;
        }

        var text = Clipboard.GetText();
        if (string.IsNullOrEmpty(text))
        {
            AddInLogger.Info("PasteClipboard: clipboard empty, abort");
            return;
        }

        // 统一换行；去掉末尾空行；按 Tab 分列（DataGridView 复制与 Excel 复制均为此格式）
        var lines = text.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');

        var startRow = anchor.RowIndex;
        var startCol = anchor.ColumnIndex;

        // 行数不足时先补行（AllowUserToAddRows 时 Rows.Count 含末尾占位新行）
        var neededLastRow = startRow + lines.Length - 1;
        var added = 0;
        while (_grid.Rows.Count - 1 < neededLastRow)
        {
            _grid.Rows.Add();
            added++;
        }

        var applied = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            var values = lines[i].Split('\t');
            for (var j = 0; j < values.Length; j++)
            {
                var r = startRow + i;
                var c = startCol + j;
                if (r >= _grid.Rows.Count || c >= _grid.Columns.Count)
                {
                    break; // 超出网格范围的部分丢弃（不自动扩列）
                }
                var cell = _grid[c, r];
                if (!cell.ReadOnly && r != _grid.NewRowIndex)
                {
                    cell.Value = values[j] == string.Empty ? null : values[j];
                    applied++;
                }
            }
        }

        AddInLogger.Info($"PasteClipboard: start=({startRow},{startCol}) rows={lines.Length} "
            + $"cols(0)={lines[0].Split('\t').Length} addedRows={added} cellsApplied={applied} chars={text.Length}");
    }

    private void ClearSelectionValues()
    {
        if (_grid.SelectedCells.Count == 0)
        {
            AddInLogger.Debug("ClearSelectionValues: no selected cells");
            return;
        }

        var cleared = 0;
        foreach (DataGridViewCell cell in _grid.SelectedCells)
        {
            if (!cell.ReadOnly && cell.RowIndex != _grid.NewRowIndex)
            {
                cell.Value = null;
                cleared++;
            }
        }
        AddInLogger.Info("ClearSelectionValues: cleared=" + cleared);
    }

    /// <summary>双缓冲 Panel，避免列宽拖动浮动线在底部栏留下残影。</summary>
    private class BufferedPanel : Panel
    {
        public BufferedPanel()
        {
            DoubleBuffered = true;
        }
    }
}
