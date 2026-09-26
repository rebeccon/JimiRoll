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
    public int ReserveCapacity { get; set; } = 15;

    /// <summary>第一章问号房间的门槛。</summary>
    public int MinUnknownRooms { get; set; } = 12;

    /// <summary>true = 必须恰好等于门槛；false = 大于等于。</summary>
    public bool RequireExactUnknownRooms { get; set; } = false;

    /// <summary>角色（形如 CHARACTER:SILENT）。</summary>
    public string CharacterKey { get; set; } = "CHARACTER:SILENT";

    /// <summary>进阶等级。</summary>
    public int Ascension { get; set; } = 10;

    /// <summary>单局人数。</summary>
    public int PlayersCount { get; set; } = 1;

    /// <summary>单次搜索的时间上限（分钟）—— 防内存持续上涨。</summary>
    public int MaxRunMinutes { get; set; } = 3;

    /// <summary>
    /// 搜索线程数。
    /// 实测瓶颈在第二级（RT2 的 Predict 是串行的），多开线程对总速度没帮助，
    /// 只会多占游戏的 CPU 和内存带宽。所以默认 1。
    /// </summary>
    public int WorkerCount { get; set; } = 1;
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
                SeedStore.Log($"[设置] 首次运行，已写入默认配置: {FilePath}");
                return;
            }

            var json = File.ReadAllText(FilePath, Encoding.UTF8);
            var loaded = JsonSerializer.Deserialize<SettingsData>(json, Json);
            if (loaded == null)
            {
                SeedStore.Log("[设置] 配置文件解析为空，用默认值");
                _current = new SettingsData();
                return;
            }

            _current = Sanitize(loaded);
            SeedStore.Log($"[设置] 已加载: {FilePath}");
        }
        catch (Exception ex)
        {
            SeedStore.Log($"[设置] 读取失败，用默认值: {ex.GetType().Name}: {ex.Message}");
            _current = new SettingsData();
        }
    }

    static void SaveDefaults(SettingsData d)
    {
        try
        {
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(d, Json), new UTF8Encoding(false));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex) { SeedStore.Log("[设置] 写默认配置失败: " + ex.Message); }
    }

    /// <summary>把明显不合理的值纠正回安全范围（用户手改 json 时难免写错）。</summary>
    static SettingsData Sanitize(SettingsData d)
    {
        var notes = new List<string>();

        if (d.ReserveCapacity < 1 || d.ReserveCapacity > 200)
        { notes.Add($"ReserveCapacity={d.ReserveCapacity} → 15"); d.ReserveCapacity = 15; }

        if (d.MinUnknownRooms < 10 || d.MinUnknownRooms > 14)
        { notes.Add($"MinUnknownRooms={d.MinUnknownRooms} → 12（游戏取值范围只可能是 10~14）"); d.MinUnknownRooms = 12; }

        if (d.MaxRunMinutes < 1 || d.MaxRunMinutes > 60)
        { notes.Add($"MaxRunMinutes={d.MaxRunMinutes} → 3"); d.MaxRunMinutes = 3; }

        if (d.WorkerCount < 1 || d.WorkerCount > 32)
        { notes.Add($"WorkerCount={d.WorkerCount} → 1"); d.WorkerCount = 1; }

        if (d.Ascension < 0 || d.Ascension > 20)
        { notes.Add($"Ascension={d.Ascension} → 10"); d.Ascension = 10; }

        if (d.PlayersCount < 1 || d.PlayersCount > 4)
        { notes.Add($"PlayersCount={d.PlayersCount} → 1"); d.PlayersCount = 1; }

        if (string.IsNullOrWhiteSpace(d.CharacterKey))
        { notes.Add("CharacterKey 为空 → CHARACTER:SILENT"); d.CharacterKey = "CHARACTER:SILENT"; }

        if (notes.Count > 0)
            SeedStore.Log("[设置] 有以下值不合理，已自动纠正：\n  - " + string.Join("\n  - ", notes));

        return d;
    }

    /// <summary>一行摘要，写日志用。</summary>
    public static string Describe()
    {
        var d = Current;
        return $"储备池={d.ReserveCapacity}; 问号{(d.RequireExactUnknownRooms ? "==" : ">=")}{d.MinUnknownRooms}; " +
               $"{d.CharacterKey} A{d.Ascension}; 线程={d.WorkerCount}; 单次上限={d.MaxRunMinutes}分钟";
    }
}
