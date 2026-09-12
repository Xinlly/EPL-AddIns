using System;
using System.IO;
using System.Text;

namespace EA.EplAddIn.Test;

/// <summary>
/// 轻量文件日志：DEBUG / INFO / WARN / ERROR 四级，按天一个文件。
/// 默认目录：DLL 所在目录下 logs\；不可写时降级 %TEMP%\EA.EplAddIn.Test\logs。
/// 线程安全（lock + 每次打开文件流，EPLAN 插件场景开销可忽略）。
/// </summary>
public static class AddInLogger
{
    public enum Level
    {
        Debug = 0,
        Info = 1,
        Warn = 2,
        Error = 3,
    }

    // 出问题时先临时调成 Debug 看全量；正式分发用 Info
    public static Level MinLevel = Level.Debug;

    private static readonly object LockObj = new object();
    private static readonly string LogDirectory = ResolveLogDirectory();

    private static string ResolveLogDirectory()
    {
        string preferred;
        try
        {
            preferred = Path.Combine(
                Path.GetDirectoryName(typeof(AddInLogger).Assembly.Location) ?? AppDomain.CurrentDomain.BaseDirectory,
                "logs");
            Directory.CreateDirectory(preferred);
            // 写探测：确认目录确实可写
            var probe = Path.Combine(preferred, ".write-probe");
            File.WriteAllText(probe, "1");
            File.Delete(probe);
            return preferred;
        }
        catch
        {
            var fallback = Path.Combine(Path.GetTempPath(), "EA.EplAddIn.Test", "logs");
            Directory.CreateDirectory(fallback);
            return fallback;
        }
    }

    public static void Debug(string message) => Write(Level.Debug, message, null);
    public static void Info(string message) => Write(Level.Info, message, null);
    public static void Warn(string message) => Write(Level.Warn, message, null);
    public static void Error(string message, Exception? ex = null) => Write(Level.Error, message, ex);

    private static void Write(Level level, string message, Exception? ex)
    {
        if (level < MinLevel)
        {
            return;
        }

        try
        {
            var sb = new StringBuilder();
            sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
            sb.Append(" [").Append(level.ToString().ToUpperInvariant()).Append(']');
            sb.Append(" [t").Append(Environment.CurrentManagedThreadId).Append(']');
            sb.Append(' ').Append(message);
            if (ex != null)
            {
                sb.Append(" | EXCEPTION: ").Append(ex.GetType().FullName)
                  .Append(": ").Append(ex.Message)
                  .Append(Environment.NewLine).Append(ex.StackTrace);
            }

            var file = Path.Combine(LogDirectory, "addin-" + DateTime.Now.ToString("yyyy-MM-dd") + ".log");
            lock (LockObj)
            {
                File.AppendAllText(file, sb + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch
        {
            // 日志失败不能影响插件本身
        }
    }

    /// <summary>日志目录（供界面提示用户去哪个目录取日志）。</summary>
    public static string DirectoryPath => LogDirectory;
}
