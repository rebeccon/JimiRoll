namespace JimiRoll;

/// <summary>日志级别，按严重程度递增。</summary>
internal enum LogLevel
{
    /// <summary>详细的诊断信息（默认不输出）。</summary>
    Debug = 0,
    /// <summary>常规运行信息。</summary>
    Info = 1,
    /// <summary>可恢复的问题，值得注意。</summary>
    Warn = 2,
    /// <summary>出错（功能可能不可用）。</summary>
    Error = 3,
    /// <summary>完全关闭日志。</summary>
    Off = 4,
}

/// <summary>
/// 分级日志 + 文件轮转。
///
/// 为什么需要它（都是实测出来的问题）：
/// <list type="bullet">
/// <item><b>原来的日志会无限增长</b> —— 每写一行就 <c>AppendAllText</c> 一次，
///       11 小时涨到 87 KB，玩几个月就是几 MB，从不清理。
///       现在超过 <see cref="MaxBytes"/> 就轮转到 <c>.1</c>，只保留两份。</item>
/// <item><b>原来噪音很大</b> —— 每次启动都全量打印储备池、冒烟测试都打完整分布。
///       这些移到 <see cref="LogLevel.Debug"/>，默认的 Info 级别只留摘要。</item>
/// </list>
///
/// 每行都带级别标签（<c>[INF]</c> / <c>[WRN]</c> / <c>[ERR]</c> / <c>[DBG]</c>），
/// 方便直接筛选。
/// </summary>
internal static class Logger
{
    /// <summary>当前级别（由 settings.json 的 LogLevel 决定）。</summary>
    public static LogLevel Level { get; set; } = LogLevel.Info;

    /// <summary>日志是否用英文（由 settings.json 的 LogLanguage 决定）。</summary>
    public static bool English { get; set; }

    /// <summary>单个日志文件的上限；超过就轮转。</summary>
    const long MaxBytes = 2 * 1024 * 1024;

    public static void Debug(string message) => Write(LogLevel.Debug, message);
    public static void Info(string message) => Write(LogLevel.Info, message);
    public static void Warn(string message) => Write(LogLevel.Warn, message);
    public static void Error(string message) => Write(LogLevel.Error, message);

    /// <summary>把字符串解析成级别（容忍大小写与中文别名）。</summary>
    public static LogLevel ParseLevel(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return LogLevel.Info;
        return s.Trim().ToLowerInvariant() switch
        {
            "debug" or "verbose" or "详细" => LogLevel.Debug,
            "info" or "information" or "信息" => LogLevel.Info,
            "warn" or "warning" or "警告" => LogLevel.Warn,
            "error" or "错误" => LogLevel.Error,
            "off" or "none" or "关闭" => LogLevel.Off,
            _ => LogLevel.Info,
        };
    }

    static void Write(LogLevel level, string message)
    {
        if (level < Level) return;
        if (Level == LogLevel.Off) return;

        var tag = level switch
        {
            LogLevel.Debug => "DBG",
            LogLevel.Warn => "WRN",
            LogLevel.Error => "ERR",
            _ => "INF",
        };
        var line = $"[{DateTime.Now:HH:mm:ss}] [{tag}] {message}";

        try { Console.WriteLine("[JimiRoll] " + message); } catch { }

        try
        {
            SeedStore.EnsureDir();
            RotateIfNeeded();
            File.AppendAllText(SeedStore.LogPath, line + Environment.NewLine);
        }
        catch { /* 日志失败不该影响任何功能 */ }
    }

    /// <summary>文件超过上限就轮转：当前的改名成 .1，重新开始。</summary>
    static void RotateIfNeeded()
    {
        try
        {
            var path = SeedStore.LogPath;
            if (!File.Exists(path)) return;

            var fi = new FileInfo(path);
            if (fi.Length < MaxBytes) return;

            var old = path + ".1";
            if (File.Exists(old)) File.Delete(old);
            File.Move(path, old);
        }
        catch { /* 轮转失败就继续追加，不是致命问题 */ }
    }
}
