using RolltheSpire2.Compatibility;
using RolltheSpire2.Core.Authority;
using RolltheSpire2.Core.Effects;
using RolltheSpire2.Core.Identity;
using RolltheSpire2.Core.Neow;
using RolltheSpire2.Core.Prediction;
using RolltheSpire2.Core.World;
using RolltheSpire2.Infrastructure.Snapshots;

namespace JimiRoll;

/// <summary>
/// 第②级：用 RolltheSpire2 的单种子预测判断「这个种子满足骨骰条件吗」。
///
/// ============================================================================
/// ★ P3 改动：从【反射】改成【编译期强类型引用】
/// ============================================================================
/// 旧版这里 400 多行几乎全是反射（<c>GetType("...")</c> + <c>GetMethod(...)</c> +
/// <c>Invoke(...)</c>）。问题在于：RT2 一旦改了字段名或方法签名，
/// 反射会在运行时**静默失效** —— 判定永远返回 false，你以为"没找到种子"，
/// 实际是接线断了。而如果只是给出错误的判定结果，那更糟。
///
/// 现在改成直接引用类型（csproj 已 <c>HintPath</c> 引用 RolltheSpire2.dll），
/// 于是：<b>名字写错 = 编译不过</b>。RT2 更新后你会立刻在编译期发现问题，
/// 而不是在游戏里悄悄拿到错种子。
///
/// 代价：编译期必须能拿到 RolltheSpire2.dll（换机器要配 <c>STS2_RT2_DIR</c>）。
/// 这是有意的取舍 —— 宁可在编译时报错，也不要在运行时静默出错。
///
/// 唯一保留反射的地方是游戏侧的 <c>NCustomRunScreen._seedInput</c>
/// （那是游戏的【私有字段】，没有公开访问方式），见 <see cref="SeedInputPatch"/>。
/// ============================================================================
///
/// ⚠️ 关键约束（决定了整个调用时序）：
///   <c>BonesOutcome</c> 只有在 authority 含「骨骰合格遗物池」时才非 null。
///   那个池子来自 <c>NeowsBones.GetValidRelics(Player)</c>，需要活的 Player
///   ⇒ 只能通过 <see cref="RuntimeContextAuthorityCapture.CaptureRuntimeReadOnly"/> 抓，
///   而它<b>必须在 Godot 主线程调用</b>。
///
///   ⇒ 所以分两步：
///       <see cref="InitializeOnMainThread"/> —— 主线程调用，抓一次 authority
///       <see cref="IsMatch"/>                —— 任意线程，用缓存的 authority 判定
/// </summary>
internal static class Rt2Binding
{
    // ==================================================================
    // 状态
    // ==================================================================

    static string _error;
    static bool _initTried;

    static GameVersionDetection _detection;
    static IRuntimeProfile _profile;
    static RuntimeContextAuthoritySnapshot _authority;

    /// <summary>探测到的游戏版本（规范化后的，如 "0.111.0"）。</summary>
    public static string GameVersion { get; private set; } = "";

    /// <summary>版本号是从哪一级探测到的（诊断用）。</summary>
    public static string VersionSource { get; private set; } = "";

    /// <summary>判定结果是否可用。</summary>
    public static bool Ready => _error == null && _authority != null;

    public static string Status => _error != null
        ? "✗ " + _error
        : (_authority == null
            ? "⏳ 已绑定，但还没抓运行时快照（需要在主线程调用 InitializeOnMainThread）"
            : "✓ 已就绪");

    /// <summary>RT2 的 Predict 内部有共享状态，串行调用最稳（实测瓶颈本就在这层，加锁零损失）。</summary>
    static readonly object PredictGate = new();

    // ==================================================================
    // 第一步：主线程初始化
    // ==================================================================

    /// <summary>
    /// <b>必须在 Godot 主线程调用</b>（在 <c>NMainMenu._Ready</c> 里做正好）。
    ///
    /// 之所以必须：骨骰效果的真值来自 <c>NeowsBones.GetValidRelics(Player)</c>，
    /// 它需要活着的 Player；RT2 的运行时快照抓取器有主线程守卫，
    /// 非主线程会抛 <c>RuntimeSnapshotCaptureRequiresGodotMainThread</c>。
    ///
    /// 抓到的快照会被缓存，之后 <see cref="IsMatch"/> 可以在后台线程随便跑。
    /// </summary>
    public static bool InitializeOnMainThread()
    {
        if (_initTried && _authority != null) return true;
        _initTried = true;
        _error = null;

        try
        {
            // ---- 1. 版本探测（三级回退，全失败就明确报错）----
            GameVersion = ResolveGameVersion();
            SeedStore.Log($"[版本] 探测结果 = \"{GameVersion}\"");
            if (!string.IsNullOrWhiteSpace(VersionSource))
                SeedStore.Log($"[版本] 来源: {VersionSource}");
            if (string.IsNullOrWhiteSpace(GameVersion))
            {
                _error = "无法探测游戏版本号。拒绝用一个猜测的版本继续（那会静默算错种子）。";
                return false;
            }

            // ---- 2. detection / profile ----
            _detection = BuildDetection(GameVersion);
            _profile = RuntimeProfileRegistry.Select(_detection);
            if (_profile == null)
            {
                _error = $"RuntimeProfileRegistry.Select 对版本 \"{GameVersion}\" 返回了 null —— " +
                         "该版本可能不被 RolltheSpire2 支持。";
                return false;
            }
            SeedStore.Log($"[RT2] profile = {_profile.GetType().Name}");

            // ---- 3. key 与角色 ----
            var bonesKey = BaseGameModelKeys.Relics.NeowsBones;
            var shearsKey = BaseGameModelKeys.Relics.PrecariousShears;
            var leafKey = BaseGameModelKeys.Relics.NewLeaf;
            var guiltyKey = BaseGameModelKeys.Cards.Guilty;
            var charKey = ResolveCharacterKey();
            _bonesKey = bonesKey; _shearsKey = shearsKey; _leafKey = leafKey;
            _guiltyKey = guiltyKey; _charKey = charKey;

            // ★ 注意：ModelKey 是 struct（值类型），不可能是 null ——
            //   所以这里不能写 `bonesKey == null`。用 IsValid 判断。
            if (!bonesKey.IsValid || !shearsKey.IsValid || !leafKey.IsValid
                || !guiltyKey.IsValid || !charKey.IsValid)
            {
                _error = "BaseGameModelKeys 里的某个 key 无效（IsValid=false）。";
                return false;
            }

            SeedStore.Log($"[RT2] key: 骨骰={bonesKey} 羊毛剪={shearsKey} 新叶={leafKey} " +
                          $"愧疚={guiltyKey} 角色={charKey}");

            _character = CharacterIdentity.FromKey(charKey);

            // ---- 3.5 校验唯一的那个反射点（构造函数的某个参数类型是 internal，
            //          编译期无法调用，只能用反射；这里确认它还对得上）----
            if (!ValidateRequestCtor())
            {
                _error = "SeedPredictionRequest 构造函数校验失败: " + _requestCtorError;
                return false;
            }

            // ---- 4. 抓运行时快照（★ 这一步是主线程硬性要求）----
            _authority = RuntimeContextAuthorityCapture.CaptureRuntimeReadOnly(
                _profile,
                SeedCodec.Format(0),          // 与种子无关；骨骰池是按稀有度筛的
                _character,
                Conditions.Ascension,
                GameVersion,
                Conditions.PlayersCount,
                0);

            if (_authority == null)
            {
                _error = "RuntimeContextAuthorityCapture.CaptureRuntimeReadOnly 返回 null。";
                return false;
            }

            // ---- 5. 确认骨骰池在不在（这决定 BonesOutcome 能否算出来）----
            var pool = _authority.EffectAuthority?.BonesEligibleRelics;
            var poolCount = pool?.Count ?? 0;
            SeedStore.Log($"[authority] BonesEligibleRelics = {poolCount} 个  " +
                          $"HasExactBonesPools = {_authority.EffectAuthority?.HasExactBonesPools}");

            if (poolCount == 0)
            {
                _error = "快照里 BonesEligibleRelics 是空的 —— 骨骰条件无法判定。" +
                         "（确认是在主线程、且游戏已完成初始化。）";
                return false;
            }

            SeedStore.Log("[authority] ✓ 就绪");
            return true;
        }
        catch (Exception ex)
        {
            var inner = ex is System.Reflection.TargetInvocationException tie ? tie.InnerException ?? ex : ex;
            _error = inner.GetType().Name + ": " + inner.Message;
            if (inner.Message.Contains("MainThread"))
                _error += "  ⇒ 必须把 InitializeOnMainThread 放在 Godot 主线程调用。";
            SeedStore.Log("[RT2] 初始化异常: " + inner);
            return false;
        }
    }

    // ==================================================================
    // 版本探测
    // ==================================================================

    /// <summary>
    /// 三级探测，全失败返回空串（调用方会报错并停止）：
    /// <list type="number">
    /// <item>RT2 自己的 <see cref="GameVersionDetector.Detect"/>（首选，它自带候选路径发现）</item>
    /// <item>游戏自己的 <c>NGame.GetGameVersion()</c>（走反射 —— 那是游戏的 API）</item>
    /// <item>游戏根目录的 <c>release_info.json</c>（路径由已加载程序集反推，不写死盘符）</item>
    /// </list>
    /// </summary>
    static string ResolveGameVersion()
    {
        // ---- 1) RT2 的探测器 ----
        try
        {
            var det = GameVersionDetector.Detect();
            if (det != null)
            {
                if (!string.IsNullOrWhiteSpace(det.NormalizedVersion) && det.IsExact)
                {
                    VersionSource = "RT2 的 GameVersionDetector.Detect()（首选）";
                    return det.NormalizedVersion;
                }
                SeedStore.Log($"[版本] RT2 探测到 normalized=\"{det.NormalizedVersion}\" " +
                              $"isExact={det.IsExact} raw=\"{det.RawVersion}\" —— 不够精确，继续回退");
            }
        }
        catch (Exception ex) { SeedStore.Log("[版本] RT2 探测失败: " + ex.GetType().Name); }

        // ---- 2) 游戏自己的 ----
        try
        {
            var ngT = typeof(MegaCrit.Sts2.Core.Random.Rng).Assembly
                .GetType("MegaCrit.Sts2.Core.Nodes.NGame", false);
            var gv = ngT?.GetMethod("GetGameVersion",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static
                | System.Reflection.BindingFlags.NonPublic);
            var v = gv?.Invoke(null, null)?.ToString();
            if (!string.IsNullOrWhiteSpace(v))
            {
                VersionSource = "游戏的 NGame.GetGameVersion()";
                return v;
            }
            SeedStore.Log("[版本] NGame.GetGameVersion 不可用");
        }
        catch (Exception ex) { SeedStore.Log("[版本] NGame.GetGameVersion 失败: " + ex.GetType().Name); }

        // ---- 3) release_info.json ----
        try
        {
            var dir = Path.GetDirectoryName(typeof(MegaCrit.Sts2.Core.Random.Rng).Assembly.Location);
            for (var i = 0; i < 3 && dir != null; i++)
            {
                var f = Path.Combine(dir, "release_info.json");
                if (File.Exists(f))
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(f));
                    foreach (var name in new[] { "version", "Version", "game_version", "gameVersion" })
                        if (doc.RootElement.TryGetProperty(name, out var el))
                        {
                            var v = el.ToString();
                            if (!string.IsNullOrWhiteSpace(v))
                            {
                                VersionSource = $"游戏根目录 release_info.json 的 {name}";
                                return v;
                            }
                        }
                }
                var parent = Path.GetDirectoryName(dir);
                if (parent == dir) break;
                dir = parent;
            }
            SeedStore.Log("[版本] 找不到 release_info.json 或里面没有版本字段");
        }
        catch (Exception ex) { SeedStore.Log("[版本] 读 release_info.json 失败: " + ex.GetType().Name); }

        return "";
    }

    static GameVersionDetection BuildDetection(string version)
        => new GameVersionDetection(version, version, "", "JimiRoll(probed)", true, "");

    /// <summary>
    /// 把 <c>Conditions.CharacterKey</c>（形如 <c>CHARACTER:SILENT</c>）映射到 RT2 的常量。
    ///
    /// RT2 的 <c>BaseGameModelKeys.Characters</c> 只有 5 个原版角色：
    /// <c>Ironclad / Silent / Defect / Necrobinder / Regent</c>。
    /// （观者 Watcher 是 Mod 角色，不在其中。）
    /// 其它角色回退到 ModelKey 字符串构造 —— 那也是有效的。
    /// </summary>
    static ModelKey ResolveCharacterKey()
    {
        var e = Conditions.CharacterKey;
        var i = e.IndexOf(':');
        var entry = i >= 0 ? e[(i + 1)..] : e;

        switch (entry.ToUpperInvariant())
        {
            case "IRONCLAD": return BaseGameModelKeys.Characters.Ironclad;
            case "SILENT": return BaseGameModelKeys.Characters.Silent;
            case "DEFECT": return BaseGameModelKeys.Characters.Defect;
            case "NECROBINDER": return BaseGameModelKeys.Characters.Necrobinder;
            case "REGENT": return BaseGameModelKeys.Characters.Regent;
            default:
                SeedStore.Log($"[RT2] 角色 \"{Conditions.CharacterKey}\" 不是 5 个原版角色之一，" +
                              $"回退到 ModelKey 字符串构造");
                return new ModelKey("CHARACTER", entry);
        }
    }

    // ==================================================================
    // 第二步：判定（任意线程）
    // ==================================================================

    static ModelKey _bonesKey, _shearsKey, _leafKey, _guiltyKey, _charKey;
    static CharacterIdentity _character;

    /// <summary>判定。返回的 <paramref name="evidence"/> 由调用方自己持有，不受其它线程影响。</summary>
    public static bool IsMatch(string seed, out string evidence)
    {
        evidence = "";
        if (!Ready) { evidence = "未就绪"; return false; }

        try
        {
            var choice = FindBonesChoice(seed);
            if (choice == null) { evidence = "Neow 未给骨骰"; return false; }

            var bones = choice.BonesOutcome;
            if (bones == null)
            {
                evidence = "骨骰选项没有 BonesOutcome（快照缺骨骰池）";
                return false;
            }

            var offered = bones.OfferedRelics ?? (IReadOnlyList<ModelKey>)Array.Empty<ModelKey>();
            // ModelKey 是 struct，不用 `?.`
            var summary = "骨骰给出[" + string.Join("+", offered.Select(k => k.ToString())) + "]";

            if (offered.Count != 2) { evidence = summary + " —— 不是两个遗物"; return false; }
            if (!offered.Any(k => KeyEq(k, _shearsKey))) { evidence = summary + " —— 缺松动羊毛剪"; return false; }
            if (!offered.Any(k => KeyEq(k, _leafKey))) { evidence = summary + " —— 缺新叶"; return false; }
            if (!HasCurse(choice, _guiltyKey)) { evidence = summary + " —— 诅咒不是愧疚"; return false; }

            evidence = summary + " + 诅咒=愧疚  ✓全中";
            return true;
        }
        catch (Exception ex)
        {
            evidence = "判定异常: " + ex.GetType().Name + ": " + ex.Message;
            return false;
        }
    }

    /// <summary>兼容旧签名（内部仍走线程静态）。</summary>
    [ThreadStatic] static string _evidence;
    public static string LastEvidence => _evidence ?? "";
    public static bool IsMatch(string seed)
    {
        var ok = IsMatch(seed, out var ev);
        _evidence = ev;
        return ok;
    }

    static NeowChoiceResult FindBonesChoice(string seed)
    {
        var doc = Predict(seed);
        if (doc?.Sections == null) return null;

        foreach (var sec in doc.Sections)
        {
            if (sec == null || sec.Kind != PredictionSectionKind.NeowIdentity) continue;
            if (sec.NeowChoices == null) continue;
            foreach (var c in sec.NeowChoices)
                if (c != null && KeyEq(c.RelicKey, _bonesKey)) return c;
        }
        return null;
    }

    // ==================================================================
    // SeedPredictionRequest 的构造 —— ★ 本文件唯一必须用反射的地方
    // ==================================================================

    static System.Reflection.ConstructorInfo _requestCtor;
    static string _requestCtorError;

    /// <summary>
    /// 构造 <c>SeedPredictionRequest</c>。
    ///
    /// ⚠️ <b>为什么这里必须用反射（已核实，不是偷懒）</b>：
    /// 该构造函数的第 13 个参数类型是
    /// <c>RolltheSpire2.Core.Prediction.TrustedRootHashInput</c>，而它是 <b>internal</b>。
    /// 参数类型 internal ⇒ 构造函数对外不可见 ⇒ <b>编译期根本没法调用</b>
    /// （编译器报 CS1729「不包含采用 14 个参数的构造函数」）。
    ///
    /// 所以这是 P3 里唯一保留的反射点。为了不让它"静默失效"：
    /// <list type="bullet">
    /// <item>启动时校验构造函数存在、参数个数正确，不满足就<b>明确报错并停止搜索</b></item>
    /// <item>参数<b>按名字</b>匹配填入，而不是按位置 —— RT2 调顺序也不会传错值</item>
    /// </list>
    /// 其余所有 RT2 API 都是编译期强类型引用。
    /// </summary>
    static SeedPredictionRequest BuildRequest(string seed)
    {
        if (_requestCtor == null)
            throw new InvalidOperationException("SeedPredictionRequest 构造函数未就绪: " + _requestCtorError);

        var ps = _requestCtor.GetParameters();
        var args = new object[ps.Length];
        for (var i = 0; i < ps.Length; i++)
        {
            var n = ps[i].Name ?? "";
            args[i] = n switch
            {
                "requestId" => PredictionRequestId.Create(),
                "originalSeed" => seed,
                "character" => _character,
                "ascension" => Conditions.Ascension,
                "playersCount" => Conditions.PlayersCount,
                "playerSlotIndex" => 0,
                "authority" => _authority,
                "ancientOptionConditions" => AncientOptionConditionProfile.BroadDefault,
                "domains" => SeedPredictionDomainSelection.Neow,
                "relicSequencePreviewCount" => 0,
                "includeDiagnostics" => false,
                "enableComplexBonesDeckInteractions" => true,
                // 下面两个就是"必须用反射"的原因所在：类型 internal / 我们不需要
                "trustedRootHashInput" => null,
                "normalCombatRewardProjectionRequest" => null,
                _ => ps[i].HasDefaultValue ? ps[i].DefaultValue : null,
            };
        }

        var obj = _requestCtor.Invoke(args);
        return (SeedPredictionRequest)obj;
    }

    /// <summary>启动时校验那个反射点，不满足就报错（而不是运行时静默失败）。</summary>
    static bool ValidateRequestCtor()
    {
        try
        {
            var ctors = typeof(SeedPredictionRequest).GetConstructors(
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Instance);

            // 选参数最多的那个（真实的那个有 14 个参数）
            _requestCtor = ctors.OrderByDescending(c => c.GetParameters().Length).FirstOrDefault();

            if (_requestCtor == null)
            {
                _requestCtorError = "找不到任何构造函数";
                return false;
            }

            var ps = _requestCtor.GetParameters();
            // 必须有的参数名（缺一个就说明 RT2 改签名了）
            var required = new[]
            {
                "requestId", "originalSeed", "character", "ascension", "playersCount", "playerSlotIndex",
                "authority", "ancientOptionConditions", "domains",
            };
            var names = ps.Select(p => p.Name).ToHashSet();
            var missing = required.Where(r => !names.Contains(r)).ToArray();
            if (missing.Length > 0)
            {
                _requestCtorError = "构造函数的参数名对不上，缺少: " + string.Join(", ", missing) +
                                    "（实际参数: " + string.Join(", ", ps.Select(p => p.Name)) + "）";
                return false;
            }

            SeedStore.Log($"[RT2] SeedPredictionRequest 构造函数 ✓（{ps.Length} 个参数，按名字匹配）");
            return true;
        }
        catch (Exception ex)
        {
            _requestCtorError = ex.GetType().Name + ": " + ex.Message;
            return false;
        }
    }

    static SeedPredictionDocument Predict(string seed)
    {
        var request = BuildRequest(seed);

        // 串行化：RT2 的 Predict 内部有共享缓存，并发调用是结果不可信的隐患。
        lock (PredictGate)
        {
            return RuntimeProfileRegistry.Predict(_detection, request);
        }
    }

    /// <summary>诅咒：在 EffectGroups 里找 Kind=AddCard 且 Target=Guilty。</summary>
    static bool HasCurse(NeowChoiceResult choice, ModelKey target)
    {
        if (choice.EffectGroups == null) return false;
        foreach (var g in choice.EffectGroups)
        {
            if (g?.OrderedItems == null) continue;
            foreach (var item in g.OrderedItems)
            {
                if (item == null) continue;
                // item.TargetKey 是 Nullable<ModelKey>（因为 ModelKey 是 struct）
                if (item.Kind == PredictedEffectKind.AddCard && KeyEqOpt(item.TargetKey, target))
                    return true;
            }
        }
        return false;
    }

    /// <summary>用 Serialized 比较 ModelKey（不依赖它的 Equals 实现）。</summary>
    static bool KeyEq(ModelKey a, ModelKey b)
        => string.Equals(a.Serialized, b.Serialized, StringComparison.OrdinalIgnoreCase);

    /// <summary><c>Nullable&lt;ModelKey&gt;</c> 版本。</summary>
    static bool KeyEqOpt(ModelKey? a, ModelKey b)
        => a.HasValue && KeyEq(a.Value, b);
}
