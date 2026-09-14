using Eplan.EplApi.Base;
using System;
using System.IO;
using System.Text;

namespace EA.EplAddIn.TextBatchEdit;

/// <summary>
/// 轻量文件日志：DEBUG / INFO / WARN / ERROR 四级，按天一个文件。
/// 当前写盘目录写死为系统消息路径下的绝对目录（临时方案，便于在 2.9 平台稳定落盘）；
/// 从工作站设置 STATION.SYSTEMERROR.LOGFILEPATH + $(EPLAN_VERSION) 动态计算的路径仅在“说明”页预览，
/// 待动态路径实测稳定后再切换为正式写盘路径。动态路径的解析过程写入本日志文件。
/// </summary>
public static class AddInLogger
{
    private const int MinLevel = 0; // 0=Debug 1=Info 2=Warn 3=Error
    private static readonly object LockObj = new object();

    // —— 临时写死的绝对目录（系统消息路径 C:\Users\Public\EPLAN\Electric P8 + EA.EplAddIn\2.9.4\TextBatchEdit）——
    private const string FixedBaseDir = @"C:\Users\Public\EPLAN\Electric P8";
    private const string FixedVersion = "2.9.4";

    private static readonly string LogDirectory = ResolveLogDirectory();
    private static string DynamicNote = "";     // 动态路径解析过程（写入日志）
    private static string? DynamicFilePath;    // 动态路径预览（仅说明页展示，不写盘）

    /// <summary>当前实际写入的日志目录。</summary>
    public static string DirectoryPath => LogDirectory;

    /// <summary>当前实际写入的日志文件完整路径（按天文件名）。</summary>
    public static string ActiveLogFilePath =>
        Path.Combine(LogDirectory, "addin-" + DateTime.Now.ToString("yyyy-MM-dd") + ".log");

    /// <summary>动态计算得到的候选日志文件路径（暂不启用，仅说明页预览）；不可用时为 null。</summary>
    public static string? PreviewLogFilePath => DynamicFilePath;

    public static void Debug(string message) => Write("DEBUG", message, null);
    public static void Info(string message) => Write("INFO ", message, null);
    public static void Warn(string message) => Write("WARN ", message, null);
    public static void Error(string message, Exception? ex) => Write("ERROR", message, ex);
    public static void Error(string message) => Write("ERROR", message, null);

    private static string ResolveLogDirectory()
    {
        // 先算动态候选（仅预览 + 诊断），不参与写盘决策
        DynamicFilePath = TryComputeDynamicPath(out var note);
        DynamicNote = note;

        // 1) 写死的绝对目录
        var preferred = Path.Combine(FixedBaseDir, "EA.EplAddIn", FixedVersion, "TextBatchEdit");
        try
        {
            if (TryWritable(preferred))
            {
                EmitStartupDiagnostic(preferred);
                return preferred;
            }
        }
        catch (Exception ex)
        {
            DynamicNote += " 写死目录不可用：" + ex.Message + "。";
        }

        // 2) 回退 $(MD_SCRIPTS)\.log
        try
        {
            var mdScripts = PathMap.SubstitutePath("$(MD_SCRIPTS)");
            if (!string.IsNullOrEmpty(mdScripts))
            {
                var alt = Path.Combine(mdScripts, ".log");
                if (TryWritable(alt))
                {
                    EmitStartupDiagnostic(alt);
                    return alt;
                }
            }
        }
        catch { /* 走回退 */ }

        // 3) DLL 所在目录 logs\
        try
        {
            var dllDir = Path.Combine(
                Path.GetDirectoryName(typeof(AddInLogger).Assembly.Location) ?? AppDomain.CurrentDomain.BaseDirectory,
                "logs");
            if (TryWritable(dllDir))
            {
                EmitStartupDiagnostic(dllDir);
                return dllDir;
            }
        }
        catch { /* 走回退 */ }

        // 4) 临时目录
        var fallback = Path.Combine(Path.GetTempPath(), "EA.EplAddIn.TextBatchEdit", "logs");
        Directory.CreateDirectory(fallback);
        EmitStartupDiagnostic(fallback);
        return fallback;
    }

    /// <summary>
    /// 从工作站设置 STATION.SystemError.LogFilePath + $(EPLAN_VERSION) 计算候选日志文件路径（仅预览）。
    /// 注意：设置路径区分大小写，模块名/设置名为驼峰（SystemError/LogFilePath），全大写会报 S024001。
    /// 不抛异常；取不到时 filePath 返回 null，note 记录原因。
    /// </summary>
    private static string? TryComputeDynamicPath(out string note)
    {
        var sb = new StringBuilder();
        try
        {
            string baseDirRaw;
            using (var settings = new Settings())
            {
                baseDirRaw = settings.GetStringSetting("STATION.SystemError.LogFilePath", 0);
            }
            // 默认设置值可能是 PathMap 变量 $(DEFAULT_LOGFILEPATH)，需再展开一次
            var baseDir = PathMap.SubstitutePath(baseDirRaw ?? string.Empty);
            var version = PathMap.SubstitutePath("$(EPLAN_VERSION)"); // 如 2.9.4
            sb.Append("STATION.SystemError.LogFilePath 原始值=[").Append(baseDirRaw)
              .Append("]，展开后=[").Append(baseDir)
              .Append("]；$(EPLAN_VERSION)=[").Append(version).Append("]。");
            if (!string.IsNullOrWhiteSpace(baseDir) && !string.IsNullOrWhiteSpace(version))
            {
                var dir = Path.Combine(baseDir!, "EA.EplAddIn", version!, "TextBatchEdit");
                sb.Append("动态候选目录：").Append(dir).Append("。");
                note = sb.ToString();
                return Path.Combine(dir, "addin-" + DateTime.Now.ToString("yyyy-MM-dd") + ".log");
            }
            sb.Append("设置值或版本为空。");
        }
        catch (Exception ex)
        {
            sb.Append("读取工作站设置失败：").Append(ex.GetType().Name).Append(": ").Append(ex.Message).Append("。");
        }
        note = sb.ToString();
        return null;
    }

    /// <summary>把“实际写盘路径 + 动态候选 + 解析过程”作为首条诊断写进日志文件。</summary>
    private static void EmitStartupDiagnostic(string activeDir)
    {
        try
        {
            var line = "========== 日志路径诊断 ==========" + Environment.NewLine
                     + "实际写盘目录：" + activeDir + Environment.NewLine
                     + "动态候选文件（暂未启用）：" + (DynamicFilePath ?? "(不可用)") + Environment.NewLine
                     + "动态路径解析过程：" + DynamicNote + Environment.NewLine
                     + "==================================";
            var path = Path.Combine(activeDir, "addin-" + DateTime.Now.ToString("yyyy-MM-dd") + ".log");
            lock (LockObj)
            {
                File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch
        {
            // 诊断失败不影响插件
        }
    }

    private static bool TryWritable(string dir)
    {
        Directory.CreateDirectory(dir);
        var probe = Path.Combine(dir, ".write-probe");
        File.WriteAllText(probe, "");
        File.Delete(probe);
        return true;
    }

    private static void Write(string level, string message, Exception? ex)
    {
        var levelValue = level[0] == 'D' ? 0 : level[0] == 'I' ? 1 : level[0] == 'W' ? 2 : 3;
        if (levelValue < MinLevel)
        {
            return;
        }

        try
        {
            var sb = new StringBuilder();
            sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
              .Append(" [").Append(level).Append(']')
              .Append(" [t").Append(System.Threading.Thread.CurrentThread.ManagedThreadId).Append("] ")
              .Append(message);
            if (ex != null)
            {
                sb.AppendLine().Append(" -> ").Append(ex.GetType().FullName).Append(": ").Append(ex.Message)
                  .AppendLine().Append(ex.StackTrace);
                if (ex.InnerException != null)
                {
                    sb.AppendLine().Append(" -> Inner: ").Append(ex.InnerException.GetType().FullName)
                      .Append(": ").Append(ex.InnerException.Message);
                }
            }

            var path = ActiveLogFilePath;
            lock (LockObj)
            {
                File.AppendAllText(path, sb + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch
        {
            // 日志失败不得影响插件运行
        }
    }
}
