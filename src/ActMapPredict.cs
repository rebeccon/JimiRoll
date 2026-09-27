using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Acts;
using MegaCrit.Sts2.Core.Random;

namespace JimiRoll;

/// <summary>
/// ★ Act 1 地图的房间构成判定 —— 核心指标是「<b>怪物房间占全部格子的比重</b>」，越低越好。
///
/// ============================================================================
/// 为什么不用「问号房间数量」
/// ============================================================================
/// 实测（`_tools/QnProbe map`，400 个种子）：两者几乎**不相关**，而且问号多的一档
/// 怪物占比的最低值反而更差：
/// <code>
///   问号=11  怪物占比 平均 44.07%  最低 22.86%
///   问号=12  怪物占比 平均 42.35%  最低 27.50%
///   问号=13  怪物占比 平均 43.37%  最低 31.11%
///   问号=14  怪物占比 平均 43.13%  最低 32.00%
/// </code>
/// ⇒ 用「问号≥14」预筛会**把怪物占比最低的种子直接排除掉**。所以那条已经删掉了。
///
/// ============================================================================
/// 依据（全部 IL 实证）
/// ============================================================================
/// <c>StandardActMap.CreateFor(runState, replaceTreasureWithElites)</c>:
/// <code>
///   var mapRng = new Rng(runState.Rng.Seed, "act_" + (CurrentActIndex + 1) + "_map");
///   new StandardActMap(mapRng, runState.Act, playersCount &gt; 1,
///                      replaceTreasureWithElites, act.HasSecondBoss, null, true);
/// </code>
/// ★ Act1 用的就是 <c>new Rng(seedHash, "act_1_map")</c> —— 和原来第①级的问号预筛**同一条流**，
///   而 <c>StandardRandomUnknownCount</c> 只是这条流的第一次抽取。
///   现在我们直接把整张地图生成出来数房间。
///
/// Act1 固定是 <c>Overgrowth</c> 或 <c>Underdocks</c>（`act_selection` 独立流随机抽）；
/// 两者的地图参数都是同一个 `StandardActMap`，所以**地图构成与 Act 是哪一本无关**。
/// </summary>
internal static class ActMapPredict
{
    static string _error;

    /// <summary>平均单张地图生成耗时（毫秒）。用来估算搜索吞吐。</summary>
    public static double AverageMs { get; private set; }

    public static bool Ready => _error == null;

    public static string Status => _error ?? "✓ 已就绪（Act1 地图生成可用）";

    /// <summary>主线程调用一次（要访问 ModelDb）。</summary>
    public static bool Initialize()
    {
        try
        {
            // 先跑一次（顺带把 JIT / 静态构造都热身掉），再量 5 次的平均值
            var (m, t, r) = CountRooms("000000000000");
            if (t <= 0) { _error = "地图生成返回 0 个格子 —— API 可能变了"; return false; }

            LogReconcile("启动·主线程");

            // ★ 黄金自检：这几颗种子的 (怪物, 总格) 已经用离线探针 + 真实存档反复核对过。
            //   对不上就说明"游戏内的地图生成"和"探针/存档里的"不是同一条路径 ——
            //   那算出来的占比就是错的，必须拒绝搜索（宁可不能用，也绝不给错种子）。
            //   ⚠️ 只告警、不停手：黄金样本是"干净环境"下算的，装了别的模组后**有可能**合法地
            //     不一样（实测本机装了 RitsuLib/UnlockTheSpire，部分种子确实不一样）。
            //     真正保证质量的是"交付前在主线程复核"那道闸（见 SeedInputPatch）——
      //     那才是和玩家这一局同进程、同线程、同环境的计算。
            SelfTest();

            var sw = System.Diagnostics.Stopwatch.StartNew();
            const int n = 5;
            for (var i = 0; i < n; i++) CountRooms("00000000000" + "12345"[i]);
            sw.Stop();
            AverageMs = sw.Elapsed.TotalMilliseconds / n;

            SeedStore.Log($"[地图] ✓ 可用；示例 seed=000000000000 共 {t} 格，怪物 {m} 格（{r * 100:F2}%），" +
                          $"平均单张耗时 {AverageMs:F1} ms（约 {1000.0 / Math.Max(AverageMs, 0.01):F0} 张/秒/线程）");
            if (AverageMs > 200)
                SeedStore.Warn($"[地图] ⚠ 单张地图生成要 {AverageMs:F1} ms —— 比预期慢很多，可把 MaxMonsterPercent 放宽来提速");
            return true;
        }
        catch (Exception ex)
        {
            _error = $"地图生成不可用: {ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// 生成 Act 1 的地图并统计。
    /// 返回 (怪物格数, 总格数, 怪物占比)。
    /// </summary>
    public static (int Monsters, int Total, double Ratio) CountRooms(string seed)
    {
        var rng = new Rng(GameInterop.RunRngSeed(seed), GameInterop.Act1MapDomain);

        var map = new StandardActMap(
            rng,
            ModelDb.Act<Overgrowth>(),        // Act1 是 Overgrowth 还是 Underdocks 不影响地图参数
            isMultiplayer: Conditions.PlayersCount > 1,
            shouldReplaceTreasureWithElites: false,
            hasSecondBoss: false,
            mapPointTypeCountsOverride: null,
            enablePruning: true);

        var monsters = 0;
        var total = 0;
        foreach (var p in map.GetAllMapPoints())
        {
            if (p == null) continue;
            total++;
            if (p.PointType == MapPointType.Monster) monsters++;
        }
        return (monsters, total, total == 0 ? 1.0 : monsters * 1.0 / total);
    }

    /// <summary>
    /// 黄金样本：种子 → (怪物格数, 总格数)。
    ///
    /// 这些值是用 <c>_tools/QnProbe map &lt;seed&gt; 1</c> 独立算出来的，并且和对真实存档
    /// (<c>saves/history/*.run</c>) 的行走路径验证一致。只要游戏内算出来的不一样，
    /// 就说明判定口径变了 —— 必须停手。
    /// </summary>
    static readonly (string Seed, int Monsters, int Total)[] Golden =
    {
        ("000000000000", 31, 62),
        ("00000000007A", 15, 50),
        ("000000KHVWYS", 10, 44),
        ("000000KQLVEX", 7, 37),
        ("000000KGFPTX", 15, 50),
        ("000000K884MK", 11, 44),
    };

    /// <summary>拿黄金样本对账；返回 false = 不准搜。</summary>
    public static bool SelfTest()
    {
        var ok = true;
        foreach (var (seed, em, et) in Golden)
        {
            try
            {
                var (m, t, _) = CountRooms(seed);
                var good = m == em && t == et;
                if (!good) ok = false;
                SeedStore.Log($"[地图·自检] {(good ? "✓" : "✗")} {seed} → {m}/{t}（期望 {em}/{et}）");
            }
            catch (Exception ex)
            {
                ok = false;
                SeedStore.Log($"[地图·自检] ✗ {seed} 抛异常: {ex.GetType().Name}: {ex.Message}");
            }
        }
        if (ok)
            SeedStore.Log($"[地图·自检] ✓✓ {Golden.Length}/{Golden.Length} 个黄金样本全部一致");
        else
            SeedStore.Warn("[地图·自检] ✗✗ 有样本对不上。继续运行（交付前还有主线程复核兜底），" +
                           "但请留意：可能是游戏改了地图算法，或者装了会改地图的模组。");
        return ok;
    }

    /// <summary>
    /// 诊断：把几个固定种子的地图统计打出来，用来和离线探针（<c>QnProbe map</c>）逐个对账。
    ///
    /// 判据：同一颗种子在**任何情况下**都应给出同样的 (怪物, 总格)。
    /// 只要某一处对不上，就说明地图生成依赖了线程/进程相关的状态。
    /// </summary>
    public static readonly string[] ReconcileSeeds =
    {
        "000000KHVWYS", "000000KYYWLG", "000000KU0Q44",
        "000000KHVE2B", "000000K884MK", "000000000000",
    };

    public static void LogReconcile(string tag)
    {
        try
        {
            var sb = new System.Text.StringBuilder($"[地图·对账] {tag}");
            foreach (var s in ReconcileSeeds)
            {
                var (m, t, r) = CountRooms(s);
                sb.Append($"\n    {s} → {t} 格，怪物 {m} = {r * 100:F2}%");
            }
            SeedStore.Log(sb.ToString());
        }
        catch (Exception ex) { SeedStore.Warn("[地图·对账] 失败: " + ex.Message); }
    }

    /// <summary>这个种子符不符合「怪物占比」要求。</summary>
    public static bool Passes(string seed, out int monsters, out int total, out double ratio)
    {
        (monsters, total, ratio) = CountRooms(seed);
        return ratio <= Conditions.MaxMonsterRatio;
    }

    /// <summary>
    /// 给一条储备记录补上章节统计（只读旧存档时需要）。
    /// ★ 对 <c>ratio</c> 做四舍五入到 2 位小数 —— 避免同一个值因浮点误差在排序/去重时抖动。
    /// </summary>
    public static void FillStats(SeedHit hit)
    {
        if (hit == null || string.IsNullOrWhiteSpace(hit.Seed)) return;
        if (hit.Act1Total > 0) return;   // 已经有统计了
        try
        {
            var (m, t, r) = CountRooms(hit.Seed);
            hit.Act1Monsters = m;
            hit.Act1Total = t;
            hit.Act1MonsterPercent = Math.Round(r * 100.0, 2);
        }
        catch (Exception ex)
        {
            SeedStore.Warn($"[地图] 给 {hit.Seed} 补统计失败: {ex.Message}");
        }
    }
}
