using Eplan.EplApi.Base;
using System;
using System.IO;
using System.Text;

namespace EA.EplAddIn.TextBatchEdit;

/// <summary>
/// 轻量文件日志：DEBUG / INFO / WARN / ERROR 四级，按天一个文件。
/// 写盘目录优先取工作站设置 STATION.SystemError.LogFilePath + $(EPLAN_VERSION)
/// （…\EA.EplAddIn\&lt;版本&gt;\TextBatchEdit\）；取不到或不可写时回退
/// $(MD_SCRIPTS)\.log → DLL 旁 logs\ → %TEMP%。解析过程写入本日志文件开头。
/// </summary>
public static class AddInLogger
{
    private const int MinLevel = 0; // 0=Debug 1=Info 2=Warn 3=Error
    private static readonly object LockObj = new object();

    private static readonly string LogDirectory = ResolveLogDirectory();
    private static string DynamicNote = "";     // 动态路径解析过程（写入日志）
    private static string? DynamicFilePath;    // 动态路径计算结果

    /// <summary>当前实际写入的日志目录。</summary>
    public static string DirectoryPath => LogDirectory;

    /// <summary>当前实际写入的日志文件完整路径（按天文件名）。</summary>
    public static string ActiveLogFilePath =>
        Path.Combine(LogDirectory, "addin-" + DateTime.Now.ToString("yyyy-MM-dd") + ".log");

    /// <summary>按工作站设置动态计算出的日志文件路径（与首选写盘路径一致）；不可用时为 null。</summary>
    public static string? PreviewLogFilePath => DynamicFilePath;

    public static void Debug(string message) => Write("DEBUG", message, null);
    public static void Info(string message) => Write("INFO ", message, null);
    public static void Warn(string message) => Write("WARN ", message, null);
    public static void Error(string message, Exception? ex) => Write("ERROR", message, ex);
    public static void Error(string message) => Write("ERROR", message, null);

    private static string ResolveLogDirectory()
    {
        // 1) 首选：工作站设置 + 版本号的动态路径
        DynamicFilePath = TryComputeDynamicPath(out var note);
        DynamicNote = note;
        if (DynamicFilePath != null)
        {
            var preferred = Path.GetDirectoryName(DynamicFilePath);
            try
            {
                if (!string.IsNullOrEmpty(preferred) && TryWritable(preferred!))
                {
                    EmitStartupDiagnostic(preferred!, "动态路径（工作站设置 STATION.SystemError.LogFilePath）");
                    return preferred!;
                }
                DynamicNote += " 动态目录不可写，进入回退。";
            }
            catch (Exception ex)
            {
                DynamicNote += " 动态目录异常：" + ex.Message + "，进入回退。";
            }
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
                    EmitStartupDiagnostic(alt, "回退路径（$(MD_SCRIPTS)\\.log）");
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
                EmitStartupDiagnostic(dllDir, "回退路径（DLL 旁 logs\\）");
                return dllDir;
            }
        }
        catch { /* 走回退 */ }

        // 4) 临时目录
        var fallback = Path.Combine(Path.GetTempPath(), "EA.EplAddIn.TextBatchEdit", "logs");
        Directory.CreateDirectory(fallback);
        EmitStartupDiagnostic(fallback, "回退路径（%TEMP%）");
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

    /// <summary>把“实际写盘路径 + 动态计算结果 + 解析过程”作为首条诊断写进日志文件。</summary>
    private static void EmitStartupDiagnostic(string activeDir, string source)
    {
        try
        {
            var line = "========== 日志路径诊断 ==========" + Environment.NewLine
                     + "实际写盘目录：" + activeDir + Environment.NewLine
                     + "路径来源：" + source + Environment.NewLine
                     + "动态计算文件：" + (DynamicFilePath ?? "(不可用)") + Environment.NewLine
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
