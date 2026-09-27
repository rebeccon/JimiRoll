using System.Text;
using System.Text.Json;

namespace JimiRoll;

/// <summary>
/// 可外部化的设置项（就是 <c>settings.json</c> 的内容）。
///
/// 只放【可调参数】。筛种条件本身（哪些遗物、哪个诅咒）**故意留在代码里** ——
/// 那是最不容易改也最不该改错的部分，写死反而更安全。
/// </summary>
internal sealed class SettingsData
{
    /// <summary>储备池容量 —— 存放的种子数量恒定维持在这个值。</summary>
    public int ReserveCapacity { get; set; } = 10;

    /// <summary>
    /// ★ 核心指标：第一章地图里【怪物房间占全部格子的比重】的上限（百分数，0~100）。
    /// 越低越好，越小越难找。
    ///
    /// 实测 CDF（2000 个种子的 Act1 地图，占比范围 24%~58%，平均约 43%）：
    /// <code>
    ///   ≤24% 0.05%   ≤26% 0.05%   ≤28% 0.25%   ≤30% 0.60%
    ///   ≤32% 1.65%   ≤33% 2.10%   ≤34% 3.20%   ≤35% 4.10%
    ///   ≤38% 10.5%   ≤40% 21.5%   ≤45% 63.4%
    /// </code>
    /// 单张地图生成要 ~55ms（比骨骰判定贵约 3 万倍），所以这一级是真正的瓶颈。
    /// 默认 30% 是"质量"与"凑齐一个储备池要多久"的折中：
    /// 8 线程下约 0.8 个/秒 → 10 个储备约 12 秒；改成 35% 会快 7 倍。
    /// </summary>
    public int MaxMonsterPercent { get; set; } = 30;

    /// <summary>角色（形如 CHARACTER:SILENT）。</summary>
    public string CharacterKey { get; set; } = "CHARACTER:SILENT";

    /// <summary>进阶等级。</summary>
    public int Ascension { get; set; } = 10;

    /// <summary>单局人数。</summary>
    public int PlayersCount { get; set; } = 1;

    /// <summary>单次搜索的时间上限（分钟）—— 防内存持续上涨。</summary>
    public int MaxRunMinutes { get; set; } = 3;

    /// <summary>
    /// 搜索线程数。<b>默认 1（单线程），别乱调。</b>
    ///
    /// ⚠️ 2026-09-27 实测：把线程数提到 8 之后，游戏在一次搜索里**无声无息地退出了**
    /// （godot.log 里既没有异常也没有退出日志，两次多线程运行都如此；单线程运行则稳定）。
    /// 怀疑是游戏的地图生成里有**进程级共享的静态缓存**，被并发调用搞坏。
    /// 所以默认单线程 —— 反正单线程也够用（约 14 张地图/秒，储备 10 个约 30~60 秒凑齐）。
    /// 0 = 自动（目前等同于 1）。
    /// </summary>
    public int WorkerCount { get; set; } = 1;

    /// <summary>
    /// 日志级别：<c>Debug</c> / <c>Info</c> / <c>Warn</c> / <c>Error</c> / <c>Off</c>。
    /// 排障时调成 Debug 能看到全部细节（储备池全量清单、冒烟测试分布等）。
    /// </summary>
    public string LogLevel { get; set; } = "Info";

    /// <summary>日志语言：<c>zh</c> / <c>en</c>。留空则跟随游戏语言。</summary>
    public string LogLanguage { get; set; } = "";

    /// <summary>
    /// 是否在「自定义开局」界面挂一个小状态面板（储备数量 / 搜索状态 / 本局种子）。
    /// 它是纯展示、只新增节点、不动游戏原有 UI；不想看到就设成 false。
    /// </summary>
    public bool ShowStatusPanel { get; set; } = true;

    /// <summary>状态面板的字号。</summary>
    public int PanelFontSize { get; set; } = 10;
}

/// <summary>
/// 读写 <c>settings.json</c>。
///
/// 设计原则：
/// <list type="bullet">
/// <item><b>懒加载 + 容错</b>：文件不存在就写一份默认的；文件损坏就用默认值并记日志，
///       绝不让配置问题把模组搞挂。</item>
/// <item><b>不做 UI</b>：改配置就是编辑这个 json，改完回主菜单生效（需要重启游戏读取）。</item>
/// </list>
/// </summary>
internal static class Settings
{
    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = true,
    };

    static SettingsData _current;

    /// <summary>当前设置（首次访问时自动加载）。</summary>
    public static SettingsData Current
    {
        get
        {
            if (_current == null) Load();
            return _current;
        }
    }

    public static string FilePath => Path.Combine(SeedStore.Dir, "settings.json");

    /// <summary>加载；任何失败都退回默认值并记日志。</summary>
    public static void Load()
    {
        try
        {
            SeedStore.EnsureDir();

            if (!File.Exists(FilePath))
            {
                _current = new SettingsData();
                SaveDefaults(_current);
                ApplyRuntimeOptions(_current);
                SeedStore.Log($"[设置] 首次运行，已写入默认配置: {FilePath}");
                return;
            }

            var json = File.ReadAllText(FilePath, Encoding.UTF8);
            var loaded = JsonSerializer.Deserialize<SettingsData>(json, Json);
            if (loaded == null)
            {
                _current = new SettingsData();
                ApplyRuntimeOptions(_current);
                SeedStore.Log("[设置] 配置文件解析为空，用默认值");
                return;
            }

            _current = Sanitize(loaded);
            ApplyRuntimeOptions(_current);
            SeedStore.Log($"[设置] 已加载: {FilePath}");
        }
        catch (Exception ex)
        {
            _current = new SettingsData();
            ApplyRuntimeOptions(_current);
            SeedStore.Warn($"[设置] 读取失败，用默认值: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>把设置里的运行时开关应用到日志系统。</summary>
    static void ApplyRuntimeOptions(SettingsData d)
    {
        try
        {
            Logger.Level = Logger.ParseLevel(d.LogLevel);

            // 日志语言：留空就跟随游戏语言
            var lang = d.LogLanguage;
            if (string.IsNullOrWhiteSpace(lang))
            {
                try
                {
                    lang = Godot.TranslationServer.GetLocale();   // 形如 "zh_CN" / "en"
                }
                catch { lang = "zh"; }
            }
            Logger.English = lang != null && lang.StartsWith("en", StringComparison.OrdinalIgnoreCase);
        }
        catch { /* 设置失败不影响功能 */ }
    }

    static void SaveDefaults(SettingsData d)
    {
        try
        {
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(d, Json), new UTF8Encoding(false));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex) { SeedStore.Warn("[设置] 写默认配置失败: " + ex.Message); }
    }

    /// <summary>把明显不合理的值纠正回安全范围（用户手改 json 时难免写错）。</summary>
    static SettingsData Sanitize(SettingsData d)
    {
        var notes = new List<string>();

        if (d.ReserveCapacity < 1 || d.ReserveCapacity > 200)
        { notes.Add($"ReserveCapacity={d.ReserveCapacity} → 10"); d.ReserveCapacity = 10; }

        if (d.MaxMonsterPercent < 10 || d.MaxMonsterPercent > 100)
        { notes.Add($"MaxMonsterPercent={d.MaxMonsterPercent} → 30"); d.MaxMonsterPercent = 30; }

        if (d.MaxRunMinutes < 1 || d.MaxRunMinutes > 60)
        { notes.Add($"MaxRunMinutes={d.MaxRunMinutes} → 3"); d.MaxRunMinutes = 3; }

        // 0 = 自动，是合法值；负数或过大才纠正
        if (d.WorkerCount < 0 || d.WorkerCount > 32)
        { notes.Add($"WorkerCount={d.WorkerCount} → 0（自动）"); d.WorkerCount = 0; }

        if (d.Ascension < 0 || d.Ascension > 20)
        { notes.Add($"Ascension={d.Ascension} → 10"); d.Ascension = 10; }

        if (d.PlayersCount < 1 || d.PlayersCount > 4)
        { notes.Add($"PlayersCount={d.PlayersCount} → 1"); d.PlayersCount = 1; }

        if (string.IsNullOrWhiteSpace(d.CharacterKey))
        { notes.Add("CharacterKey 为空 → CHARACTER:SILENT"); d.CharacterKey = "CHARACTER:SILENT"; }

        if (d.PanelFontSize < 8 || d.PanelFontSize > 96)
        { notes.Add($"PanelFontSize={d.PanelFontSize} → 10"); d.PanelFontSize = 10; }

        if (notes.Count > 0)
            SeedStore.Log("[设置] 有以下值不合理，已自动纠正：\n  - " + string.Join("\n  - ", notes));

        return d;
    }

    /// <summary>一行摘要，写日志用。</summary>
    public static string Describe()
    {
        var d = Current;
        return $"储备池={d.ReserveCapacity}; 怪物占比<={d.MaxMonsterPercent}%; " +
               $"{d.CharacterKey} A{d.Ascension}; 线程={d.WorkerCount}; 单次上限={d.MaxRunMinutes}分钟; " +
               $"面板={(d.ShowStatusPanel ? "开" : "关")}";
    }
}
