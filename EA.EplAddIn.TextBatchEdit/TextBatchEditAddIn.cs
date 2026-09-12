using Eplan.EplApi.ApplicationFramework;
using Eplan.EplApi.Base;
using Eplan.EplApi.Gui;
using System;
using System.Windows.Forms;

namespace EA.EplAddIn.TextBatchEdit;

public class TextBatchEditAddIn : IEplAddIn
{
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
        return true;
    }

    public bool OnInitGui()
    {
        AddInLogger.Info("OnInitGui: registering menus");
        new Eplan.EplApi.Gui.Menu().AddMenuItem(
            "批量修改选中文本（中英文）", "TextBatchEditAction");
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
        return true;
    }
}
