namespace JimiRoll;

/// <summary>
/// 筛种条件。
///
/// 分两类：
/// <list type="bullet">
/// <item><b>固定条件</b>（哪些遗物、哪个诅咒）—— 写死在代码里。
///       这些不该随便改，改错代价大，所以<b>故意</b>不放进配置文件。</item>
/// <item><b>可调参数</b>（问号门槛、角色、难度、储备池容量…）—— 从
///       <c>settings.json</c> 读，改完回主菜单生效，<b>不用重新编译</b>。</item>
/// </list>
/// </summary>
internal static class Conditions
{
    // ==================================================================
    // 一、固定条件（涅奥骨骰开局）
    // ==================================================================

    /// <summary>
    /// 需要出现在开局选项里的遗物（涅奥骨骰）。游戏里的类是
    /// <c>MegaCrit.Sts2.Core.Models.Relics.NeowsBones</c>。
    /// 它会给出「两个遗物 + 一个诅咒」，对应 RT2 的 <c>RequireNeowsBones</c>。
    ///
    /// 格式 <c>类别:ENTRY</c> —— 类别是 <c>RELIC</c> / <c>CARD</c> 等。
    /// ⚠️ 别混：<c>NeowsSacrifice</c> / <c>NeowsTalisman</c> / <c>NeowsTorment</c> 是另外三个遗物。
    /// </summary>
    public const string RequireRelicKey = "RELIC:NEOWS_BONES";

    /// <summary>
    /// 骨骰必须给出的两个遗物（All 语义 = 两个都必须命中）。
    /// 骨骰本身就只给两个，所以这里就是"两个都要是我要的"。
    /// </summary>
    public static readonly string[] RequiredBonesRelics =
    {
        "RELIC:PRECARIOUS_SHEARS",  // 松动羊毛剪（⚠️ 不是 RELIC:PRECISE_SCISSORS 精准剪刀）
        "RELIC:NEW_LEAF",           // 新叶（⚠️ 不是 RELIC:LEAFY_POULTICE 药膏叶）
    };

    /// <summary>
    /// 骨骰给出的诅咒必须是这一个。
    /// 游戏里的类是 <c>MegaCrit.Sts2.Core.Models.Cards.Guilty</c>。
    /// （⚠️ 不是 Regret 悔恨 / Shame 羞耻 / Debt 债务）
    /// </summary>
    public const string RequiredFinalCurseKey = "CARD:GUILTY";

    // ==================================================================
    // 二、可调参数 —— ★ 这些现在从 settings.json 读，改完不用重新编译
    //     （文件在 %APPDATA%\SlayTheSpire2\JimiRoll\settings.json）
    // ==================================================================

    /// <summary>第一章问号房间的门槛。取值范围只可能是 10~14。</summary>
    public static int TargetAct1UnknownRooms => Settings.Current.MinUnknownRooms;

    /// <summary>true = 必须恰好等于门槛；false = 大于等于。</summary>
    public static bool RequireExactUnknownRooms => Settings.Current.RequireExactUnknownRooms;

    /// <summary>角色。</summary>
    public static string CharacterKey => Settings.Current.CharacterKey;

    /// <summary>难度（进阶等级）。</summary>
    public static int Ascension => Settings.Current.Ascension;

    /// <summary>单局人数（1 = 单人）。</summary>
    public static int PlayersCount => Settings.Current.PlayersCount;

    /// <summary>
    /// 搜索线程数。
    ///
    /// ★ 实测结论：<b>1 个就够</b>，多开纯属白烧 CPU。
    ///
    /// 原因：瓶颈在第二级，而 RT2 的 Predict 已被全局锁串行化（约 2.8 万次/秒）。
    /// 单线程的第一级产出 = 370 万种子/秒 × 69% 通过率 ≈ 250 万次/秒，
    /// 远超第二级能吃下的量。所以再加线程只会分走游戏的 CPU 和内存带宽。
    /// </summary>
    public static int WorkerCount => Settings.Current.WorkerCount;

    /// <summary>★ 储备池容量 —— 存放的种子数量恒定维持在这个值。</summary>
    public static int ReserveCapacity => Settings.Current.ReserveCapacity;

    /// <summary>单次搜索的时间上限（分钟）—— 防内存持续上涨的安全网。</summary>
    public static int MaxRunMinutes => Settings.Current.MaxRunMinutes;

    /// <summary>
    /// 起点种子。空字符串 = 从游标文件里读（首次则从 0 开始）。
    /// （这个留在代码里 —— 正常使用不需要动它。）
    /// </summary>
    public const string StartSeed = "";

    // ==================================================================
    // 判断辅助
    // ==================================================================

    public static bool UnknownRoomsPass(int actual)
        => RequireExactUnknownRooms
            ? actual == TargetAct1UnknownRooms
            : actual >= TargetAct1UnknownRooms;

    /// <summary>把条件描述成一行，写进结果文件和日志，方便日后对账。</summary>
    public static string Describe()
        => $"涅奥骨骰={RequireRelicKey}; 遗物={string.Join("+", RequiredBonesRelics)}; "
         + $"诅咒={RequiredFinalCurseKey}; 第一章问号{(RequireExactUnknownRooms ? "==" : ">=")}{TargetAct1UnknownRooms}; "
         + $"{CharacterKey} A{Ascension}";
}
