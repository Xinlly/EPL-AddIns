using Eplan.EplApi.ApplicationFramework;
using Eplan.EplApi.Base;
using Eplan.EplApi.Scripting;

namespace EA.EplAddIn.Test;

public class HelloWorldAction : IEplAction
{
    [DeclareAction("HelloWorldAction")]
    public bool Execute(ActionCallingContext oActionCallingContext)
    {
        new Decider().Decide(
            EnumDecisionType.eOkDecision,
            "Hello World",
            "Hello World!",
            EnumDecisionReturn.eOK,
            EnumDecisionReturn.eOK);
        return true;
    }

    public void GetActionProperties(ref ActionProperties actionProperties)
    {
    }

    public bool OnRegister(ref string Name, ref int Ordinal)
    {
        return true;
    }
}
