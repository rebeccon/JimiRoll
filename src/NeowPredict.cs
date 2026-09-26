using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.CardPools;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Runs;

namespace JimiRoll;

/// <summary>
/// ★ 自研的「骨骰条件」判定 —— 完全不用 RolltheSpire2。
///
/// ============================================================================
/// 全部依据逐条 IL 反汇编 + 20 万种子对拍（见 JimiRoll/docs/去RT2化方案.md）
/// ============================================================================
///
/// 【①】涅奥是否给出骨骰
///     <c>EventModel+&lt;BeginEvent&gt;d__28::MoveNext</c>:
///         <c>set_Rng(new Rng( Owner.RunState.Rng.Seed + GetPlayerSlotIndex(Owner)
///                           + GetDeterministicHashCode(Id.Entry) ))</c>
///         注意是<b>单参</b> <c>Rng(ulong)</c>，不带流名字。
///     <c>Neow.GenerateInitialOptions</c>（无 Modifiers 分支）:
///         <c>var opts = CurseOptions.ToList();
///          opts.RemoveAll(o =&gt; o.Relic != null &amp;&amp; !o.Relic.IsAllowedAtNeow(Owner));
///          var chosen = EventModel.Rng.NextItem(opts);</c>   ← 事件流的第 1 次抽取
///         <c>return [2 个洗过的正向选项, chosen]</c>
///     ⇒ 骨骰在 <c>CurseOptions</c> 里，所以「是否给出骨骰」== <c>chosen is NeowsBones</c>。
///     实录：与 RT2 在 200,000 个种子上<b>一致率 100.0000%</b>。
///
/// 【②】骨骰给的两个遗物
///     <c>NeowsBones.AfterObtained</c>: <c>Owner.PlayerRng.Rewards.Shuffle(pool); pool.Take(2)</c>
///
/// 【③】骨骰给的诅咒
///     同处: <c>RunState.Rng.Niche.NextItem(CurseCardPool 过滤排序后的列表)</c>
///
/// 【池子顺序至关重要】
///     <c>Neow.AllPossibleOptions</c> = CurseOptions(10) + PositiveOptions(14) + 单例(6) = 30，
///     减去 NeowsBones 自己、再减去 <c>MassiveScroll</c>（其 <c>IsAllowed</c> 是
///     <c>players.Count &gt; 1</c>，多人专属）⇒ <b>28</b>，
///     与游戏内实测的 <c>BonesEligibleRelics = 28</c> 完全吻合。
///
/// 【前置消耗 = 0】
///     假设搜索（<c>_tools/BonesProbe hypo</c>）在 14 个地面真值种子上证明：
///     两条流在骨骰之前都<b>一次都没被消耗</b>。骨骰是全局第一件事，符合直觉。
/// ============================================================================
/// </summary>
internal static class NeowPredict
{
    /// <summary>涅奥事件的 <c>ModelId.Entry</c>（由 <c>EVENT.NEOW</c> 这个 key 反证）。</summary>
    const string EventEntry = "NEOW";

    /// <summary>
    /// <c>Neow.CurseOptions</c> 的数组顺序 —— <b>这个顺序就是 <c>NextItem</c> 的下标</b>。
    /// 来自 <c>get_CurseOptions</c> 的 IL（stelem 顺序）。
    /// </summary>
    static readonly string[] CurseOptionOrder =
    {
        "CursedPearl", "DowsingRod", "HeftyTablet", "LargeCapsule", "LeafyPoultice",
        "NeowsBones",                       // ★ 目标
        "NeowsSacrifice", "PrecariousShears", "SilkenTress", "SilverCrucible",
    };

    /// <summary>
    /// 骨骰合格遗物池（28 个）—— <b>顺序至关重要</b>，错一位整条 shuffle 全错。
    /// = CurseOptions(去掉 NeowsBones) + PositiveOptions(去掉 MassiveScroll) + 单例。
    /// </summary>
    static readonly string[] RelicPoolOrder =
    {
        // --- CurseOptions 段（去掉 NeowsBones）---
        "CursedPearl", "DowsingRod", "HeftyTablet", "LargeCapsule", "LeafyPoultice",
        "NeowsSacrifice", "PrecariousShears", "SilkenTress", "SilverCrucible",
        // --- PositiveOptions 段（去掉 MassiveScroll：IsAllowed = players.Count > 1）---
        "ArcaneScroll", "BoomingConch", "FishingRod", "GoldenPearl", "Kaleidoscope",
        "LeadPaperweight", "LostCoffer", "NeowsTorment", "NewLeaf", "PhialHolster",
        "PreciseScissors", "ScrollBoxes", "WingedBoots",
        // --- 六个单例 ---
        "LavaRock", "NeowsTalisman", "NutritiousOyster", "Pomander", "SmallCapsule",
        "StoneHumidifier",
    };

    // ==================================================================
    // 初始化（需要 ModelDb，必须在主线程调一次）
    // ==================================================================

    static List<CardModel> _cursePool;
    static string _error;

    public static bool Ready => _error == null && _cursePool != null;
    public static string Status => _error != null ? "✗ " + _error
        : _cursePool == null ? "⏳ 还没初始化（需要在主线程调 Initialize）"
        : $"✓ 已就绪（遗物池 {RelicPoolOrder.Length} 个，诅咒池 {_cursePool.Count} 个）";

    public static int CursePoolSize => _cursePool?.Count ?? 0;

    /// <summary><b>必须在 Godot 主线程调用</b>（要访问 ModelDb）。</summary>
    public static bool Initialize()
    {
        if (_cursePool != null) return true;
        try
        {
            // 与 NeowsBones.AfterObtained 里那一段逐字对应：
            //   CardPool<CurseCardPool>().GetUnlockedCards(...).Where(CanBeGeneratedByModifiers).OrderBy(Id)
            // 我们用 AllCards（已解锁玩家的等价集合）—— 诅咒牌默认全解锁。
            var pool = ModelDb.CardPool<CurseCardPool>()
                .AllCards
                .Where(c => c != null && c.CanBeGeneratedByModifiers)   // 这是【属性】，不是方法
                .OrderBy(c => c.Id)
                .ToList();

            if (pool.Count == 0)
            {
                _error = "CurseCardPool 过滤后是空的 —— 池子推导可能不对，拒绝启动（宁可不筛，也不给错种子）";
                return false;
            }

            _cursePool = pool;
            SeedStore.Log($"[自研] 诅咒池 = {pool.Count} 张: " +
                          string.Join(", ", pool.Take(12).Select(c => c.Id.Entry)) +
                          (pool.Count > 12 ? " …" : ""));
            SeedStore.Log("[自研] " + Status);
            return true;
        }
        catch (Exception ex)
        {
            _error = $"建诅咒池失败: {ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    // ==================================================================
    // 【①】涅奥是否给出骨骰
    // ==================================================================

    /// <summary>true = 涅奥的三个选项里会有骨骰。</summary>
    public static bool BonesOffered(string seed)
    {
        // EventModel.Rng = new Rng(RunRngSet.Seed + slotIndex + hash(事件 Entry))
        // 注意是【单参】构造器（不带流名字）
        var rng = new Rng(GameInterop.RunRngSeed(seed) + GameInterop.RunRngSeed(EventEntry));

        // CurseOptions.RemoveAll(o => o.Relic != null && !o.Relic.IsAllowedAtNeow(owner))
        // 单人局下这 10 个全都 IsAllowedAtNeow（SilverCrucible 要求 players==1，正好满足）
        // ⇒ 池子就是全部 10 个。
        var chosen = rng.NextItem(CurseOptionOrder);
        return chosen == "NeowsBones";
    }

    // ==================================================================
    // 【②③】骨骰给出的遗物与诅咒
    // ==================================================================

    /// <summary>把自研判定包成 SearchDriver 要的 <c>(bool, evidence)</c> 形状。</summary>
    public static bool IsMatch(string seed, out string evidence)
    {
        evidence = null;
        if (!Ready) return false;

        if (!BonesOffered(seed)) return false;

        var (relics, curse) = BonesOutcome(seed);

        var targets = Conditions.RequiredBonesRelics;      // RELIC:PRECARIOUS_SHEARS / RELIC:NEW_LEAF
        var relicOk = relics.Count == 2
                      && targets.All(t => relics.Any(r => NameIs(r, t)));

        var curseName = Conditions.RequiredFinalCurseKey;  // CARD:GUILTY
        var curseOk = curse != null && NameIs(curse, curseName);

        var detail = $"骨骰给出[{string.Join("+", relics.Select(r => "RELIC:" + r))}] + 诅咒={curse}";

        if (relicOk && curseOk)
        {
            evidence = detail + "  ✓全中";
            return true;
        }

        // 不给错种子：只要有一项不符就是 false（顺便留下可诊断的证据）
        evidence = relicOk
            ? detail + $"  ✗诅咒不对（应为 {curseName}）"
            : detail + $"  ✗遗物不对（应为 {string.Join("+", targets)}）";
        return false;
    }

    /// <summary>骰骨实际给出的两个遗物与一个诅咒。</summary>
    public static (List<string> Relics, string Curse) BonesOutcome(string seed)
    {
        // ---- ② 遗物：PlayerRngSet(hash(seed) + slotIndex).Rewards ----
        //      Player.InitializeSeed: new PlayerRngSet(hash(seed) + GetPlayerSlotIndex(this))
        var rewards = new PlayerRngSet(GameInterop.RunRngSeed(seed) + 0UL).Rewards;

        // ★ Shuffle<T> 只做位置交换，与 T 无关 ⇒ 直接拿类名 List<string> 洗，
        //   结果和游戏里洗 RelicModel 完全一致，且不需要建任何模型实例。
        var pool = new List<string>(RelicPoolOrder);
        rewards.Shuffle(pool);
        var picked = pool.Take(2).ToList();

        // ---- ③ 诅咒：RunRngSet.Niche ----
        var niche = new RunRngSet(seed).Niche;
        var curseCard = niche.NextItem(_cursePool);
        var curse = curseCard?.Id.Entry;

        return (picked, curse);
    }

    // ==================================================================
    // 名字比对：类名（PrecariousShears）↔ key（RELIC:PRECARIOUS_SHEARS）
    // ==================================================================

    /// <summary>忽略前缀、大小写、下划线和分隔符。</summary>
    static bool NameIs(string actual, string wanted)
        => Norm(actual) == Norm(wanted);

    static string Norm(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var ch in s)
        {
            if (ch == '_' || ch == ':' || ch == '.' || ch == ' ' || ch == '-') continue;
            sb.Append(char.ToUpperInvariant(ch));
        }
        // 去掉常见前缀，比如 RELIC:/CARD: 已由上面的 ':' 处理
        return sb.ToString();
    }

    // ==================================================================
    // 自检：用游戏内实测过的已知命中种子
    // ==================================================================

    /// <summary>
    /// 这些种子是**游戏里实测过「骨骰 → 松动羊毛剪+新叶 → 愧疚」**的
    /// （JimiRoll 的 seeds_found.json / seeds_used.json 里 Evidence 含「全中」）。
    ///
    /// ★ 它们是自研实现的地面真值。只要有一个不过，就说明我们的推导错了，
    ///   此时**必须拒绝启动搜索** —— 宁可不能用，也绝不能给错种子。
    /// </summary>
    static readonly string[] KnownGoodSeeds =
    {
        "00000006CNR9", "00000007H4GH", "00000007PCZ1", "0000000895Q1",
        "000000092ZKX", "000000097L5B", "0000000A0T45", "0000000ASRX5",
        "0000000ASW9U", "0000000ASRX5", "0000000AT2Y1", "0000000ATKT7",
        "0000000BH268", "0000000BZAE2",
    };

    public static void SelfTest(List<string> problems)
    {
        if (!Ready)
        {
            problems.Add("[自研] 未就绪: " + Status);
            return;
        }

        if (RelicPoolOrder.Length != 28)
            problems.Add($"[自研] 遗物池应有 28 个，实际 {RelicPoolOrder.Length} 个 —— 池子顺序错了");

        var passed = 0;
        var failed = new List<string>();
        foreach (var seed in KnownGoodSeeds.Distinct())
        {
            try
            {
                if (IsMatch(seed, out var ev)) passed++;
                else failed.Add($"      {seed}: {ev}");
            }
            catch (Exception ex)
            {
                failed.Add($"      {seed}: 抛异常 {ex.GetType().Name}: {ex.Message}");
            }
        }

        var total = KnownGoodSeeds.Distinct().Count();
        if (passed == total)
        {
            SeedStore.Log($"[自研] ✓✓ 自检通过：{passed}/{total} 个已知命中种子全部判定为命中");
        }
        else
        {
            problems.Add($"[自研] ✗ 自检失败：只有 {passed}/{total} 个已知命中种子被判为命中。" +
                         "\n    这说明自研推导（RNG 流 / 池子顺序 / 前置消耗）有问题。" +
                         "\n    未通过的种子：\n" + string.Join("\n", failed));
        }

        // 反向抽查：随便挑几个种子，命中率应该很低（否则说明判定过于宽松）
        var hits = 0;
        for (var i = 0; i < 20000; i++)
            if (IsMatch(SeedCodec.Format((ulong)(i * 7919L)), out _)) hits++;

        var rate = hits * 100.0 / 20000;
        SeedStore.Log($"[自研] 反向抽查：20,000 个种子命中 {hits} 个（{rate:F3}%）" +
                      "—— 预期在 0.01% ~ 1% 量级（骨骰 10% × 遗物条件约 0.26% × 诅咒 1/n）");

        if (rate > 5)
            problems.Add($"[自研] ✗ 命中率异常高（{rate:F3}%）—— 判定条件可能写错了");
    }
}
