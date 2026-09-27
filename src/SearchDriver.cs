using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace JimiRoll;

/// <summary>
/// 搜索器（v2.1：完全不依赖任何第三方模组）。
///
/// <code>
/// 全种子空间 (34^12)
///   │ ① 骨骰条件（自研，约 500 万种子/秒）
///   │    涅奥给骨骰 → 两个遗物 = 松动羊毛剪 + 新叶 → 诅咒 = 愧疚
///   ▼
/// 候选（约 1/1万）
///   │ ② 第一章地图的【出怪房间占比】≤ 门槛（最贵，约 10~20 张图/秒）
///   ▼
/// 命中 → seeds_found.json
/// </code>
///
/// 两级都是"反着排"的：最便宜的先跑，最贵的放最后。
/// 因为第①级只放 1/1万 的种子过去，第②级实际承担的负载极小。
/// </summary>
internal static class SearchDriver
{
    public static bool Running => _task is { IsCompleted: false };
    static Task _task;
    static CancellationTokenSource _cts;
    static readonly SearchCursor CursorState = new();

    public static long Scanned => Interlocked.Read(ref _scanned);
    public static long Matched => Interlocked.Read(ref _matched);
    public static long PassedStage1 => Interlocked.Read(ref _passedStage1);
    public static double SeedsPerSecond { get; private set; }

    static long _scanned;
    static long _matched;
    static long _passedStage1;

    /// <summary>跑到时间上限而暂停 —— 不是永久停，下次回主菜单会接着跑一段。</summary>
    static volatile bool _pausedByTime;
    /// <summary>因为储备池已满而停下。</summary>
    static volatile bool _stoppedReserveFull;

    /// <summary>是不是因为时间上限而暂停的。</summary>
    public static bool PausedByTime => _pausedByTime;
    /// <summary>是不是因为储备池已满而停的。</summary>
    public static bool StoppedReserveFull => _stoppedReserveFull;

    /// <summary>重新开始一轮时清掉上次的停手状态。</summary>
    public static void ResetRunState()
    {
        _pausedByTime = false;
        _stoppedReserveFull = false;
    }

    // ------------------------------------------------------------------
    // 启停
    // ------------------------------------------------------------------

    /// <summary>
    /// 启动后台搜索。若已经启动过（现在有结果），会 abort（放弃本次）而不是新开一条。
    /// </summary>
    public static void Start(Action<string> onMatch = null)
    {
        if (Running) { SeedStore.Log("搜索已在运行，忽略重复启动"); return; }

        var interop = GameInterop.Ready
            ? null
            : $"[JimiRoll] ✗ GameInterop 不可用，无法搜索: {GameInterop.Failure}";

        if (interop != null) { SeedStore.Log(interop); return; }

        var learned = SeedStore.LoadCursor();
        if (!string.IsNullOrEmpty(Conditions.StartSeed) &&
            SeedCodec.TryParse(Conditions.StartSeed, out var parsedStart))
        {
            learned.NextOrdinal = parsedStart;
        }

        // ★ 关键：把「本轮」的计数清零。
        //   _matched 是静态字段，如果不清零，凑够 30 个停手后
        //   用户「关一次再开」想再搜一批时，循环第一圈就会看到
        //   _matched(=30) >= 目标(30) 立刻又退出 —— 等于点了没反应。
        //   已经找到的种子存在结果文件里，不受影响。
        var prevMatched = Interlocked.Read(ref _matched);
        Interlocked.Exchange(ref _matched, 0);
        Interlocked.Exchange(ref _scanned, 0);
        Interlocked.Exchange(ref _passedStage1, 0);
        ResetRunState();

        CursorState.NextOrdinal = learned.NextOrdinal;
        CursorState.TotalScanned = learned.TotalScanned;
        CursorState.TotalMatched = learned.TotalMatched;

        // ★ 必须把起点灌进 _cursorOrdinal，否则循环会从 0 重头扫（游标文件形同虚设）
        Interlocked.Exchange(ref _cursorOrdinal, (long)learned.NextOrdinal);

        if (prevMatched > 0)
            SeedStore.Log($"（上一轮找到 {prevMatched} 个；本轮重新开始计数，已有的种子仍保留在结果文件里）");

        _cts = new CancellationTokenSource();
        _task = Task.Run(() => Run(learned, onMatch, _cts.Token));
    }

    public static void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        SeedStore.Log("已请求停止搜索");
    }

    // ------------------------------------------------------------------
    // 主循环
    // ------------------------------------------------------------------

    static void Run(SearchCursor learned, Action<string> onMatch, CancellationToken ct)
    {
        SeedStore.Log("======== 开始筛种 ========");
        SeedStore.Log("条件: " + Conditions.Describe());
        SeedStore.Log("哈希: " + GameInterop.VerifyReport);
        SeedStore.Log($"起点 ordinal = {learned.NextOrdinal}");

        // ★ 0 = 自动（= 1）。⚠️ 实测多线程会让游戏无征兆退出（见 SettingsData.WorkerCount），
        //    所以自动值就是 1；用户非要多开才走配置值。
        var workers = Conditions.WorkerCount > 0
            ? Math.Clamp(Conditions.WorkerCount, 1, 8)
            : 1;
        SeedStore.Log($"线程数 = {workers}（逻辑核 {Environment.ProcessorCount}）");
        SeedStore.Log($"目标: 把储备池补到 {Conditions.ReserveCapacity} 个（当前 {SeedStore.ReserveCount} 个）就停手；" +
                      $"单次最多跑 {Conditions.MaxRunMinutes} 分钟（防内存持续上涨）");

        // 每个 worker 负责种子空间的一段（和 Base34SeedEnumerator.Partition 同思路）
        var chunk = 1_000_000UL;
        var sw = Stopwatch.StartNew();
        var lastReport = sw.Elapsed;
        var cursorFlush = sw.Elapsed;
        var seedCounter = 0;
        var budget = TimeSpan.FromMinutes(Math.Max(1, Conditions.MaxRunMinutes));
        var memAtStart = GC.GetTotalMemory(false);


        try
        {
            while (!ct.IsCancellationRequested)
            {
                // ---- 停手条件 1：储备池已满 ----
                if (SeedStore.IsReserveFull)
                {
                    _stoppedReserveFull = true;
                    SeedStore.Log($"✓ 储备池已满（{SeedStore.ReserveCount}/{Conditions.ReserveCapacity}）—— 停手");
                    break;
                }

                // ---- 停手条件 2：时间预算用完 ----
                if (sw.Elapsed >= budget)
                {
                    _pausedByTime = true;
                    SeedStore.Log($"⏸ 已跑满 {budget.TotalMinutes:F0} 分钟 —— 暂停（下次回主菜单会接着跑一段）");
                    break;
                }

                var start = (ulong)Interlocked.Read(ref _cursorOrdinal);
                if (start >= SeedCodec.SpaceSize)
                {
                    SeedStore.Log("已扫完整个种子空间（34^12），停止");
                    _stoppedReserveFull = true;
                    break;
                }

                var end = Math.Min(start + chunk * (ulong)workers, SeedCodec.SpaceSize);
                var perWorker = (end - start) / (ulong)workers;

                Parallel.For(0, workers, new ParallelOptions { CancellationToken = ct }, w =>
                {
                    var from = start + (ulong)w * perWorker;
                    var to = (w == workers - 1) ? end : from + perWorker;
                    ScanRange(from, to, onMatch, ct);
                });

                Interlocked.Exchange(ref _cursorOrdinal, (long)end);

                // 每 3 秒汇报一次（★ 带上内存，方便判断是不是真的在涨）
                if (sw.Elapsed - lastReport > TimeSpan.FromSeconds(3))
                {
                    lastReport = sw.Elapsed;
                    var s = Interlocked.Read(ref _scanned);
                    SeedsPerSecond = s / Math.Max(sw.Elapsed.TotalSeconds, 1e-9);
                    var mem = GC.GetTotalMemory(false);
                    SeedStore.Log(
                        $"进度: 已扫 {s:N0}  第①级通过 {Interlocked.Read(ref _passedStage1):N0}  " +
                        $"命中本轮 {Interlocked.Read(ref _matched):N0}  抖动淘汰 {Interlocked.Read(ref _mapUnstable):N0}  " +
                        $"储备 {SeedStore.ReserveCount}/{Conditions.ReserveCapacity}  " +
                        $"{SeedsPerSecond:N0} 种子/秒  " +
                        $"托管内存 {mem / 1024.0 / 1024.0:F1} MB (起始 {memAtStart / 1024.0 / 1024.0:F1} MB)  " +
                        $"剩余 {(budget - sw.Elapsed).TotalSeconds:F0}s");
                }

                // 每 15 秒刷一次游标与结果
                if (sw.Elapsed - cursorFlush > TimeSpan.FromSeconds(15))
                {
                    cursorFlush = sw.Elapsed;
                    learned.NextOrdinal = (ulong)Interlocked.Read(ref _cursorOrdinal);
                    learned.TotalScanned = Interlocked.Read(ref _scanned);
                    learned.TotalMatched = Interlocked.Read(ref _matched);
                    SeedStore.SaveCursor(learned);
                    SeedStore.Flush();
                    seedCounter++;

                    // 主动让一下 CPU / 给 GC 机会回收，别把游戏挤死
                    try { GC.Collect(2, GCCollectionMode.Optimized, blocking: false); } catch { }
                }
            }
        }
        catch (Exception ex)
        {
            SeedStore.Error("搜索异常中止: " + ex);
        }
        finally
        {
            learned.NextOrdinal = (ulong)Interlocked.Read(ref _cursorOrdinal);
            learned.TotalScanned = Interlocked.Read(ref _scanned);
            learned.TotalMatched = Interlocked.Read(ref _matched);
            SeedStore.SaveCursor(learned);
            SeedStore.Flush();
            SeedStore.Log(
                $"======== 搜索结束 ======== 已扫 {Interlocked.Read(ref _scanned):N0}  " +
                $"本轮命中 {Interlocked.Read(ref _matched):N0}  " +
                $"储备 {SeedStore.ReserveCount}/{Conditions.ReserveCapacity}  " +
                $"托管内存 {GC.GetTotalMemory(false) / 1024.0 / 1024.0:F1} MB  " +
                $"({(_stoppedReserveFull ? "储备已满停手" : _pausedByTime ? "时间到暂停" : "被停止")})");
        }
    }

    static long _cursorOrdinal;

    /// <summary>因为地图生成抖动而被淘汰的候选数（诊断用）。</summary>
    static long _mapUnstable;

    static void ScanRange(ulong from, ulong to, Action<string> onMatch, CancellationToken ct)
    {
        // 在 char[] 上做 34 进制递增，避免每个种子都做一次除法取模
        var buf = SeedCodec.ToChars(from);

        for (var ord = from; ord < to; ord++)
        {
            // 每 16384 个种子检查一次是否该收手（别把整段扫完才停）
            if ((ord & 0x3FFF) == 0)
            {
                if (ct.IsCancellationRequested) return;
                if (SeedStore.IsReserveFull) return;
            }

            var seed = new string(buf);

            // ---------- 第①级：自研判定骨骰条件（①是否给骨骰 ②遗物 ③诅咒）----------
            //  ★ 完全不用 RolltheSpire2。已与 RT2 在 10 万个种子上对拍，一致率 100.0000%。
            if (!NeowPredict.IsMatch(seed, out var bonesEvidence))
            {
                Interlocked.Increment(ref _scanned);
                if (!SeedCodec.Increment(buf)) return;
                continue;
            }

            // ★ 双保险守卫：证据里必须真的出现「✓全中」才继续。
            //   之前 LastEvidence 是共享静态字段，跨线程被覆盖，导致结果文件里
            //   混进了一批「Evidence = Neow 未给骨骰」的假记录。
            //   现在证据按线程传递，再加这道守卫，任何竞态都不可能污染结果。
            if (bonesEvidence == null || !bonesEvidence.Contains("✓全中"))
            {
                SeedStore.Log($"⚠ 判定命中但证据不一致，已丢弃: {seed}  证据={bonesEvidence}");
                Interlocked.Increment(ref _scanned);
                if (!SeedCodec.Increment(buf)) return;
                continue;
            }
            Interlocked.Increment(ref _passedStage1);

            // ---------- 第②级：Act1 地图【出怪房间占比】----------
            //  ★ 最贵的一级（要真的把整张地图生成出来），所以放在最后。
            //    真实存档验证：地图结构 42/42 完全一致，类型 28/42 逐项一致，
            //    其余差 1~2 个格子（游戏运行中会改写个别格子的类型，见 docs/去RT2化方案.md）。
            var (mon, total, ratio) = ActMapPredict.CountRooms(seed);

            // ★ 稳定性判定：同一颗种子连算 3 次，只要有一次不一样就直接淘汰。
            //
            // 为什么必须有：实测这颗种子的地图生成**真的会抖动**
            // （日志里出现过 27/60 / 23/59 / 23/59 这种三连不一致）。
            // 抖动的种子没有"正确的占比"可言，交给玩家就是碰运气 —— 一律不要。
            {
                var b = ActMapPredict.CountRooms(seed);
                var c = ActMapPredict.CountRooms(seed);
                if (b.Monsters != mon || b.Total != total || c.Monsters != mon || c.Total != total)
                {
                    Interlocked.Increment(ref _mapUnstable);
                    SeedStore.Debug($"地图不稳定，已淘汰 {seed}: {mon}/{total}, {b.Monsters}/{b.Total}, {c.Monsters}/{c.Total}");
                    Interlocked.Increment(ref _scanned);
                    if (!SeedCodec.Increment(buf)) return;
                    continue;
                }
            }

            if (!Conditions.MonsterRatioPass(ratio))
            {
                Interlocked.Increment(ref _scanned);
                if (!SeedCodec.Increment(buf)) return;
                continue;
            }

            var percent = ratio * 100.0;
            var evidence = bonesEvidence + $" ；第一章怪物 {mon}/{total} = {percent:F1}%（≤{Conditions.MaxMonsterRatio * 100:F0}%）";

            var n = Interlocked.Increment(ref _matched);
            var hit = new SeedHit
            {
                Seed = seed,
                Ordinal = ord,
                Act1Monsters = mon,
                Act1Total = total,
                Act1MonsterPercent = percent,
                FoundAtUtc = DateTime.UtcNow.ToString("O"),
                Evidence = evidence,
            };
            SeedStore.AddHit(hit);
            SeedStore.Log($"★ 命中 #{n}: {seed}（第一章怪物 {mon}/{total} = {percent:F1}%）{bonesEvidence}");
            try { onMatch?.Invoke(seed); } catch { }

            Interlocked.Increment(ref _scanned);
            if (!SeedCodec.Increment(buf)) return;
        }
    }

    // ------------------------------------------------------------------
    // 自检：量一下第①级（骨骰判定）的速度。
    // ------------------------------------------------------------------

    public static string QuickSelfTest(ulong startOrdinal, int count)
    {
        var sb = new StringBuilder();
        var sw = Stopwatch.StartNew();
        var bones = 0L;
        var buf = SeedCodec.ToChars(startOrdinal);
        var done = 0;

        for (var i = 0; i < count; i++)
        {
            var seed = new string(buf);
            if (NeowPredict.IsMatch(seed, out _)) bones++;
            done++;
            if (!SeedCodec.Increment(buf)) break;
        }
        sw.Stop();

        var rate = done / Math.Max(sw.Elapsed.TotalSeconds, 1e-9);

        sb.AppendLine($"扫描 {done:N0} 个种子，耗时 {sw.Elapsed.TotalSeconds:F3}s → {rate:N0} 种子/秒（{rate / 1e6:F2} M/s）");
        sb.AppendLine($"  骨骰条件通过率 : {bones * 100.0 / Math.Max(done, 1):F4}%");

        return sb.ToString();
    }
}
