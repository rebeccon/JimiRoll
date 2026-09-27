using System.Diagnostics;
using System.Reflection;
using Godot;
using HarmonyLib;

namespace JimiRoll;

/// <summary>
/// 打开「自定义开局」界面时：
/// <list type="number">
/// <item><b>取走</b>储备池队首的那个种子（取走即从储备池删除并归档）</item>
/// <item>填进种子输入框</item>
/// </list>
///
/// 依据（探针实测 + 游戏日志确认）：
///   <c>MegaCrit.Sts2.Core.Nodes.Screens.CustomRun.NCustomRunScreen</c>
///   有私有字段 <c>LineEdit _seedInput</c>；<c>AfterInitialized()</c> 由
///   <c>InitializeSingleplayer()</c> 调用，<c>_Ready()</c> 也会触发。
///
/// ============================================================================
/// ⚠️ 这一版修了一个把界面打崩的严重 bug（务必别改回去）
/// ============================================================================
/// 之前去重窗口写成 <c>if (now - _lastHandled &lt; Window) return;</c>，
/// 而 <c>_lastHandled</c> 初始值是 <c>TimeSpan.MinValue</c>（约 -1067 万年）——
/// 拿它做减法 <b>直接 OverflowException</b>。更糟的是那一行在 <c>try</c> 外面，
/// 异常逃进 Godot 的回调，导致「自定义开局」界面直接打不开：
///
///   ERROR: System.OverflowException: TimeSpan overflowed because the duration is too long.
///     at JimiRoll.SeedInputPatch.Fill(...)
///     at ...NCustomRunScreen.InitializeSingleplayer()
///
/// 教训有两条，都已落实：
///   ① 去重不能靠"极小初值 + 减法"，改用 <c>_everHandled</c> 标志 + 从 0 起算。
///   ② <b>整个方法体都包在 try/catch 里</b>，连早退判断也不例外；
///      Harmony 的 Postfix 外面再包一层。绝不允许任何异常逃进游戏回调。
/// ============================================================================
/// </summary>
internal static class SeedInputPatch
{
    /// <summary>总开关。改成 false 就不再自动填种子框（也就不会取走种子）。</summary>
    public const bool Enabled = true;

    /// <summary>
    /// ★ 运行时保险：存在这个文件就跳过本补丁（不用重新编译）。
    /// 路径：<c>%APPDATA%\SlayTheSpire2\JimiRoll\DISABLE_SEED_PATCH.txt</c>
    /// 万一这个补丁又把「自定义开局」搞坏了，建个这个文件就能立刻恢复。
    /// </summary>
    public static string DisableFlag =>
        Path.Combine(SeedStore.Dir, "DISABLE_SEED_PATCH.txt");

    /// <summary>去重时间窗口：这个间隔内的重复触发视为同一次进入。</summary>
    static readonly TimeSpan DedupeWindow = TimeSpan.FromSeconds(2);

    static readonly Stopwatch Clock = Stopwatch.StartNew();
    /// <summary>注意：必须从 0 开始，且配合 <see cref="_everHandled"/> 使用 —— 别再塞 MinValue。</summary>
    static TimeSpan _lastHandled = TimeSpan.Zero;
    static bool _everHandled;
    static string _lastFilled;
    static int _takeCount;

    /// <summary>一次填框最多复核几个种子（复核不过就丢掉换下一个）。</summary>
    const int VerifiedTakeLimit = 12;
    static bool _warnedField;
    static bool _warnedEmpty;
    static bool _warnedDisabled;

    /// <summary>由 Harmony 补丁调用。<b>保证不抛异常。</b></summary>
    public static void Fill(object screen, string where)
    {
        // ★ 整段都在 try 里 —— 连早退判断也不例外。
        //   任何异常都必须被吞掉：逃进 Godot 回调会把界面打崩。
        try
        {
            if (!Enabled || screen == null) return;

            // 运行时保险：存在文件就整体跳过
            if (File.Exists(DisableFlag))
            {
                if (!_warnedDisabled)
                {
                    _warnedDisabled = true;
                    SeedStore.Log("[种子框] 发现 DISABLE_SEED_PATCH.txt → 已停用自动填种子。" +
                                  "（删掉那个文件即可恢复）");
                }
                return;
            }

            // 时间窗口去重：_Ready 与 AfterInitialized 会在同一瞬间各触发一次
            var now = Clock.Elapsed;
            if (_everHandled && now - _lastHandled < DedupeWindow) return;

            if (!FoundSeeds.Any)
            {
                if (!_warnedEmpty)
                {
                    _warnedEmpty = true;
                    SeedStore.Log("[种子框] 储备池是空的 —— 等搜索补上种子后，下次进入就会自动填。");
                }
                _everHandled = true;
                _lastHandled = now;
                return;
            }

            var t = screen.GetType();
            var input = FindSeedInput(t, screen);
            if (input == null)
            {
                if (!_warnedField)
                {
                    _warnedField = true;
                    SeedStore.Log($"[种子框] [{where}] 找不到 _seedInput 字段，跳过（游戏可能改版了）");
                }
                return;   // 不设 _everHandled → 下次还能再试
            }

            // 只覆盖「空」或「我们自己上次填的」；其它情况说明是你自己打的，绝不能动
            var current = input.Text ?? "";
            if (current.Length > 0 && current != _lastFilled)
            {
                _everHandled = true;
                _lastHandled = now;
                SeedStore.Log($"[种子框] 输入框里已有你手填的「{current}」，不覆盖");
                return;
            }

            // ★★ 取走队首（删除式）—— 并且在【主线程】上当场复核一遍地图统计。
            //
            // 为什么必须复核：地图生成会被"对象身份哈希"之类运行期状态影响，
            // 后台搜索线程算出来的值**偶尔**和主线程算出来的不一致（实测 14:43 那次就踩到）。
            // 玩家这一局的地图是在主线程生成的，所以只有**主线程算的值**才作数。
            // 复核不过的直接丢掉（归档为 rejected），换下一个 —— 这叫"宁可少给，也绝不给错种子"。
            SeedHit hit = null;
            for (var attempt = 0; attempt < VerifiedTakeLimit; attempt++)
            {
                var cand = FoundSeeds.TakeNext();
                if (cand == null) break;

                // 连算两次：抖动过的种子两次会不一样，直接淘汰（宁可少给，也绝不给错种子）
                var (vm, vt, vr) = ActMapPredict.CountRooms(cand.Seed);
                var (vm2, vt2, vr2) = ActMapPredict.CountRooms(cand.Seed);
                if (vm2 != vm || vt2 != vt)
                {
                    SeedStore.Warn($"[种子框] ✗ 丢弃 {cand.Seed}：主线程两次复核不一致（{vm}/{vt} vs {vm2}/{vt2}）→ 地图抖动，不可信");
                    continue;
                }
                if (vt > 0 && Conditions.MonsterRatioPass(vr))
                {
                    if (vm != cand.Act1Monsters || vt != cand.Act1Total)
                        SeedStore.Log($"[种子框] {cand.Seed} 主线程复核 = {vm}/{vt} = {vr * 100:F1}%（后台记录 {cand.Act1Monsters}/{cand.Act1Total}）；" +
                                      $"两边数值不同但都合格，以复核为准");
                    cand.Act1Monsters = vm;
                    cand.Act1Total = vt;
                    cand.Act1MonsterPercent = Math.Round(vr * 100.0, 2);
                    hit = cand;
                    break;
                }

                SeedStore.Warn($"[种子框] ✗ 丢弃 {cand.Seed}：主线程复核 {vm}/{vt} = {vr * 100:F1}% 不满足 ≤{Conditions.MaxMonsterRatio * 100:F0}%");
            }

            if (hit == null)
            {
                _everHandled = true;
                _lastHandled = now;
                return;
            }

            // 记下来给状态面板显示（P5）
            StatusPanel.RememberCurrentSeed(hit);

            if (hit.Act1MonsterPercent > Conditions.MaxMonsterRatio * 100.0)
                SeedStore.Warn($"[种子框] {hit.Seed} 复核后仍然超标（{hit.Act1MonsterPercent:F1}%），请手动留意");

            input.Text = hit.Seed;
            _lastFilled = hit.Seed;
            _everHandled = true;
            _lastHandled = now;
            _takeCount++;

            SeedStore.Log($"[种子框] 已填入 {hit.Seed}（第一章怪物 {hit.Act1Monsters}/{hit.Act1Total} = {hit.Act1MonsterPercent:F1}%）" +
                          $"—— 这是第 {_takeCount} 个取走的；储备池剩 {FoundSeeds.Count}/{Conditions.ReserveCapacity}");
        }
        catch (Exception ex)
        {
            try { SeedStore.Warn("[种子框] 填充失败（已忽略，不影响游戏）: " + ex.GetType().Name + ": " + ex.Message); }
            catch { }
            try { _everHandled = true; _lastHandled = Clock.Elapsed; } catch { }
        }
    }

    /// <summary>
    /// 拿到种子输入框。先找私有字段 <c>_seedInput</c>（含基类），
    /// 找不到就退而在子节点里翻一个 <c>LineEdit</c> —— 这样游戏改字段名也不会整个功能失灵。
    /// </summary>
    public static LineEdit FindSeedInput(Type t, object screen)
    {
        try
        {
            var field = AccessTools.Field(t, "_seedInput")
                        ?? (t.BaseType != null ? AccessTools.Field(t.BaseType, "_seedInput") : null);

            if (field?.GetValue(screen) is LineEdit byField) return byField;

            // 退路：深度遍历找第一个 LineEdit
            if (screen is Node node) return FindLineEditDeep(node);
        }
        catch { /* 找不到就当没有 */ }
        return null;
    }

    static LineEdit FindLineEditDeep(Node root)
    {
        try
        {
            foreach (var child in root.GetChildren())
            {
                if (child is LineEdit le) return le;
                var deep = FindLineEditDeep(child);
                if (deep != null) return deep;
            }
        }
        catch { }
        return null;
    }
}

/// <summary>
/// Harmony 补丁的公共入口 —— 再包一层 try/catch。
/// 这是最后一道防线：即使 <see cref="SeedInputPatch.Fill"/> 内部出了意外，
/// 也绝不让异常传回 Godot 的回调（那会把界面打崩）。
/// </summary>
internal static class SafePatchEntry
{
    public static void Run(object instance, string where)
    {
        // ① 填种子（可能取走储备池队首）
        try { SeedInputPatch.Fill(instance, where); }
        catch { /* 吞掉一切，宁可功能不生效也绝不能让游戏出问题 */ }

        // ② 挂状态面板。放在这里而不是 Fill 里面，是为了"储备池为空"时也照样显示面板。
        try { StatusPanel.Attach(instance); }
        catch { }
    }
}

/// <summary><c>NCustomRunScreen._Ready</c> 后置补丁。</summary>
[HarmonyPatch]
internal static class CustomRunReadyPatch
{
    static MethodBase TargetMethod()
    {
        try
        {
            var t = AccessTools.TypeByName("MegaCrit.Sts2.Core.Nodes.Screens.CustomRun.NCustomRunScreen");
            return t == null ? null : AccessTools.Method(t, "_Ready");
        }
        catch { return null; }
    }

    [HarmonyPostfix]
    static void Postfix(object __instance) => SafePatchEntry.Run(__instance, "_Ready");
}

/// <summary>
/// <c>NCustomRunScreen.AfterInitialized</c> 后置补丁 —— 由 <c>InitializeSingleplayer()</c> 调用。
/// 时间窗口会挡住与 <c>_Ready</c> 的重复取种子。
/// </summary>
[HarmonyPatch]
internal static class CustomRunAfterInitializedPatch
{
    static MethodBase TargetMethod()
    {
        try
        {
            var t = AccessTools.TypeByName("MegaCrit.Sts2.Core.Nodes.Screens.CustomRun.NCustomRunScreen");
            return t == null ? null : AccessTools.Method(t, "AfterInitialized");
        }
        catch { return null; }
    }

    [HarmonyPostfix]
    static void Postfix(object __instance) => SafePatchEntry.Run(__instance, "AfterInitialized");
}
