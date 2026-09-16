using Eplan.EplApi.ApplicationFramework;
using Eplan.EplApi.Base;
using Eplan.EplApi.Gui;
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using EplContextMenu = Eplan.EplApi.Gui.ContextMenu;

namespace EA.EplAddIn.TextBatchEdit;

public class TextBatchEditAddIn : IEplAddIn
{
    private const string CtxMenuText = "文本批量编辑";
    private const int WhCallWndProc = 4;          // 消息送达窗口过程"之前"回调（必须早于 BCG 的 OnInitMenuPopup）
    private const int WmInitMenu = 0x0116;        // 菜单激活（更早），wParam=HMENU
    private const int WmInitMenuPopup = 0x0117;   // 弹出菜单显示前，wParam=HMENU
    private const uint MF_BYPOSITION = 0x0400;

    private IntPtr _hookHandle = IntPtr.Zero;
    private NativeMethods.CallWndProc? _hookProc; // 必须常驻字段，防 GC 回收委托

    public bool OnInit()
    {
        AddInLogger.Info("OnInit: " + typeof(TextBatchEditAddIn).Assembly.FullName);
        AddInLogger.Info("OnInit: log dir = " + AddInLogger.DirectoryPath);
        Application.ThreadException += (_, e) =>
            AddInLogger.Error("Application.ThreadException (UI thread)", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            AddInLogger.Error("AppDomain.UnhandledException (isTerminating=" + e.IsTerminating + ")",
                e.ExceptionObject as Exception);
        return true;
    }

    public bool OnExit()
    {
        AddInLogger.Info("OnExit: EPLAN shutting down");
        Unhook();
        return true;
    }

    public bool OnInitGui()
    {
        AddInLogger.Info("OnInitGui: registering menus");
        new Eplan.EplApi.Gui.Menu().AddMenuItem(
            CtxMenuText, TextBatchEditAction.ActionName);

        // 图纸（图形编辑器）右键：Editor/Ged，菜单项常驻注册一次。
        // GED 右键不回调 IEplActionEnable、ContextMenu 也无置灰接口，且 EPLAN 是 MFC/BCG 消息循环
        // （WinForms IMessageFilter / onActionEnd 均实测无效）；标准 MF_GRAYED 也拦不住 BCG 自绘菜单的命令路由。
        // 故在本 UI 线程装 WH_CALLWNDPROC 线程钩子：在 BCG 的 OnInitMenuPopup 处理 WM_INITMENU(POPUP)
        // “之前”，若选择集不含文本，就把本插件项从本次弹出的临时 HMENU 中 DeleteMenu 删除——
        // BCG 遍历 HMENU 时该项已不存在，既不绘制也不可点；含文本时不动，默认亮。
        // 只删每次弹出的临时菜单副本，不动注册，下次弹出框架重新生成。
        try
        {
            new EplContextMenu().AddMenuItem(
                new ContextMenuLocation { DialogName = "Editor", ContextMenuName = "Ged" },
                CtxMenuText, TextBatchEditAction.ActionName, false, true);
            AddInLogger.Info("右键菜单：常驻项已注册 (Editor/Ged)");
        }
        catch (Exception ex)
        {
            AddInLogger.Error("右键菜单：注册失败", ex);
        }

        // 页导航器（页树）右键：PmPageObjectTreeDialog / 1007；与图纸右键同名、同一 Action（内部支持图面文本与所选页两路）。
        try
        {
            new EplContextMenu().AddMenuItem(
                new ContextMenuLocation { DialogName = "PmPageObjectTreeDialog", ContextMenuName = "1007" },
                CtxMenuText, TextBatchEditAction.ActionName, false, true);
            AddInLogger.Info("右键菜单：常驻项已注册 (PmPageObjectTreeDialog/1007)");
        }
        catch (Exception ex)
        {
            AddInLogger.Error("页导航器右键菜单：注册失败", ex);
        }

        // 查找结果列表右键：XSeSearchResultsTab1 / 1002（“查找结果”选项卡，标识取自 SearchAndReplaceGui 模块）。
        // 同名、同一 Action；结果行对象经 SelectionSet 进入——文本结果走“选中文本”，页结果走“选中页→整页”。
        try
        {
            new EplContextMenu().AddMenuItem(
                new ContextMenuLocation { DialogName = "XSeSearchResultsTab1", ContextMenuName = "1002" },
                CtxMenuText, TextBatchEditAction.ActionName, false, true);
            AddInLogger.Info("右键菜单：常驻项已注册 (XSeSearchResultsTab1/1002)");
        }
        catch (Exception ex)
        {
            AddInLogger.Error("查找结果右键菜单：注册失败", ex);
        }

        InstallHook();
        return true;
    }

    public bool OnRegister(ref bool bLoadOnStart)
    {
        bLoadOnStart = true;
        AddInLogger.Info("OnRegister: bLoadOnStart=true, assembly = "
            + typeof(TextBatchEditAddIn).Assembly.Location);
        return true;
    }

    public bool OnUnregister()
    {
        AddInLogger.Info("OnUnregister");
        Unhook();
        return true;
    }

    private void InstallHook()
    {
        if (_hookHandle != IntPtr.Zero) { return; }
        try
        {
            _hookProc = HookCallback;
            uint threadId = NativeMethods.GetCurrentThreadId(); // OnInitGui 运行在 MFC UI 线程
            _hookHandle = NativeMethods.SetWindowsHookExW(WhCallWndProc, _hookProc, IntPtr.Zero, threadId);
            if (_hookHandle == IntPtr.Zero)
            {
                AddInLogger.Error("安装 WH_CALLWNDPROC 钩子失败，Win32Error=" + Marshal.GetLastWin32Error());
            }
            else
            {
                AddInLogger.Info("已安装 WH_CALLWNDPROC 线程钩子 (tid=" + threadId + ") 用于右键项按需删除");
            }
        }
        catch (Exception ex)
        {
            AddInLogger.Error("安装钩子异常", ex);
        }
    }

    private void Unhook()
    {
        if (_hookHandle != IntPtr.Zero)
        {
            try { NativeMethods.UnhookWindowsHookEx(_hookHandle); }
            catch (Exception ex) { AddInLogger.Debug("卸载钩子异常：" + ex.Message); }
            _hookHandle = IntPtr.Zero;
        }
        _hookProc = null;
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        // 任何异常都不能抛出钩子过程（会直接崩溃宿主 EPLAN）
        try
        {
            if (nCode >= 0 && lParam != IntPtr.Zero)
            {
                NativeMethods.CWPSTRUCT s = Marshal.PtrToStructure<NativeMethods.CWPSTRUCT>(lParam);
                // 在窗口过程（BCG 的 OnInitMenuPopup）处理“之前”介入
                if (s.message == WmInitMenuPopup || s.message == WmInitMenu)
                {
                    RemoveItemIfNeeded(s.wParam, s.hwnd); // wParam=待初始化菜单 HMENU，hwnd=菜单所属窗口
                }
            }
        }
        catch (Exception ex)
        {
            AddInLogger.Debug("钩子回调异常：" + ex.GetType().Name + " " + ex.Message);
        }
        return NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    private void RemoveItemIfNeeded(IntPtr hMenu, IntPtr hwnd)
    {
        if (hMenu == IntPtr.Zero) { return; }

        // 统一菜单名后，主菜单项也叫“文本批量编辑”。必须区分主菜单栏下拉（常驻、不删）
        // 与右键 TrackPopupMenu（按需删除）：所属窗口是主框架，或该 HMENU 挂在主框架菜单树里，即判为主菜单。
        if (IsMainFramePopup(hMenu, hwnd))
        {
            return;
        }

        int count = NativeMethods.GetMenuItemCount(hMenu);
        if (count <= 0) { return; }

        var sb = new StringBuilder(256);
        for (int i = 0; i < count; i++)
        {
            sb.Length = 0;
            int len = NativeMethods.GetMenuStringW(hMenu, (uint)i, sb, (uint)sb.Capacity, MF_BYPOSITION);
            string text = sb.ToString().Replace("&", "");
            if (len == 0 || text.IndexOf(CtxMenuText, StringComparison.Ordinal) < 0) { continue; }

            // 同名项挂在三处：图纸(Editor/Ged)、页导航器(PmPageObjectTreeDialog/1007)、查找结果(XSeSearchResultsTab1/1002)。
            // 必须按“当前弹出的是哪个菜单”分别判定，不能用一个 OR 条件——
            // 否则 GED 打开页时 GetSelectedPages() 返回的“当前页”会让图纸菜单在未选文本时也出现（退化根因）。
            // 判据（实测光标命中窗口链）：图面图形视图链含 MFC 文档视图类 AfxFrameOrView（…<MDIClient<AfxMDIFrame）；
            // 页导航器/查找结果是停靠面板里的列表（AfxWnd/#32770/Afx:ControlBar 等），不含 AfxFrameOrView。
            // 注意：菜单 owner 被 MFC 路由到主框架，绝不能用 owner hwnd/类名判，只能用右键瞬间光标实际所在窗口。
            string chain = CursorHitChain();
            bool isGedMenu = chain.IndexOf("AfxFrameOrView", StringComparison.Ordinal) >= 0;
            bool enabled = isGedMenu
                ? TextBatchEditAction.SelectionHasText()                       // 图纸：严格只认选中的文本对象
                : (TextBatchEditAction.SelectionHasText()                      // 查找结果：选中结果里的文本对象
                   || TextBatchEditAction.SelectionHasPage());                 // 页导航器/结果：选中页或结构节点→整页
            AddInLogger.Info("右键菜单：来源=" + (isGedMenu ? "图纸(AfxFrameOrView)" : "列表(页导航器/查找结果)")
                + " 可编辑=" + enabled + " cursorHitChain=" + chain + " pos=" + i);
            if (enabled)
            {
                AddInLogger.Info("右键菜单：保留项 pos=" + i + "（菜单项数=" + count + "）");
            }
            else
            {
                // MF_BYPOSITION 必须配合 MF_BYCOMMAND=0；DeleteMenu 会立即重排后续项位置
                bool ok = NativeMethods.DeleteMenu(hMenu, (uint)i, MF_BYPOSITION);
                int after = NativeMethods.GetMenuItemCount(hMenu);
                AddInLogger.Info("右键菜单：既无文本也无选中页，删除项 pos=" + i
                    + "（删前项数=" + count + "，删后项数=" + after + "，DeleteMenu=" + ok + "）");
            }
            return;
        }
    }

    /// <summary>
    /// 返回右键弹出瞬间，光标实际所在窗口沿父链到顶层的类名序列（用 ' &lt; ' 连接）。
    /// 这是区分图面/页树右键的可靠信号——GED 菜单的 WM_INITMENUPOPUP owner 被 MFC 路由到主框架，
    /// 但光标仍停在被点击的真实表面（图面=AfxFrameOrView…MDIClient 链；页树=AfxWnd/#32770/Afx:ControlBar 链）。
    /// </summary>
    private static string CursorHitChain()
    {
        try
        {
            NativeMethods.POINT pt;
            if (!NativeMethods.GetCursorPos(out pt)) { return "(GetCursorPos fail)"; }
            var hit = NativeMethods.WindowFromPoint(pt);
            if (hit == IntPtr.Zero) { return "(no window at " + pt.X + "," + pt.Y + ")"; }
            var parts = new System.Collections.Generic.List<string>();
            for (var cur = hit; cur != IntPtr.Zero; cur = NativeMethods.GetParent(cur))
            {
                parts.Add(GetClass(cur));
            }
            return string.Join(" < ", parts);
        }
        catch (Exception ex) { return "(chain fail: " + ex.GetType().Name + ")"; }
    }

    private static string GetClass(IntPtr hWnd)
    {
        var sb = new StringBuilder(128);
        return NativeMethods.GetClassNameW(hWnd, sb, sb.Capacity) > 0 ? sb.ToString() : string.Empty;
    }

    /// <summary>
    /// 判断待初始化的弹出菜单是否“主菜单栏的下拉”。
    /// 只能依据 HMENU 是否挂在主框架 GetMenu() 的菜单树里——绝不能用“hwnd==主框架”：
    /// 实测 MFC 把图面(GED)右键菜单的 WM_INITMENUPOPUP 也路由到主框架窗口（hwnd 就是 AfxMDIFrame140u），
    /// 用 hwnd 判等会把图面右键误当成主菜单下拉而放行，导致“未选文本仍出现菜单项”。
    /// </summary>
    private static bool IsMainFramePopup(IntPtr hMenu, IntPtr hwnd)
    {
        try
        {
            var frame = Process.GetCurrentProcess().MainWindowHandle;
            if (frame == IntPtr.Zero) { return false; }
            var root = NativeMethods.GetMenu(frame);
            if (root == IntPtr.Zero) { return false; }
            return MenuTreeContains(root, hMenu, 0); // 右键 TrackPopupMenu 是独立菜单，不在此树
        }
        catch (Exception ex)
        {
            AddInLogger.Debug("IsMainFramePopup 判定异常，按非主菜单处理：" + ex.Message);
            return false; // 判定失败时保守按右键菜单处理（维持原有按需删除行为）
        }
    }

    private static bool MenuTreeContains(IntPtr parentMenu, IntPtr target, int depth)
    {
        if (parentMenu == target) { return true; }
        if (depth > 8 || parentMenu == IntPtr.Zero) { return false; } // 菜单层级有限，防止异常递归
        int count = NativeMethods.GetMenuItemCount(parentMenu);
        for (int i = 0; i < count; i++)
        {
            var sub = NativeMethods.GetSubMenu(parentMenu, i);
            if (sub != IntPtr.Zero && MenuTreeContains(sub, target, depth + 1)) { return true; }
        }
        return false;
    }

    private static class NativeMethods
    {
        public delegate IntPtr CallWndProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetWindowsHookExW(int idHook, CallWndProc lpfn, IntPtr hMod, uint dwThreadId);
        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UnhookWindowsHookEx(IntPtr hhk);
        [DllImport("user32.dll")]
        public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] public static extern int GetMenuItemCount(IntPtr hMenu);
        [DllImport("user32.dll")] public static extern IntPtr GetMenu(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern IntPtr GetSubMenu(IntPtr hMenu, int nPos);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetMenuStringW(IntPtr hMenu, uint uIDItem, StringBuilder lpString, uint nMaxCount, uint uFlag);
        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool DeleteMenu(IntPtr hMenu, uint uPosition, uint uFlags);

        [DllImport("user32.dll")] public static extern IntPtr GetParent(IntPtr hWnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetClassNameW(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X; public int Y; }
        [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT pt);
        [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT pt);

        [StructLayout(LayoutKind.Sequential)]
        public struct CWPSTRUCT
        {
            public IntPtr lParam;
            public IntPtr wParam;
            public uint message;
            public IntPtr hwnd;
        }
    }
}
