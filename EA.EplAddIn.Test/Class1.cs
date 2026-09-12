using Eplan.EplApi.ApplicationFramework;
using Eplan.EplApi.Base;
using Eplan.EplApi.Gui;
using System;
using System.Windows.Forms;

namespace EA.EplAddIn.Test;

public class Class1 : IEplAddIn
{
    public bool OnInit()
    {
        AddInLogger.Info("OnInit: " + typeof(Class1).Assembly.FullName);
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
        new Eplan.EplApi.Gui.Menu().AddMenuItem("Hello World", "HelloWorldAction");
        new Eplan.EplApi.Gui.Menu().AddMenuItem(
            "表格式编辑（演示）", "TableEditorAction");
        return true;
    }

    public bool OnRegister(ref bool bLoadOnStart)
    {
        bLoadOnStart = true;
        AddInLogger.Info("OnRegister: bLoadOnStart=true, assembly = "
            + typeof(Class1).Assembly.Location);
        new Decider().Decide(
            EnumDecisionType.eOkDecision,
            "MyFirst Add-in",
            "插件已加载！",
            EnumDecisionReturn.eOK,
            EnumDecisionReturn.eOK);
        return true;
    }

    public bool OnUnregister()
    {
        return true;
    }
}
