using System;
using System.IO;
using System.Linq;
using System.Text;
using Eplan.EplApi.Base;

/// <summary>
/// 轻量文件日志：DEBUG / INFO / WARN / ERROR 四级。
/// 写盘目录优先取工作站设置 STATION.SystemError.LogFilePath + $(EPLAN_VERSION)
/// （…\EA.EplAddIn\&lt;版本&gt;\TextBatchEdit\）；取不到或不可写时回退
/// $(MD_SCRIPTS)\.log → DLL 旁 logs\ → %TEMP%。
/// 按大小滚动：文件名 addin-yyyyMMddHHmm.log（文件创建时刻），单文件达到上限后下次写入另建新文件，
/// 目录内最多保留 MaxFileCount 个，超出按最后写入时间删最旧。
/// </summary>
public static class AddInLogger
{
    private const int MinLevel = 0; // 0=Debug 1=Info 2=Warn 3=Error

    private const long MaxFileSize = 1024L * 1024L; // 单文件 1 MB
    private const int MaxFileCount = 5;             // 最多保留 5 个日志文件
    private const string FilePrefix = "addin-";
    private const string FileExt = ".log";

    private static readonly object LockObj = new object();

    private static readonly string LogDirectory = ResolveLogDirectory();
    private static string DynamicNote = "";      // 动态路径解析过程（写入日志）
    private static string? _currentFile;         // 当前正在写的日志文件（绝对路径）

    /// <summary>当前实际写入的日志目录。</summary>
    public static string DirectoryPath => LogDirectory;

    /// <summary>当前正在写入的日志文件完整路径（首次访问时在目录内选定/新建）。</summary>
    public static string ActiveLogFilePath => EnsureFile(LogDirectory);

    public static void Debug(string message) => Write("DEBUG", message, null);
    public static void Info(string message) => Write("INFO ", message, null);
    public static void Warn(string message) => Write("WARN ", message, null);
    public static void Error(string message, Exception? ex) => Write("ERROR", message, ex);
    public static void Error(string message) => Write("ERROR", message, null);

    private static string ResolveLogDirectory()
    {
        // 1) 首选：工作站设置 + 版本号的动态路径
        var dynamicFile = TryComputeDynamicPath(out var note);
        DynamicNote = note;
        if (dynamicFile != null)
        {
            var preferred = Path.GetDirectoryName(dynamicFile);
            try
            {
                if (!string.IsNullOrEmpty(preferred) && TryWritable(preferred!))
                {
                    var dir = preferred!;
                    EmitStartupDiagnostic(dir, "动态路径（工作站设置 STATION.SystemError.LogFilePath）");
                    return dir;
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

    private static bool TryWritable(string dir)
    {
        Directory.CreateDirectory(dir);
        var probe = Path.Combine(dir, ".writeprobe");
        File.WriteAllText(probe, "1");
        File.Delete(probe);
        return true;
    }

    /// <summary>
    /// 从工作站设置 STATION.SystemError.LogFilePath + $(EPLAN_VERSION) 计算日志文件路径。
    /// 设置路径区分大小写：模块名/设置名为驼峰（SystemError/LogFilePath），全大写会报 S024001。
    /// 读到的值再做一次 PathMap 展开（默认值可能是 $(DEFAULT_LOGFILEPATH) 之类的变量）。
    /// 不抛异常；取不到时返回 null，note 记录原因。
    /// </summary>
    private static string? TryComputeDynamicPath(out string note)
    {
        note = "";
        try
        {
            var settings = new Settings();
            const string settingPath = "STATION.SystemError.LogFilePath";

            string baseDir;
            if (settings.ExistSetting(settingPath))
            {
                var raw = settings.GetStringSetting(settingPath, 0);
                var expanded = SubstituteIfNeeded(raw);
                note += "STATION.SystemError.LogFilePath 原始值=[" + raw + "]，展开后=[" + expanded + "]；";
                if (string.IsNullOrWhiteSpace(expanded))
                {
                    note += "值为空。";
                    return null;
                }
                baseDir = expanded!;
            }
            else
            {
                note += "工作站设置 STATION.SystemError.LogFilePath 不存在。";
                return null;
            }

            string ver;
            try
            {
                ver = PathMap.SubstitutePath("$(EPLAN_VERSION)");
                if (string.IsNullOrWhiteSpace(ver)) { ver = "unknown"; }
            }
            catch (Exception exVer)
            {
                ver = "unknown";
                note += "$(EPLAN_VERSION) 展开失败：" + exVer.Message + "；";
            }
            note += "$(EPLAN_VERSION)=[" + ver + "]。";

            var dir = Path.Combine(baseDir, "EA.EplAddIn", ver, "TextBatchEdit");
            var fileName = "addin-" + DateTime.Now.ToString("yyyyMMddHHmm") + FileExt;
            note += "动态候选目录：" + dir + "。";
            return Path.Combine(dir, fileName);
        }
        catch (Exception ex)
        {
            note += "读取工作站设置失败：" + ex.GetType().Name + ": " + ex.Message + "。";
            return null;
        }
    }

    /// <summary>值若包含 $(...) 变量则用 PathMap 展开，否则原样返回（去首尾空白）。</summary>
    private static string? SubstituteIfNeeded(string? value)
    {
        var v = value?.Trim();
        if (string.IsNullOrEmpty(v)) { return v; }
        try
        {
            return v!.Contains("$(") ? PathMap.SubstitutePath(v) : v;
        }
        catch
        {
            return v;
        }
    }

    // ── 按大小滚动 ─────────────────────────────────────────────────────────

    /// <summary>返回当前应写入的文件：没有就新建；当前文件已达上限也新建并清理旧文件。</summary>
    private static string EnsureFile(string dir)
    {
        lock (LockObj)
        {
            Directory.CreateDirectory(dir);
            if (_currentFile == null) { _currentFile = SelectLatestUnderLimit(dir); }
            if (_currentFile == null || !File.Exists(_currentFile) || new FileInfo(_currentFile).Length >= MaxFileSize)
            {
                _currentFile = CreateNewFile(dir);
                PruneOldFiles(dir);
            }
            return _currentFile;
        }
    }

    /// <summary>选用目录内“新命名规则、未满上限”的最新文件（跨次启动续写），没有则返回 null。</summary>
    private static string? SelectLatestUnderLimit(string dir)
    {
        return Directory.GetFiles(dir, FilePrefix + "*" + FileExt)
            .Where(IsNewPattern)
            .Select(p => new FileInfo(p))
            .Where(f => f.Length < MaxFileSize)
            .OrderByDescending(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault()?.FullName;
    }

    /// <summary>以当前时刻创建新日志文件（同一分钟内重复滚动时追加 _2/_3 防覆盖）。</summary>
    private static string CreateNewFile(string dir)
    {
        var stamp = DateTime.Now.ToString("yyyyMMddHHmm");
        var path = Path.Combine(dir, FilePrefix + stamp + FileExt);
        var seq = 2;
        while (File.Exists(path))
        {
            path = Path.Combine(dir, FilePrefix + stamp + "_" + seq + FileExt);
            seq++;
        }
        using (File.Create(path)) { } // 立刻建文件，LastWriteTime 即创建时间
        return path;
    }

    /// <summary>新命名规则：addin-yyyyMMddHHmm[.log]（允许 _2 等同分钟后缀）。</summary>
    private static bool IsNewPattern(string path)
    {
        var name = Path.GetFileName(path);
        if (!name.StartsWith(FilePrefix, StringComparison.Ordinal) ||
            !name.EndsWith(FileExt, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        var core = name.Substring(FilePrefix.Length, name.Length - FilePrefix.Length - FileExt.Length);
        return core.Length >= 12 && long.TryParse(core.Substring(0, 12), out _);
    }

    /// <summary>目录内 addin-*.log 只保留最后写入最新的 MaxFileCount 个，其余删除（含旧版按天命名文件）。</summary>
    private static void PruneOldFiles(string dir)
    {
        try
        {
            var old = Directory.GetFiles(dir, FilePrefix + "*" + FileExt)
                .Select(p => new FileInfo(p))
                .OrderByDescending(f => f.LastWriteTime)
                .Skip(MaxFileCount);
            foreach (var f in old)
            {
                try { f.Delete(); } catch { /* 删不掉就留着，不影响写日志 */ }
            }
        }
        catch { /* 清理失败不影响插件 */ }
    }

    /// <summary>把“实际写盘路径 + 动态解析过程 + 滚动策略”作为首条诊断写进当前日志文件。</summary>
    private static void EmitStartupDiagnostic(string activeDir, string source)
    {
        try
        {
            var path = EnsureFile(activeDir);
            var line = "========== 日志路径诊断 ==========" + Environment.NewLine
                     + "当前日志文件：" + path + Environment.NewLine
                     + "路径来源：" + source + Environment.NewLine
                     + "滚动策略：单文件达到 " + (MaxFileSize / 1024) + " KB 后另建新文件，最多保留 " + MaxFileCount + " 个。" + Environment.NewLine
                     + "动态路径解析过程：" + DynamicNote + Environment.NewLine
                     + "==================================";
            File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
        }
        catch
        {
            // 诊断失败不影响插件
        }
    }

    private static void Write(string level, string message, Exception? ex)
    {
        var sev = level switch
        {
            "DEBUG" => 0,
            "INFO " => 1,
            "WARN " => 2,
            "ERROR" => 3,
            _ => 1,
        };
        if (sev < MinLevel) { return; }

        var line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")
                 + " [" + level + "] "
                 + "[t" + System.Threading.Thread.CurrentThread.ManagedThreadId + "] "
                 + message
                 + (ex != null ? (" -> " + ex.GetType().Name + ": " + ex.Message + Environment.NewLine + ex.StackTrace) : "");
        try
        {
            lock (LockObj)
            {
                var path = EnsureFile(LogDirectory);
                File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch
        {
            // 日志失败不影响插件
        }
    }
}
