using System;
using System.IO;
using System.Text;

namespace EA.EplAddIn.TextBatchEdit;

/// <summary>
/// 轻量文件日志：DEBUG / INFO / WARN / ERROR 四级，按天一个文件。
/// 默认目录：DLL 所在目录下 logs\；不可写时降级 %TEMP%\EA.EplAddIn.TextBatchEdit\logs。
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

    private static string ResolveLogDirectory()
    {
        string preferred;
        try
        {
            preferred = Path.Combine(
                Path.GetDirectoryName(typeof(AddInLogger).Assembly.Location) ?? AppDomain.CurrentDomain.BaseDirectory,
                "logs");
            Directory.CreateDirectory(preferred);
            var probe = Path.Combine(preferred, ".write-probe");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return preferred;
        }
        catch
        {
            var fallback = Path.Combine(Path.GetTempPath(), "EA.EplAddIn.TextBatchEdit", "logs");
            Directory.CreateDirectory(fallback);
            return fallback;
        }
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
