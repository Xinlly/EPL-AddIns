using Eplan.EplApi.Base;
using System;
using System.IO;
using System.Text;

namespace EA.EplAddIn.TextBatchEdit;

/// <summary>
/// 轻量文件日志：DEBUG / INFO / WARN / ERROR 四级，按天一个文件。
/// 目录优先级：
/// 1) 脚本主数据目录下的 .log：$(MD_SCRIPTS)\.log（与 EPL-Scripts 日志位置约定一致，便于统一查看）
/// 2) DLL 所在目录下 logs\
/// 3) %TEMP%\EA.EplAddIn.TextBatchEdit\logs
/// </summary>
public static class AddInLogger
{
    private const int MinLevel = 0; // 0=Debug 1=Info 2=Warn 3=Error
    private static readonly object LockObj = new object();
    private static readonly string LogDirectory = ResolveLogDirectory();

    public static string DirectoryPath => LogDirectory;

    public static void Debug(string message) => Write("DEBUG", message, null);
    public static void Info(string message) => Write("INFO ", message, null);
    public static void Warn(string message) => Write("WARN ", message, null);
    public static void Error(string message, Exception? ex) => Write("ERROR", message, ex);
    public static void Error(string message) => Write("ERROR", message, null);

    private static string ResolveLogDirectory()
    {
        // 1) $(MD_SCRIPTS)\.log —— 脚本主数据目录（用户在此放置 .log 目录/junction）
        try
        {
            var mdScripts = PathMap.SubstitutePath("$(MD_SCRIPTS)");
            if (!string.IsNullOrEmpty(mdScripts))
            {
                var preferred = Path.Combine(mdScripts, ".log");
                if (TryWritable(preferred)) { return preferred; }
            }
        }
        catch { /* PathMap 早期可能不可用，走回退 */ }

        // 2) DLL 所在目录 logs\
        try
        {
            var dllDir = Path.Combine(
                Path.GetDirectoryName(typeof(AddInLogger).Assembly.Location) ?? AppDomain.CurrentDomain.BaseDirectory,
                "logs");
            if (TryWritable(dllDir)) { return dllDir; }
        }
        catch { /* 走回退 */ }

        // 3) 临时目录
        var fallback = Path.Combine(Path.GetTempPath(), "EA.EplAddIn.TextBatchEdit", "logs");
        Directory.CreateDirectory(fallback);
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

            var path = Path.Combine(LogDirectory, "addin-" + DateTime.Now.ToString("yyyy-MM-dd") + ".log");
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
