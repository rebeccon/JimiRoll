using System.Diagnostics;
using System.Reflection;
using Godot;
using HarmonyLib;

namespace FastSeedRoll;

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
///     at FastSeedRoll.SeedInputPatch.Fill(...)
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
    /// 路径：<c>%APPDATA%\SlayTheSpire2\FastSeedRoll\DISABLE_SEED_PATCH.txt</c>
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
            var field = AccessTools.Field(t, "_seedInput")
                        ?? (t.BaseType != null ? AccessTools.Field(t.BaseType, "_seedInput") : null);

            if (field == null)
            {
                if (!_warnedField)
                {
                    _warnedField = true;
                    SeedStore.Log($"[种子框] [{where}] 找不到 _seedInput 字段，跳过（游戏可能改版了）");
                }
                return;   // 不设 _everHandled → 下次还能再试
            }

            if (field.GetValue(screen) is not LineEdit input)
            {
                if (!_warnedField)
                {
                    _warnedField = true;
                    SeedStore.Log($"[种子框] [{where}] _seedInput 还不是 LineEdit，跳过");
                }
                return;
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

            // ★ 取走队首（删除式）
            var hit = FoundSeeds.TakeNext();
            if (hit == null)
            {
                _everHandled = true;
                _lastHandled = now;
                return;
            }

            input.Text = hit.Seed;
            _lastFilled = hit.Seed;
            _everHandled = true;
            _lastHandled = now;
            _takeCount++;

            SeedStore.Log($"[种子框] 已填入 {hit.Seed}（问号 {hit.Act1UnknownRooms}）" +
                          $"—— 这是第 {_takeCount} 个取走的；储备池剩 {FoundSeeds.Count}/{Conditions.ReserveCapacity}");
        }
        catch (Exception ex)
        {
            try { SeedStore.Log("[种子框] 填充失败（已忽略，不影响游戏）: " + ex.GetType().Name + ": " + ex.Message); }
            catch { }
            try { _everHandled = true; _lastHandled = Clock.Elapsed; } catch { }
        }
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
        try { SeedInputPatch.Fill(instance, where); }
        catch { /* 吞掉一切，宁可功能不生效也绝不能让游戏出问题 */ }
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
