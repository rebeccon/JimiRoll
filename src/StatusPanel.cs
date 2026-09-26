using Godot;
using MegaCrit.Sts2.addons.mega_text;

namespace JimiRoll;

/// <summary>
/// P5：在「自定义开局」界面挂一个只读状态小面板。
///
/// ============================================================================
/// ⚠️ 为什么这么写（三条硬约束，全部来自 IL 实证 —— 别"顺手简化"）
/// ============================================================================
/// ① <c>MegaLabel._Ready()</c> 第一句就是
///    <c>MegaLabelHelper.AssertThemeFontOverride(this, ThemeConstants.Label.Font)</c>，
///    而它内部是 <c>if (!HasThemeFontOverride(name)) throw new InvalidOperationException(...)</c>。
///    <b>⇒ 裸 <c>new MegaLabel()</c> + <c>AddChild</c> 必崩（异常直接逃进 Godot 回调）。</b>
///    Godot 的 <c>AddChild</c> 在父节点已在场景树里时会<b>同步</b>调用子节点的 <c>_Ready</c>，
///    所以字体覆盖必须在 <c>AddChild</c> <b>之前</b>设好。
///
/// ② 字体从哪来：抄现场已有的 MegaLabel（<c>_disclaimer</c>，它既然活着就说明它自带覆盖）。
///    用 <c>GetThemeFontOverride(ThemeConstants.Label.Font)</c> 把 <c>Font</c> 对象取出来。
///
/// ③ <c>MegaLabel.RefreshFont()</c> → <c>FontControlUtils.ApplyLocaleFontSubstitution</c>
///    会自己按语言再调 <c>AddThemeFontOverride</c> 换成中日韩字体。它的每一步都有 guard：
///    editor 返回、测试模式返回、LocManager 空返回、不需要替换返回、替换字体为空返回。
///    <b>⇒ 它不会抛。而且它是英文游戏不会替换、中文游戏自动换 CJK 字体。</b>
///    所以我们只要"垫"一个合法字体进去过掉 ① 就够了。
///
/// ============================================================================
/// 安全策略（和 SeedInputPatch 同一套路）
/// ============================================================================
/// - 只<b>新增</b>节点，绝不改动游戏已有的任何节点；
/// - 整个流程包在 try/catch 里，异常绝不允许逃进 Godot 回调；
/// - <c>MouseFilter = Ignore</c>，不抢点击；
/// - 两级开关：settings.json 的 <c>ShowStatusPanel</c>，以及
///   <c>DISABLE_PANEL.txt</c>（建个空文件立刻停用，不用重编译）；
/// - 拿不到字体/拿不到父容器就<b>直接放弃</b>（没有面板 ≠ 游戏坏了）。
/// </summary>
internal static class StatusPanel
{
    /// <summary>★ 运行时保险：存在这个文件就不挂面板。</summary>
    public static string DisableFlag => Path.Combine(SeedStore.Dir, "DISABLE_PANEL.txt");

    const string NodeName = "JimiRoll_StatusPanel";
    const float RefreshSeconds = 1.0f;

    static MegaLabel _label;
    static Godot.Timer _timer;
    static Control _anchor;

    /// <summary>本局取走的那张种子（供面板显示）。</summary>
    static string _currentSeed;
    static int _currentQn;

    static bool _warnedNoFont;
    static bool _warnedDisabled;
    static bool _loggedOk;

    /// <summary>记录"这一局用的是哪个种子"。</summary>
    public static void RememberCurrentSeed(SeedHit hit)
    {
        try
        {
            _currentSeed = hit?.Seed;
            _currentQn = hit?.Act1UnknownRooms ?? 0;
        }
        catch { /* 纯展示，失败无所谓 */ }
    }

    /// <summary>
    /// 由 <see cref="SeedInputPatch"/> 在填完种子框之后调用（顺序很重要：
    /// 先取走、再挂面板，这样面板第一次显示的就是扣掉之后的真实数量）。
    /// <b>保证不抛异常。</b>
    /// </summary>
    public static void Attach(object screen)
    {
        try
        {
            if (!Settings.Current.ShowStatusPanel) return;
            if (screen is not Node screenNode) return;

            if (File.Exists(DisableFlag))
            {
                if (!_warnedDisabled)
                {
                    _warnedDisabled = true;
                    SeedStore.Log("[面板] 发现 DISABLE_PANEL.txt → 不挂状态面板。（删掉那个文件即可恢复）");
                }
                return;
            }

            // 已经挂过 → 只刷新文字 + 位置（_Ready / AfterInitialized 会各触发一次）
            if (IsAlive(_label) && _label.GetParent() == screenNode)
            {
                var a2 = SeedInputPatch.FindSeedInput(screenNode.GetType(), screenNode);
                if (a2 != null) _anchor = a2;
                Reposition();
                Refresh();
                return;
            }

            var anchor = SeedInputPatch.FindSeedInput(screenNode.GetType(), screenNode);
            if (anchor == null) return;
            _anchor = anchor;

            var src = FindFontSource(screenNode);
            if (src == null)
            {
                WarnNoFont("现场没有任何带字体覆盖的 MegaLabel —— 无法安全创建标签");
                return;
            }

            // Godot 4.5 的 Control 只有 HasThemeFontOverride，没有公开的 "GetThemeFontOverride"。
            // 但 GetThemeFont(name) 的查找顺序是「自身 override → Theme → 祖先」，
            // 所以对一个确实持有 override 的控件调用它，拿到的就是那个 override。
            var font = src.GetThemeFont(ThemeConstants.Label.Font)
                       ?? src.GetThemeDefaultFont();
            if (font == null)
            {
                WarnNoFont("从 " + src.Name + " 取不到任何字体");
                return;
            }

            var label = new MegaLabel { Name = NodeName };

            // ★★ 必须在 AddChild 之前 —— 见类注释 ①
            label.AddThemeFontOverride(ThemeConstants.Label.Font, font);
            label.AddThemeFontSizeOverride(ThemeConstants.Label.FontSize, Settings.Current.PanelFontSize);
            label.AddThemeColorOverride(ThemeConstants.Label.FontColor, new Color(1.00f, 0.93f, 0.60f));   // 淡金，和游戏 UI 区分开
            label.MouseFilter = Control.MouseFilterEnum.Ignore;
            label.ZIndex = 100;    // 保证画在最上层

            // 挂到屏幕本身（NSubmenu 是纯 Control，不是 Container）
            // —— 挂到 LineEdit 的父容器里会被容器当成"要参与排版的孩子"，可能挤坏原有布局，危险。
            screenNode.AddChild(label);

            _label = label;
            Reposition();          // 用 GlobalPosition 相对 LineEdit 定位：正下方 4px
            BuildText();
            AttachTimer(label);

            if (!_loggedOk)
            {
                _loggedOk = true;
                SeedStore.Log("[面板] ✓ 已在「自定义开局」界面挂上状态面板" +
                              $"（字号 {Settings.Current.PanelFontSize}，字体取自 {src.Name}）");
            }
        }
        catch (Exception ex)
        {
            WarnNoFont("挂载失败（已忽略，不影响游戏）: " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    // ==================================================================
    // 内容
    // ==================================================================

    /// <summary>刷新面板文字。</summary>
    public static void Refresh()
    {
        try
        {
            if (!IsAlive(_label)) return;
            BuildText();
        }
        catch { /* 展示失败不影响功能 */ }
    }

    static void BuildText()
    {
        if (!IsAlive(_label)) return;

        var cap = Conditions.ReserveCapacity;
        var n = FoundSeeds.Count;

        var sb = new System.Text.StringBuilder();
        sb.Append(Loc.T("基米roll种器", "JimiRoll"));
        sb.Append("　");
        sb.Append(Loc.T($"储备 {n}/{cap}", $"reserve {n}/{cap}"));

        if (n == 0)
        {
            sb.Append("　·　");
            sb.Append(Loc.T("正在补搜…", "searching…"));
        }
        else if (SearchDriver.Running)
        {
            sb.Append("　·　");
            sb.Append(Loc.T("搜索中 ", "searching "));
            sb.Append(Loc.Rate(SearchDriver.SeedsPerSecond));
        }
        else if (FoundSeeds.IsFull)
        {
            sb.Append("　·　");
            sb.Append(Loc.T("空闲待命", "idle"));
        }

        if (!string.IsNullOrEmpty(_currentSeed))
        {
            sb.Append("　｜　");
            sb.Append(Loc.T("本局 ", "this run "));
            sb.Append(_currentSeed);
            sb.Append(Loc.T($"（问号 {_currentQn}）", $" ({_currentQn} ?)"));
        }

        _label.Text = sb.ToString();
    }

    // ==================================================================
    // 定时刷新（搜索是后台线程在跑，储备数量会自己变）
    // ==================================================================

    static void AttachTimer(MegaLabel label)
    {
        try
        {
            if (IsAlive(_timer)) return;

            var timer = new Godot.Timer
            {
                Name = NodeName + "_Timer",
                WaitTime = RefreshSeconds,
                OneShot = false,
                Autostart = true,
            };
            timer.Timeout += Refresh;
            label.AddChild(timer);      // Timer 挂在 label 下，label 一没它跟着没
            _timer = timer;
        }
        catch { /* 不刷新也能用，只是数字会停在挂载那一刻 */ }
    }

    // ==================================================================
    // 工具
    // ==================================================================

    static bool IsAlive(GodotObject o)
    {
        try { return o != null && GodotObject.IsInstanceValid(o); }
        catch { return false; }
    }

    /// <summary>
    /// 贴着种子输入框正下方 4px。
    /// <c>_Ready</c> 时容器可能还没排完版（<c>Size</c> 还是 0），所以
    /// <c>AfterInitialized</c> 那次进来会再算一遍，位置就正了。
    /// </summary>
    static void Reposition()
    {
        try
        {
            if (!IsAlive(_label) || !IsAlive(_anchor)) return;
            _label.GlobalPosition = _anchor.GlobalPosition + new Vector2(0, _anchor.Size.Y + 4);
        }
        catch { /* 位置算不出来就保持原样 */ }
    }

    /// <summary>找一个"自带字体覆盖"的 MegaLabel 当字体来源。</summary>
    static Control FindFontSource(Node root)
    {
        try
        {
            foreach (var child in root.GetChildren())
            {
                if (child is MegaLabel ml && ml.HasThemeFontOverride(ThemeConstants.Label.Font))
                    return ml;

                var deep = FindFontSource(child);
                if (deep != null) return deep;
            }
        }
        catch { /* 某个节点不可访问就放弃这条路 */ }
        return null;
    }

    static void WarnNoFont(string why)
    {
        if (_warnedNoFont) return;
        _warnedNoFont = true;
        SeedStore.Warn("[面板] 未挂载状态面板：" + why +
                       "（不影响筛种和自动填种子；可把 settings.json 的 ShowStatusPanel 设成 false 关掉这条日志）");
    }
}
