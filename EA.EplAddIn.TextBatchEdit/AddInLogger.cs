using Eplan.EplApi.Base;
using System;
using System.IO;
using System.Text;

namespace EA.EplAddIn.TextBatchEdit;

/// <summary>
/// 轻量文件日志：DEBUG / INFO / WARN / ERROR 四级，按天一个文件。
/// 目录优先级：
/// 1) 系统消息文件路径（工作站设置 STATION.SYSTEMERROR.LOGFILEPATH）下：
///    &lt;系统消息路径&gt;\EA.EplAddIn\$(EPLAN_VERSION)\TextBatchEdit\
/// 2) $(MD_SCRIPTS)\.log（与 EPL-Scripts 日志位置约定一致）
/// 3) DLL 所在目录下 logs\
/// 4) %TEMP%\EA.EplAddIn.TextBatchEdit\logs
/// </summary>
public static class AddInLogger
{
    private const int MinLevel = 0; // 0=Debug 1=Info 2=Warn 3=Error
    private static readonly object LockObj = new object();
    private static readonly string LogDirectory = ResolveLogDirectory();

    /// <summary>目录解析过程说明（含 STATION 设置原始值/版本/失败原因），供“说明”页诊断展示。</summary>
    public static string ResolutionNote { get; private set; } = "";

    public static string DirectoryPath => LogDirectory;

    /// <summary>当前实际写入的日志文件完整路径（按天文件名）。</summary>
    public static string ActiveLogFilePath =>
        Path.Combine(LogDirectory, "addin-" + DateTime.Now.ToString("yyyy-MM-dd") + ".log");

    public static void Debug(string message) => Write("DEBUG", message, null);
    public static void Info(string message) => Write("INFO ", message, null);
    public static void Warn(string message) => Write("WARN ", message, null);
    public static void Error(string message, Exception? ex) => Write("ERROR", message, ex);
    public static void Error(string message) => Write("ERROR", message, null);

    private static string ResolveLogDirectory()
    {
        var note = new StringBuilder();

        // 1) 工作站设置的“系统消息文件路径” \ EA.EplAddIn \ 版本 \ TextBatchEdit
        try
        {
            // 不依赖 ExistSetting（实测可能对该工作站项误判），直接读取，读不到会抛 BaseException
            string baseDir;
            using (var settings = new Settings())
            {
                baseDir = settings.GetStringSetting("STATION.SYSTEMERROR.LOGFILEPATH", 0);
            }
            var version = PathMap.SubstitutePath("$(EPLAN_VERSION)"); // 如 2.9.4
            note.Append("STATION.SYSTEMERROR.LOGFILEPATH=[").Append(baseDir).Append("]；$(EPLAN_VERSION)=[").Append(version).Append("]。");
            if (!string.IsNullOrWhiteSpace(baseDir) && !string.IsNullOrWhiteSpace(version))
            {
                var preferred = Path.Combine(baseDir!, "EA.EplAddIn", version!, "TextBatchEdit");
                if (TryWritable(preferred))
                {
                    note.Append("命中并可写：").Append(preferred);
                    ResolutionNote = note.ToString();
                    return preferred;
                }
                note.Append("目标不可写：").Append(preferred).Append("，回退。");
            }
            else
            {
                note.Append("设置值或版本为空，回退。");
            }
        }
        catch (Exception ex)
        {
            note.Append("读取工作站设置失败：").Append(ex.GetType().Name).Append(": ").Append(ex.Message).Append("，回退。");
        }

        // 2) $(MD_SCRIPTS)\.log —— 脚本主数据目录（用户在此放置 .log 目录/junction）
        try
        {
            var mdScripts = PathMap.SubstitutePath("$(MD_SCRIPTS)");
            if (!string.IsNullOrEmpty(mdScripts))
            {
                var preferred = Path.Combine(mdScripts, ".log");
                if (TryWritable(preferred))
                {
                    note.Append(" 回退命中 $(MD_SCRIPTS)\\.log：").Append(preferred);
                    ResolutionNote = note.ToString();
                    return preferred;
                }
                note.Append(" $(MD_SCRIPTS)\\.log 不可写：").Append(preferred).Append("。");
            }
        }
        catch (Exception ex) { note.Append(" $(MD_SCRIPTS) 不可用：").Append(ex.Message).Append("。"); }

        // 3) DLL 所在目录 logs\
        try
        {
            var dllDir = Path.Combine(
                Path.GetDirectoryName(typeof(AddInLogger).Assembly.Location) ?? AppDomain.CurrentDomain.BaseDirectory,
                "logs");
            if (TryWritable(dllDir))
            {
                note.Append(" 回退命中 DLL 旁 logs：").Append(dllDir);
                ResolutionNote = note.ToString();
                return dllDir;
            }
        }
        catch (Exception ex) { note.Append(" DLL 旁 logs 不可用：").Append(ex.Message).Append("。"); }

        // 4) 临时目录
        var fallback = Path.Combine(Path.GetTempPath(), "EA.EplAddIn.TextBatchEdit", "logs");
        Directory.CreateDirectory(fallback);
        note.Append(" 最终回退临时目录：").Append(fallback);
        ResolutionNote = note.ToString();
        return fallback;
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
