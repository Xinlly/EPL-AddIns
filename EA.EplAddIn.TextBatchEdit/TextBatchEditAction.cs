using Eplan.EplApi.ApplicationFramework;
using Eplan.EplApi.DataModel;
using Eplan.EplApi.DataModel.Graphics;
using Eplan.EplApi.HEServices;
using Eplan.EplApi.Scripting;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;

namespace EA.EplAddIn.TextBatchEdit;

public class TextBatchEditAction : IEplAction
{
    [DeclareAction("TextBatchEditAction")]
    public bool Execute(ActionCallingContext oActionCallingContext)
    {
        AddInLogger.Info("TextBatchEditAction.Execute: begin");
        try
        {
            StorableObject[] selected;
            try
            {
                selected = new SelectionSet().Selection ?? Array.Empty<StorableObject>();
            }
            catch (Exception ex)
            {
                AddInLogger.Error("读取选择集失败（是否未打开项目/页面？）", ex);
                MessageBox.Show("无法读取当前选择集，请先打开项目并在页面中选中文本。",
                    "批量修改选中文本", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return true;
            }

            var texts = selected.OfType<TextBase>().ToList();
            AddInLogger.Info("选择集对象数=" + selected.Length + "，其中文本对象数=" + texts.Count);

            if (texts.Count == 0)
            {
                MessageBox.Show("当前选择集中没有文本对象。\n请在图形编辑器中框选一个或多个文本后再执行。",
                    "批量修改选中文本", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return true;
            }

            using (var form = new TextBatchEditForm(texts))
            {
                form.ShowDialog();
            }
            return true;
        }
        catch (Exception ex)
        {
            AddInLogger.Error("TextBatchEditAction.Execute 异常", ex);
            MessageBox.Show("执行异常：" + ex.Message, "批量修改选中文本",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return true;
        }
    }

    public void GetActionProperties(ref ActionProperties actionProperties)
    {
    }

    public bool OnRegister(ref string Name, ref int Ordinal)
    {
        return true;
    }
}
