using Eplan.EplApi.ApplicationFramework;
using Eplan.EplApi.Base;
using Eplan.EplApi.Gui;

namespace EA.EplAddIn.Test;

public class Class1 : IEplAddIn
{
    public bool OnInit()
    {
        return true;
    }

    public bool OnExit()
    {
        return true;
    }

    public bool OnInitGui()
    {
        new Menu().AddMenuItem("Hello World", "HelloWorldAction");
        return true;
    }

    public bool OnRegister(ref bool bLoadOnStart)
    {
        bLoadOnStart = true;
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
