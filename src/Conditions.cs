namespace JimiRoll;

/// <summary>
/// ★ 你要的筛种条件 —— 改条件只需要改这个文件，然后重新编译。
///
/// 这就是本 Mod 的"简化"所在：不做任意组合的编辑器，条件就是代码里的常量。
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
    // 二、软条件（第一章问号房间尽可能多）
    // ==================================================================

    /// <summary>
    /// 第一章问号房间的目标数量。
    ///
    /// 取值范围 <c>[10, 14]</c>（来自 <c>NextGaussianInt(12,1,10,14)</c>），
    /// 所以 14 是理论上限。<b>实测分布</b>：
    /// <code>
    ///   10 → 6.1%   11 → 24.4%   12 → 38.7%   13 → 24.5%   14 → 6.2%
    /// </code>
    ///
    /// 配合 <see cref="RequireExactUnknownRooms"/> = <c>false</c> 时表示「<b>大于等于</b>」：
    ///   · 14  → 通过率  6.2%（最严）
    ///   · 13  → 通过率 30.7%
    ///   · 12  → 通过率 69.4%（较宽松，命中率约 ×11）
    ///
    /// 放宽这一级几乎不花时间（预筛比第二级快 100 倍以上），
    /// 所以想快点出结果就调到 12。
    /// </summary>
    public const int TargetAct1UnknownRooms = 12;

    /// <summary>
    /// true  = 必须恰好等于 <see cref="TargetAct1UnknownRooms"/>；
    /// false = 大于等于（推荐，命中率更高）。
    /// </summary>
    public const bool RequireExactUnknownRooms = false;

    // ==================================================================
    // 三、运行环境
    // ==================================================================

    /// <summary>角色。存的是你 RT2 当前的设置：沉默猎手。</summary>
    public const string CharacterKey = "CHARACTER:SILENT";

    /// <summary>难度（进阶等级）。存的是你 RT2 当前的设置。</summary>
    public const int Ascension = 10;

    /// <summary>单局人数（1 = 单人）。</summary>
    public const int PlayersCount = 1;

    /// <summary>
    /// 搜索线程数。
    ///
    /// ★ 实测结论：<b>1 个就够</b>，多开纯属白烧 CPU。
    ///
    /// 原因：瓶颈在第二级，而 RT2 的 Predict 已被全局锁串行化（约 2.8 万次/秒）。
    /// 单线程的第一级产出 = 270 万种子/秒 × 6.1% 通过率 ≈ 16 万次/秒，
    /// 远超第二级能吃下的量。所以再加线程只会分走游戏的 CPU 和内存带宽，
    /// 整体速度一点不涨。
    ///
    /// 想证实：把它改成 4 跑一次，看日志里的「种子/秒」——基本不变。
    /// </summary>
    public const int WorkerCount = 1;

    /// <summary>
    /// ★ 储备池容量 —— <b>存放的种子数量恒定维持在这个值</b>。
    ///
    /// 工作方式（消耗式）：
    /// <list type="number">
    /// <item>每次进入「自定义开局」界面，自动取走队首的那个种子并<b>从储备池删除</b>
    ///       （删除的会归档到 seeds_used.json，不会真的丢）</item>
    /// <item>储备池掉到 14 个 → 下次回主菜单自动补搜</item>
    /// <item>储备池满 15 个 → <b>不搜索</b>，完全不占资源</item>
    /// </list>
    ///
    /// 所以它是"取一个、补一个"的自动维持，不需要手动开关。
    /// </summary>
    public const int ReserveCapacity = 15;

    /// <summary>
    /// ★ 单次搜索的【时间上限】（分钟）。到点就停手，哪怕储备池还没补满。
    ///
    /// 这是防止"命中很少 → 搜半天不停 → 内存涨"的安全网。
    /// 这种"到点暂停"不会永久停：下次回主菜单会自动接着跑一段。
    /// </summary>
    public const int MaxRunMinutes = 3;

    /// <summary>
    /// 起点种子。空字符串 = 从游标文件里读（首次则从 0 开始）。
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
