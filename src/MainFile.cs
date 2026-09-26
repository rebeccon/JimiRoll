using System.Reflection;
using HarmonyLib;

namespace JimiRoll;

/// <summary>
/// Mod 入口 + 开关。
///
/// 设计要点：
/// <list type="bullet">
/// <item><b>不注册任何 UI</b> —— 因此不会踩到 RolltheSpire2 的
///       <c>SearchPageController.CancelForPanelClose()</c>（那个方法会在面板关闭时取消搜索）。</item>
/// <item><b>开关是"活"的</b> —— 每次回到主菜单都会重新检查开关文件，
///       所以你可以随时开/关，不需要重启游戏。</item>
/// <item><b>默认不开</b> —— 绝不自动占用 CPU、绝不自动改你的游戏状态。</item>
/// </list>
/// </summary>
public static class MainFile
{
    /// <summary>
    /// ★ 正式版默认【自动运行】—— 装了就干活，不需要任何开关文件。
    ///
    /// 之所以可以默认开：储备池满了就停手（<see cref="Conditions.ReserveCapacity"/>），
    /// 所以空闲时它完全不占资源。只有"用掉一个 → 掉到 14"之后才会补搜一下。
    ///
    /// 这个文件是【唯一的例外】—— 存在它就暂停搜索（给排障/不想让它跑时用）：
    /// <c>%APPDATA%\SlayTheSpire2\JimiRoll\DISABLE_SEARCH.txt</c>
    /// </summary>
    public static string DisableSearchFile => Path.Combine(SeedStore.Dir, "DISABLE_SEARCH.txt");

    /// <summary>（兼容旧版）之前用的开关文件，正式版不再需要，存在也只当作"启用"。</summary>
    public static string LegacySwitchFile => Path.Combine(SeedStore.Dir, "ON.txt");

    static bool _setupDone;
    static bool _setupOk;

    /// <summary>
    /// 由 Harmony 补丁在主菜单就绪时调用（<see cref="MainMenuReadyPatch"/>）。可重复调用。
    /// </summary>
    public static void OnGameReady()
    {
        SeedStore.EnsureDir();

        // ---------- 一次性初始化 ----------
        if (!_setupDone)
        {
            _setupDone = true;
            _setupOk = DoOneTimeSetup();
        }

        // ---------- 每次都检查是否需要补搜（这样能热生效）----------
        CheckSearch();
    }

    static bool DoOneTimeSetup()
    {
        SeedStore.Log("==================== JimiRoll 启动 ====================");

        // ---- 先加载设置：后面的日志和逻辑都要用它 ----
        Settings.Load();
        SeedStore.Log("设置: " + Settings.Describe());
        SeedStore.Log("  配置文件: " + Settings.FilePath);
        SeedStore.Log("条件: " + Conditions.Describe());
        SeedStore.Log("运行模式: 默认自动运行（储备池不满就补搜，满了就空闲待命）");
        SeedStore.Log("  唯一的暂停方式: 建一个空文件 " + DisableSearchFile);

        // ---- 0. 读取并整理储备池（与开关无关，总是要做）----
        try
        {
            SeedStore.Flush();                          // 把上次没落盘的状态写出去
            var (dupes, overflow) = SeedStore.TrimToCapacity();   // 去重 + 裁到上限
            if (dupes > 0)
                SeedStore.Log($"储备池发现 {dupes} 个重复种子，已去掉");
            if (overflow > 0)
                SeedStore.Log($"储备池超过 {Conditions.ReserveCapacity} 个，已把多出来的 {overflow} 个移出归档");

            // ★ 强制写一次：即使储备池内容没变，也要保证 seeds.txt 是最新的。
            //   否则（Flush 在 _dirty=false 时提前返回）seeds.txt 会一直是旧的。
            SeedStore.FlushForce();

            FoundSeeds.LogReserve();
            FoundSeeds.CopyToClipboard();
        }
        catch (Exception ex)
        {
            SeedStore.Warn("整理储备池失败（不影响其它功能）: " + ex.Message);
        }

        // ---- 1. 自检 ----
        var problems = new List<string>();
        try { SeedCodec.SelfTest(problems); }
        catch (Exception ex) { problems.Add("[SeedCodec] 自检抛异常: " + ex); }

        try { GameInterop.SelfTest(problems); }
        catch (Exception ex) { problems.Add("[GameInterop] 自检抛异常: " + ex); }

        SeedStore.Log("种子哈希: " + GameInterop.VerifyReport);

        // ---- 1.5 第②级预测器：主线程初始化（要访问 ModelDb）----
        //  ★ 必须在主线程：NeowPredict 要读 ModelDb.CardPool<CurseCardPool>()。
        //    本方法由 NMainMenu._Ready 的后置补丁调用，那正是主线程。
        var predictOk = NeowPredict.Initialize();
        SeedStore.Log("自研判定状态: " + NeowPredict.Status);
        if (!predictOk)
        {
            SeedStore.Error("✗ 自研判定没初始化成功 —— 拒绝搜索（不给错种子）");
            return false;
        }

        // ---- 1.6 自研预测器自检：用游戏内实测过的已知命中种子 ----
        //  ★ 这一条是摘掉 RT2 之后最重要的保险：14 个地面真值种子必须全部判为命中。
        //    任何一个不过，都说明 RNG 流 / 池子顺序 / 前置消耗的推导有问题。
        try { NeowPredict.SelfTest(problems); }
        catch (Exception ex) { problems.Add("[自研] 自检抛异常: " + ex); }

        if (problems.Count > 0)
        {
            SeedStore.Error("✗✗✗ 自检失败，为避免给出错误种子，搜索不会启动：");
            foreach (var p in problems) SeedStore.Log("  - " + p);
            return false;
        }
        SeedStore.Log("✓ 自检通过（种子哈希与游戏一致）");

        // ---- 2. 第①级冒烟测试（问号数预筛，纯自研）----
        try
        {
            var smoke = SearchDriver.QuickSelfTest(0, 200_000);
            SeedStore.Log("第①级冒烟测试：\n" + smoke);
        }
        catch (Exception ex)
        {
            SeedStore.Error("✗ 第①级冒烟测试失败: " + ex);
            return false;
        }

        // 第②级（骨骰判定）已经在 1.5 / 1.6 初始化并自检过了。
        SeedStore.Log("✓✓ 全部就绪 —— 储备池不满时会自动补搜（不依赖任何第三方模组）");
        return true;
    }

    /// <summary>检查是否该搜索 —— 默认就是搜，只有暂停文件或储备已满才不搜。</summary>
    static void CheckSearch()
    {
        // ---------- 唯一的暂停条件 ----------
        if (File.Exists(DisableSearchFile))
        {
            if (SearchDriver.Running)
            {
                SeedStore.Log("发现 DISABLE_SEARCH.txt → 停止搜索");
                SearchDriver.Stop();
            }
            SearchDriver.ResetRunState();
            _fullWarned = false;
            return;
        }

        if (!_setupOk)
        {
            if (!_switchWarned)
            {
                _switchWarned = true;
                SeedStore.Warn("⚠ 初始化没成功，无法开始搜索。请看上面的诊断。");
            }
            return;
        }

        if (SearchDriver.Running) return;   // 已经在跑，别重复启动

        // ---------- 储备池够 15 个 → 不搜索（这才是常态） ----------
        if (SeedStore.IsReserveFull)
        {
            if (!_fullWarned)
            {
                _fullWarned = true;
                SeedStore.Log($"储备池已满（{SeedStore.ReserveCount}/{Conditions.ReserveCapacity}）→ 不搜索，空闲待命。");
                SeedStore.Log("  用掉一个（进「自定义开局」取走）之后，储备掉到 14，下次回主菜单会自动补上。");
            }
            return;
        }

        // ---------- 储备池不满 → 补搜 ----------
        _fullWarned = false;
        var why = SearchDriver.PausedByTime ? "（上一轮时间到暂停，继续）" : "";
        SeedStore.Log($"储备池 {SeedStore.ReserveCount}/{Conditions.ReserveCapacity} 不足 → 自动补搜{why}" +
                      $"（单次上限 {Conditions.MaxRunMinutes} 分钟）");

        SearchDriver.Start(seed => SeedStore.Log("★ 新种子已入池: " + seed));
    }

    static bool _switchWarned;
    static bool _fullWarned;

    public static void OnGameShutdown()
    {
        try
        {
            SearchDriver.Stop();
            SeedStore.Flush();
            SeedStore.Log("JimiRoll 退出");
        }
        catch { }
    }
}

/// <summary>
/// 主菜单就绪时做初始化 / 检查开关。
///
/// 用 <c>NMainMenu._Ready</c> 是因为：
/// <list type="bullet">
/// <item>RolltheSpire2 自己也挂这个点（日志里的 <c>minimalUiPatchTarget=NMainMenu._Ready</c>），
///       说明那时 Mod 程序集都已加载、<c>ModelDb</c> 也可用。</item>
/// <item>它跑在 <b>Godot 主线程</b> —— 这是抓运行时快照的硬性要求。</item>
/// <item>每次回到主菜单都会重新触发，所以开关可以热切换。</item>
/// </list>
///
/// ⚠️ 这个补丁<b>绝不能抛异常</b> —— 异常会逃进 Godot 的回调、把界面打崩。
/// （这个教训是真踩出来的：种子框补丁曾因 TimeSpan 溢出把「自定义开局」打不开。）
/// 所以：TargetMethod 失败返回 null，Postfix 里再包一层 try/catch。
///
/// ⚠️ Mod 加载器（<c>ModManager.TryLoadMod</c>）会自己 <c>Harmony.PatchAll(assembly)</c>，
/// 所以我们<b>不需要也不应该</b>再手动 PatchAll 一次。
/// </summary>
[HarmonyPatch]
internal static class MainMenuReadyPatch
{
    static MethodBase TargetMethod()
    {
        try
        {
            var t = AccessTools.TypeByName("MegaCrit.Sts2.Core.Nodes.Screens.MainMenu.NMainMenu");
            return t == null ? null : AccessTools.Method(t, "_Ready");
        }
        catch { return null; }
    }

    [HarmonyPostfix]
    static void Postfix()
    {
        // 连错误日志都再包一层 —— 万一 Log 本身出问题也不能外泄
        try { MainFile.OnGameReady(); }
        catch (Exception ex)
        {
            try { SeedStore.Log("初始化失败: " + ex.GetType().Name + ": " + ex.Message); } catch { }
        }
    }
}
