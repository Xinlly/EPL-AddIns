using Eplan.EplApi.ApplicationFramework;
using Eplan.EplApi.Base;
using System;

namespace EA.EplAddIn.ControlBridge;

/// <summary>
/// ControlBridge 插件入口（I0 脚手架：最小可加载骨架，零网络/零端点/无菜单）。
/// 五方法签名与 EA.EplAddIn.TextBatchEdit.TextBatchEditAddIn 完全一致；
/// 每个生命周期点只写一行日志，任何异常仅记录、绝不抛回 EPLAN（OnInitGui 尤其不能抛）。
/// I1 起在 OnInitGui/OnExit 中管理 loopback HTTP 通道。
/// </summary>
public class ControlBridgeAddIn : IEplAddIn
{
    /// <summary>获取 $(EPLAN_VERSION)（如 2.9.20240xxx）；任何失败回退 "unknown"，绝不抛。</summary>
    private static string TryGetEplanVersion()
    {
        try
        {
            var ver = PathMap.SubstitutePath("$(EPLAN_VERSION)");
            return string.IsNullOrWhiteSpace(ver) ? "unknown" : ver;
        }
        catch (Exception ex)
        {
            AddInLogger.Warn("读取 $(EPLAN_VERSION) 失败：" + ex.GetType().Name + " " + ex.Message);
            return "unknown";
        }
    }

    public bool OnInit()
    {
        try
        {
            var asm = typeof(ControlBridgeAddIn).Assembly;
            AddInLogger.Info("OnInit: EPLAN_VERSION=" + TryGetEplanVersion()
                + " addin=" + asm.FullName);
            AddInLogger.Info("OnInit: log dir = " + AddInLogger.DirectoryPath);
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
                AddInLogger.Error("AppDomain.UnhandledException (isTerminating=" + e.IsTerminating + ")",
                    e.ExceptionObject as Exception);
        }
        catch (Exception ex)
        {
            AddInLogger.Error("OnInit 异常（已吞掉，不向 EPLAN 抛出）", ex);
        }
        return true;
    }

    public bool OnExit()
    {
        try
        {
            AddInLogger.Info("OnExit: EPLAN shutting down");
        }
        catch (Exception ex)
        {
            AddInLogger.Error("OnExit 异常（已吞掉，不向 EPLAN 抛出）", ex);
        }
        return true;
    }

    public bool OnInitGui()
    {
        // I0：不注册任何菜单/Action，不起监听。仅记录生命周期，证明插件已被 EPLAN 加载。
        try
        {
            AddInLogger.Info("OnInitGui: GUI ready (I0 scaffold, no menu, no endpoint)");
        }
        catch (Exception ex)
        {
            AddInLogger.Error("OnInitGui 异常（已吞掉，不向 EPLAN 抛出）", ex);
        }
        return true;
    }

    public bool OnRegister(ref bool bLoadOnStart)
    {
        try
        {
            bLoadOnStart = true;
            AddInLogger.Info("OnRegister: bLoadOnStart=true, assembly = "
                + typeof(ControlBridgeAddIn).Assembly.Location);
        }
        catch (Exception ex)
        {
            AddInLogger.Error("OnRegister 异常（已吞掉，不向 EPLAN 抛出）", ex);
        }
        return true;
    }

    public bool OnUnregister()
    {
        try
        {
            AddInLogger.Info("OnUnregister");
        }
        catch (Exception ex)
        {
            AddInLogger.Error("OnUnregister 异常（已吞掉，不向 EPLAN 抛出）", ex);
        }
        return true;
    }
}
