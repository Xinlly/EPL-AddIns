using Eplan.EplApi.ApplicationFramework;
using Eplan.EplApi.Scripting;
using System;
using System.Windows.Forms;

namespace EA.EplAddIn.Test;

public class TableEditorAction : IEplAction
{
    [DeclareAction("TableEditorAction")]
    public bool Execute(ActionCallingContext oActionCallingContext)
    {
        AddInLogger.Info("TableEditorAction.Execute: opening form");
        try
        {
            using var form = new TableEditorForm();
            var result = form.ShowDialog();
            AddInLogger.Info("TableEditorAction.Execute: form closed, DialogResult=" + result);
        }
        catch (Exception ex)
        {
            AddInLogger.Error("TableEditorAction.Execute failed", ex);
            throw;
        }
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
