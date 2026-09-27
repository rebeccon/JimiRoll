using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Acts;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Runs;

namespace JimiRoll;

/// <summary>
/// ★ 自研的「第一个事件」判定 —— 完成「第一章问号门槛」之后再加的一道条件。
///
/// ============================================================================
/// 条件定义（方案 A）
/// ============================================================================
/// **第一个问号房间就要是「事件」，而且那个事件必须是变形灵林谷（MorphicGrove）。**
///
/// 这样玩家进第一个问号房间就直接看到变形灵林谷，体感上等价于「第一个事件是它」，
/// 而且不需要实现完整的赔率状态机（那要求模拟整条路径）。
///
/// ============================================================================
/// 机制（全部逐条 IL 反汇编 + 存档实证）
/// ============================================================================
/// 【A】问号房间的类型是**进房时掷骰**决定的
///     <c>RunManager.RollRoomTypeFor</c> → <c>UnknownMapPointOdds.Roll</c>
///     <c>UnknownMapPointOdds..ctor</c> 的赔率表（IL 里的字面量）：
///         Monster 0.10 / Elite -1（不参与）/ Treasure 0.02 / Shop 0.03
///     掷骰是<b>顺序累计</b>的：<c>acc += odds; if (roll &lt;= acc) { result = type; break; }</c>，
///     而<b>默认结果就是 Event</b>。
///     ⇒ roll &gt; 0.15 ⇒ 一定是事件。
///     ★ 保守且安全：即使 Shop 被黑名单屏蔽（<c>BuildRoomTypeBlacklist</c> 只在
///       "该节点的子节点全是商店"时才屏蔽），累计只到 0.12，roll &gt; 0.15 仍然落在默认值 Event。
///       ⇒ 用 0.15 判定在两种情况下都正确，代价只是漏掉一小撮种子，绝不会判错。
///     RNG：<c>new RunOddsSet(RunRngSet.UnknownMapPoint)</c>（在 RunState.CreateForNewRun 里构造），
///     第一个问号房间之前**没有被消耗过**。
///
/// 【B】事件不是进房随机抽的，而是**每个 Act 开局预生成一个洗过的队列**，玩家按顺序取
///     <c>ActModel.GenerateRooms</c>:
///         <c>evts = AllEvents.Concat(ModelDb.AllSharedEvents).ToList();</c>
///         （按 Event1/2/3Epoch 的解锁情况 RemoveAll）
///         <c>_rooms.events.AddRange(UnstableShuffle(evts, rng));</c>
///     <c>RoomSet.NextEvent = events[eventsVisited % Count]</c>
///     ⇒ **第一个事件 = 洗完队列的第一张。**
///
/// 【C】这条队列用的是 <c>RunRngSet.UpFront</c>，而它在之前已经被消耗过：
///     <c>RunManager.GenerateRooms</c>（IL 逐条读完）：
///         ancients = UnstableShuffle(SharedAncients.ToList(), UpFront)   // A-1 次
///         foreach (act in State.Acts.Skip(1)) { UpFront.NextInt(ancients.Count + 1); ... }  // Acts-1 次
///         for (i = 0; i &lt; Acts.Count; i++) State.Acts[i].GenerateRooms(UpFront, ...)  // i=0 就洗事件
///     ⇒ 前置消耗 <c>k = (SharedAncients - 1) + (Acts - 1)</c>。
///     实测：<c>UnlockState.SharedAncients = 1</c>（只有 Darv），
///           存档 <c>1790480922.run</c> 里 <c>"acts": ["ACT.OVERGROWTH","ACT.HIVE","ACT.GLORY"]</c>
///           ⇒ Acts = 3 ⇒ <b>k = 0 + 2 = 2</b>。
///
/// 【D】事件池 = Overgrowth 的 13 个 + ModelDb.AllSharedEvents 的 18 个 = <b>31 个</b>
///     （Act1 确定是 Overgrowth —— 存档实证；ActModel.ActsByIndex 槽位 0 是 [Overgrowth, Underdocks]，
///      但实际跑的是 Overgrowth。）
///
/// ============================================================================
/// 安全策略
/// ============================================================================
/// 与 <see cref="NeowPredict"/> 一致：初始化时把「我们自己拼的事件池」和
/// 「游戏 GenerateRooms 真跑一遍得到的结果」比一比（集合必须相等），
/// 不等就拒绝启动。这样池子顺序错了不会静默给错种子。
/// </summary>
internal static class EventPredict
{
    /// <summary>目标事件（变形灵林谷）的类名。</summary>
    public const string TargetEventName = "MorphicGrove";

    /// <summary>房间类型掷骰的事件阈值（见类注释【A】）。</summary>
    const float EventRollThreshold = 0.15f;

    /// <summary>UpFront 在事件队列洗牌之前被消耗的次数（见类注释【C】）。</summary>
    const int UpFrontSkips = 2;

    static List<string> _pool;
    static string _error;
    static bool _poolVerified;

    /// <summary>每个 Act 的事件池（按类名，顺序 = AllEvents + AllSharedEvents）。</summary>
    static readonly Dictionary<string, List<string>> PoolsByAct = new();

    /// <summary>Act 1 的候选（顺序 = ModelDb.ActsByIndex[0]，实测是 [Overgrowth, Underdocks]）。</summary>
    static List<object> _act1Candidates;

    static object _unlock;
    const bool _isMultiplayer = false;   // 我们只做单人

    public static bool Ready => _error == null && _pool != null;
    public static int PoolSize => _pool?.Count ?? 0;
    public static bool PoolVerified => _poolVerified;
    public static string Act1Resolved { get; private set; } = "(未定)";

    public static string Status => _error != null
        ? "✗ " + _error
        : _pool == null
            ? "⏳ 还没初始化"
            : $"✓ 已就绪（Act1 候选 {_act1Candidates?.Count ?? 0} 个；" +
              $"Overgrowth 池 {GetPool("Overgrowth")?.Count ?? 0} 个含 {TargetEventName}=" +
              $"{GetPool("Overgrowth")?.Contains(TargetEventName) ?? false}；池子与游戏一致={_poolVerified}）";

    static List<string> GetPool(string actName) => PoolsByAct.TryGetValue(actName, out var v) ? v : null;

    // ==================================================================
    // 初始化（主线程）
    // ==================================================================

    public static bool Initialize()
    {
        if (_pool != null) return true;
        try
        {
            _unlock = GetUnlockState();
            if (_unlock == null) { _error = "拿不到 UnlockState.all"; return false; }

            // ★ 每个 Act 各建一份池子（因为 Act 1 可能是 Overgrowth 也可能是 Underdocks，
            //   而 MorphicGrove 只在 Overgrowth 里）。
            foreach (var act in ModelDb.Acts)
            {
                var names = act.AllEvents
                    .Concat(ModelDb.AllSharedEvents)
                    .Where(e => e != null)
                    .Select(e => e.GetType().Name)
                    .ToList();
                PoolsByAct[act.GetType().Name] = names;
            }

            foreach (var kv in PoolsByAct)
                SeedStore.Log($"[事件] {kv.Key,-12} 事件池 {kv.Value.Count,3} 个" +
                              (kv.Value.Contains(TargetEventName) ? "   ★含变形灵林谷" : ""));

            var overgrowthPool = GetPool("Overgrowth");
            if (overgrowthPool == null || !overgrowthPool.Contains(TargetEventName))
            {
                _error = "Overgrowth 的事件池里没有 MorphicGrove —— 条件不可能成立";
                return false;
            }

            // Act 1 的候选（顺序至关重要：rng.NextItem 按这个顺序取下标）
            var byIndex = ModelDb.ActsByIndex;
            if (byIndex == null || byIndex.Count == 0) { _error = "ModelDb.ActsByIndex 是空的"; return false; }
            _act1Candidates = byIndex[0].Cast<object>().ToList();
            SeedStore.Log($"[事件] Act1 候选（按序）= {string.Join(", ", _act1Candidates.Select(a => a.GetType().Name))}");
            if (!_act1Candidates.Any(a => a.GetType().Name == "Overgrowth"))
            {
                _error = "Act1 候选里没有 Overgrowth —— 条件不可能成立";
                return false;
            }

            _pool = overgrowthPool;
            VerifyPoolAgainstGame();
            SeedStore.Log("[事件] " + Status);
            return true;
        }
        catch (Exception ex)
        {
            _error = $"初始化失败: {ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    // ==================================================================
    // Act 1 是哪一本（复刻 ActModel.GetRandomList）
    // ==================================================================

    /// <summary>
    /// 复刻 <c>ActModel.GetRandomList(Rng rng, UnlockState, bool)</c>（IL 逐条）：
    /// <code>
    /// foreach (group in ModelDb.ActsByIndex) {
    ///     chosen = null; unlocked = [];
    ///     foreach (act in group) {
    ///         if (!act.IsUnlocked(unlockState)) continue;
    ///         if (!act.IsDefault &amp;&amp; !isMultiplayer
    ///             &amp;&amp; !SaveManager.Instance.Progress.DiscoveredActs.Contains(act.Id)
    ///             &amp;&amp; TestMode.IsOff()) { chosen = act; break; }   // 没发现过的非默认 Act 优先
    ///         unlocked.Add(act);
    ///     }
    ///     chosen ??= rng.NextItem(unlocked);
    /// }
    /// </code>
    /// rng = <c>new Rng(hash(seed), "act_selection")</c>（<b>独立流，不消耗 UpFront</b>）。
    ///
    /// 实测：<c>Overgrowth.IsDefault = true</c>、<c>Underdocks.IsDefault = false</c>；
    ///       Act1 候选 = [Overgrowth, Underdocks]。
    /// </summary>
    /// <summary>复刻 ActModel.GetRandomList —— 判定 Act 1 是哪一本。<b>public 供离线对拍使用。</b></summary>
    public static string ResolveAct1(string seed)
    {
        var rng = new Rng(GameInterop.RunRngSeed(seed), ActSelectionDomain);
        var unlocked = new List<object>();

        foreach (var act in _act1Candidates)
        {
            var t = act.GetType();
            var isUnlocked = (bool)(AccessTools.Method(t, "IsUnlocked")?.Invoke(act, new[] { _unlock }) ?? true);
            if (!isUnlocked) continue;

            var isDefault = (bool)(AccessTools.Property(t, "IsDefault")?.GetValue(act) ?? true);
            if (!isDefault && !_isMultiplayer && !IsDiscovered(act) && TestModeIsOff())
                return t.Name;                       // 未发现过的非默认 Act：直接选它（不抽）

            unlocked.Add(act);
        }

        if (unlocked.Count == 0) return null;

        // 按名字抽（UnstableShuffle 之外，NextItem 也是与元素类型无关的）
        var names = unlocked.Select(a => a.GetType().Name).ToList();
        return (string)rng.NextItem(names);
    }

    const string ActSelectionDomain = "act_selection";

    /// <summary>
    /// 仅供离线对拍使用：手动指定"已发现的 Act"。
    /// 离线环境里 <c>SaveManager.Instance</c> 会触发 Godot 原生调用直接崩（不可 catch），
    /// 所以由调用方注入。
    /// </summary>
    public static void SetDiscoveredForTest(IEnumerable<string> ids)
    {
        _discovered = new HashSet<string>(ids ?? Array.Empty<string>());
    }

    static HashSet<string> _discovered;

    static bool IsDiscovered(object act)
    {
        try
        {
            if (_discovered == null)
            {
                _discovered = new HashSet<string>();
                var saveMgrT = AccessTools.TypeByName("MegaCrit.Sts2.Core.Saves.SaveManager");
                var inst = AccessTools.Property(saveMgrT, "Instance")?.GetValue(null)
                           ?? AccessTools.Field(saveMgrT, "Instance")?.GetValue(null);
                var progress = AccessTools.Property(saveMgrT, "Progress")?.GetValue(inst);
                var discovered = AccessTools.Property(progress?.GetType(), "DiscoveredActs")?.GetValue(progress)
                                 as System.Collections.IEnumerable;
                if (discovered != null)
                    foreach (var did in discovered) _discovered.Add(did?.ToString());
            }
        }
        catch { /* 拿不到就当成"已发现"，走随机分支（更保守） */ }

        var actId = AccessTools.Property(act.GetType(), "Id")?.GetValue(act)?.ToString();
        return actId == null || _discovered.Contains(actId);
    }

    static bool TestModeIsOff()
    {
        try
        {
            var t = AccessTools.TypeByName("MegaCrit.Sts2.Core.TestSupport.TestMode");
            var m = AccessTools.Method(t, "get_IsOff") ?? AccessTools.Method(t, "IsOff");
            return m == null || (bool)m.Invoke(null, null);
        }
        catch { return true; }
    }

    /// <summary>
    /// 用一个固定 rng，分别跑「游戏自己的 GenerateRooms」和「我们的名字池洗牌」，
    /// 两者结果必须完全一致。不一致就说明池子的内容或顺序错了。
    /// </summary>
    static void VerifyPoolAgainstGame()
    {
        try
        {
            var actT = typeof(ActModel);
            var roomsF = AccessTools.Field(actT, "_rooms");
            var roomSetT = roomsF?.FieldType;
            var eventsF = roomSetT == null ? null : AccessTools.Field(roomSetT, "events");
            var genRoomsM = AccessTools.Method(actT, "GenerateRooms");
            var toMutableM = AccessTools.Method(actT, "ToMutable");
            var unlock = GetUnlockState();

            if (roomsF == null || eventsF == null || genRoomsM == null || toMutableM == null || unlock == null)
            {
                SeedStore.Warn("[事件] ⚠ 无法与游戏对拍（缺反射句柄），池子顺序未经验证");
                return;
            }

            for (var trial = 0; trial < 3; trial++)
            {
                var probeSeed = 0x5EED_0000UL + (ulong)trial * 7919UL;

                // 我们的：名字池洗牌
                var mine = new List<string>(_pool);
                new Rng(probeSeed).Shuffle(mine);

                // 游戏的：真跑 GenerateRooms
                var act = toMutableM.Invoke(ModelDb.Act<Overgrowth>(), null);
                genRoomsM.Invoke(act, new object[] { new Rng(probeSeed), unlock, false });
                var rooms = roomsF.GetValue(act);
                var list = (System.Collections.IList)eventsF.GetValue(rooms);
                var theirs = list.Cast<object>().Select(e => e.GetType().Name).ToList();

                if (mine.Count != theirs.Count || !mine.SequenceEqual(theirs))
                {
                    _error = "事件池与游戏不一致 —— 拒绝启动（宁可不筛，也不给错种子）。" +
                             $"\n      第 {trial + 1} 次对拍：我们 {mine.Count} 个 / 游戏 {theirs.Count} 个。" +
                             "\n      前 5 个 我们=" + string.Join(",", mine.Take(5)) +
                             "  游戏=" + string.Join(",", theirs.Take(5));
                    _pool = null;
                    return;
                }
            }

            _poolVerified = true;
        }
        catch (Exception ex)
        {
            SeedStore.Warn("[事件] ⚠ 对拍过程抛异常（池子顺序未经验证）: " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    static object GetUnlockState()
    {
        try { return AccessTools.Field(typeof(MegaCrit.Sts2.Core.Unlocks.UnlockState), "all")?.GetValue(null); }
        catch { return null; }
    }

    // ==================================================================
    // 判定
    // ==================================================================

    /// <summary>true = 第一个问号房间就是事件，且该事件是变形灵林谷。</summary>
    public static bool FirstRoomIsTargetEvent(string seed, out string evidence)
    {
        evidence = null;
        if (!Ready) return false;

        // ---- 0) Act 1 是哪一本？MorphicGrove 只在 Overgrowth 里 ----
        var act1 = ResolveAct1(seed);
        Act1Resolved = act1 ?? "(拿不到)";
        if (act1 != "Overgrowth")
        {
            evidence = $"Act1={Act1Resolved}（不是 Overgrowth）⇒ 第一个事件不可能是变形灵林谷";
            return false;
        }

        // ---- A) 第一个问号房间的类型掷骰 ----
        //   RunOddsSet(RunRngSet.UnknownMapPoint).UnknownMapPoint.Roll
        //   赔率累计：Monster 0.10 → Treasure 0.12 → Shop 0.15，默认结果是 Event
        var oddsRng = new RunRngSet(seed).UnknownMapPoint;
        var roll = oddsRng.NextFloat(1f);
        if (roll <= EventRollThreshold)
        {
            evidence = $"Act1=Overgrowth；第一个问号房间不是事件（roll={roll:F4} ≤ {EventRollThreshold}）";
            return false;
        }

        // ---- B) 事件队列的第一张 ----
        var upFront = new RunRngSet(seed).UpFront;
        for (var i = 0; i < UpFrontSkips; i++) upFront.NextInt(2);

        var pool = new List<string>(_pool);
        upFront.Shuffle(pool);            // 与 ListExtensions.UnstableShuffle 是同一个 Fisher-Yates
        var first = pool[0];

        evidence = $"Act1=Overgrowth；第一个问号房=事件（roll={roll:F4}）→ 第一个事件={first}";
        if (first == TargetEventName) { evidence += "  ✓全中"; return true; }
        return false;
    }

    // ==================================================================
    // 自检
    // ==================================================================

    public static void SelfTest(List<string> problems)
    {
        if (!Ready) { problems.Add("[事件] 未就绪: " + Status); return; }

        if (!_poolVerified)
            problems.Add("[事件] 事件池没有通过「与游戏 GenerateRooms 对拍」—— 顺序可能不对，拒绝启动");

        if (PoolSize != 31)
            SeedStore.Warn($"[事件] 事件池是 {PoolSize} 个（预期 31）—— 可能是 epoch 解锁情况不同，" +
                           "只要上面那次对拍通过就没问题");
    }
}
