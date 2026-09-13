using Eplan.EplApi.ApplicationFramework;
using Eplan.EplApi.Base;
using Eplan.EplApi.Gui;
using System;
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
            "批量修改选中文本（中英文）", TextBatchEditAction.ActionName);

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
                    RemoveItemIfNeeded(s.wParam); // wParam = 待初始化菜单的 HMENU
                }
            }
        }
        catch (Exception ex)
        {
            AddInLogger.Debug("钩子回调异常：" + ex.GetType().Name + " " + ex.Message);
        }
        return NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    private void RemoveItemIfNeeded(IntPtr hMenu)
    {
        if (hMenu == IntPtr.Zero) { return; }
        int count = NativeMethods.GetMenuItemCount(hMenu);
        if (count <= 0) { return; }

        var sb = new StringBuilder(256);
        for (int i = 0; i < count; i++)
        {
            sb.Length = 0;
            int len = NativeMethods.GetMenuStringW(hMenu, (uint)i, sb, (uint)sb.Capacity, MF_BYPOSITION);
            string text = sb.ToString().Replace("&", "");
            if (len == 0 || text.IndexOf(CtxMenuText, StringComparison.Ordinal) < 0) { continue; }

            // 命中本插件项。含文本→保留（默认亮、可点）；不含→从本次弹出的临时菜单删除。
            bool hasText = TextBatchEditAction.SelectionHasText();
            if (hasText)
            {
                AddInLogger.Info("右键菜单：选择含文本，保留项 pos=" + i + "（菜单项数=" + count + "）");
            }
            else
            {
                // MF_BYPOSITION 必须配合 MF_BYCOMMAND=0；DeleteMenu 会立即重排后续项位置
                bool ok = NativeMethods.DeleteMenu(hMenu, (uint)i, MF_BYPOSITION);
                int after = NativeMethods.GetMenuItemCount(hMenu);
                AddInLogger.Info("右键菜单：选择无文本，删除项 pos=" + i
                    + "（删前项数=" + count + "，删后项数=" + after + "，DeleteMenu=" + ok + "）");
            }
            return;
        }
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
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetMenuStringW(IntPtr hMenu, uint uIDItem, StringBuilder lpString, uint nMaxCount, uint uFlag);
        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool DeleteMenu(IntPtr hMenu, uint uPosition, uint uFlags);

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
