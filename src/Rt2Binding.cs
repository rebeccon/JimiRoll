using System.Reflection;
using System.Text;
using MegaCrit.Sts2.Core.Random;

namespace JimiRoll;

/// <summary>
/// 第②级：用 RolltheSpire2 的单种子预测判断「这个种子满足骨骰条件吗」。
///
/// ============================================================================
/// 本实现全部基于【实测过的 API】，不再靠猜
/// ============================================================================
/// 已用运行时探针逐项验证（见 _tools/notes/JimiRoll-findings.md）：
///
///   ModelKey = RolltheSpire2.Core.Identity.ModelKey（**RT2 自己的类型**，不在 sts2 里）
///               ctor (string category, string entry)，格式 "RELIC:NEOWS_BONES"
///   更有用：RolltheSpire2.Core.Identity.BaseGameModelKeys+Relics / +Cards / +Characters
///           里已有全部现成静态字段（NeowsBones / PrecariousShears / NewLeaf / Guilty）
///           ⇒ 完全不需要拼字符串
///
///   RuntimeProfileRegistry.Select(GameVersionDetection)      → Beta111Profile
///   GameVersionDetection(raw, normalized, branch, evidence, isExact, failure)  6 参 public ctor
///   CharacterIdentity.FromKey(ModelKey)
///   AncientOptionConditionProfile.BroadDefault               ← 现成默认值
///   RuntimeProfileRegistry.Predict(detection, SeedPredictionRequest) → SeedPredictionDocument
///
///   document.Sections[Kind == NeowIdentity].NeowChoices[i]
///       .RelicKey                                   ← Neow 给的遗物
///       .BonesOutcome.OfferedRelics                 ← ★ 骨骰给出的两个遗物
///       .EffectGroups[].OrderedItems[]              ← Kind=AddCard, TargetKey=CARD:GUILTY
///
/// ⚠️⚠️ 关键约束（实测发现，决定了整个架构）：
///   <c>BonesOutcome</c> 只有在 authority 含有「骨骰合格遗物池」时才非 null，
///   否则选项上会带警告 EvidenceCode = <c>bones-eligible-relic-pool-required</c>。
///   而这个池子来自 <c>NeowsBones.GetValidRelics(Player)</c>，需要活的 Player
///   ⇒ 只能通过 <c>CaptureRuntimeReadOnly</c> 抓，而它
///   <b>必须在 Godot 主线程调用</b>（否则抛 RuntimeSnapshotCaptureRequiresGodotMainThread）。
///
///   ⇒ 所以本类分成两步：
///       <see cref="InitializeOnMainThread"/> —— 主线程调用，抓一次 authority
///       <see cref="IsMatch"/>                —— 任意线程，用缓存 authority 判定
/// ============================================================================
/// </summary>
internal static class Rt2Binding
{
    /// <summary>
    /// ⚠️ <b>线程静态</b>：之前是普通静态字段，被多个 worker 线程共享 ——
    /// 线程 A 判失败写了「Neow 未给骨骰」，线程 B 判成功后来读，读到的是 A 的那句话，
    /// 结果 seeds_found.json 里混进了一批「Evidence = Neow 未给骨骰」的假记录。
    /// 改成线程静态后每个线程各看各的。
    /// </summary>
    [ThreadStatic] static string _evidence;

    public static string LastEvidence => _evidence ?? "";

    /// <summary>单线程专用的 Predicting 锁 —— RT2 的 Predict 内部有共享状态，串行调用最稳。</summary>
    static readonly object PredictGate = new();

    static Assembly _rt2;
    static string _error;
    static bool _initTried;
    static readonly StringBuilder Diag = new();

    // 解析出来的类型
    static Type _modelKey, _detectionT, _registryT, _requestT, _requestIdT, _domainSelT, _ancientT;
    static Type _docT, _choiceT, _bonesOutcomeT, _effectGroupT, _effectT, _profileIdT, _charIdT;

    // 条件用的 key 与角色
    static object _bonesKey, _shearsKey, _leafKey, _guiltyKey, _charKey;

    static object _detection, _profile, _authority, _neowDomain, _broadDefault;
    static ConstructorInfo _requestCtor;
    static MethodInfo _predict, _charFromKey, _requestIdCreate;

    /// <summary>können we 判定？</summary>
    public static bool Ready => _error == null && _predict != null && _authority != null;

    public static string Status => _error != null
        ? "✗ " + _error
        : (_authority == null
            ? "⏳ 已绑定，但还没抓运行时快照（需要在主线程调用 InitializeOnMainThread）"
            : "✓ 已就绪");

    public static string Diagnostics => Diag.ToString();

    // ==================================================================
    // 第一步：绑定（可在任意线程）
    // ==================================================================

    static void BindTypes()
    {
        if (_rt2 != null) return;

        // ★ P0 改动：原来这里会兜底 LoadFrom 写死的创意工坊路径（含工坊编号 3755166888），
        //   现在删掉 —— RT2 由游戏 Mod 加载器载入（我们在 manifest 里声明了依赖），
        //   必然已经在已加载程序集列表里。写死路径会在换机器 / 工坊编号变化时失效。
        //   找不到就明确报错，让问题暴露出来，而不是去猜路径。
        _rt2 = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "RolltheSpire2");
        if (_rt2 == null) throw new InvalidOperationException(
            "RolltheSpire2 程序集未加载。请确认 manifest 里的 dependencies 写了 RolltheSpire2，" +
            "并且在游戏的 mod 列表里它是启用状态。");

        Diag.AppendLine($"[RT2] {_rt2.Location}");

        Type Req(string n)
        {
            var t = _rt2.GetType(n, false);
            if (t == null)
            {
                var ns = n[..n.LastIndexOf('.')];
                var sib = string.Join(", ", _rt2.GetTypes().Where(x => x.Namespace == ns).Select(x => x.Name).Take(15));
                Diag.AppendLine($"[RT2] ✗ 缺 {n}（同命名空间: {sib}）");
                throw new InvalidOperationException($"RT2 缺类型 {n}");
            }
            return t;
        }

        _modelKey = Req("RolltheSpire2.Core.Identity.ModelKey");
        _charIdT = Req("RolltheSpire2.Core.Identity.CharacterIdentity");
        _detectionT = Req("RolltheSpire2.Compatibility.GameVersionDetection");
        _registryT = Req("RolltheSpire2.Compatibility.RuntimeProfileRegistry");
        _profileIdT = Req("RolltheSpire2.Compatibility.RuntimeProfileId");
        _requestT = Req("RolltheSpire2.Core.Prediction.SeedPredictionRequest");
        _requestIdT = Req("RolltheSpire2.Core.Prediction.PredictionRequestId");
        _domainSelT = Req("RolltheSpire2.Core.Prediction.SeedPredictionDomainSelection");
        _ancientT = Req("RolltheSpire2.Core.World.AncientOptionConditionProfile");
        _docT = Req("RolltheSpire2.Core.Prediction.SeedPredictionDocument");
        _choiceT = Req("RolltheSpire2.Core.Neow.NeowChoiceResult");
        _bonesOutcomeT = Req("RolltheSpire2.Core.Effects.BonesOutcomeAnalysis");
        _effectGroupT = Req("RolltheSpire2.Core.Effects.PredictedEffectGroup");
        _effectT = Req("RolltheSpire2.Core.Effects.PredictedEffect");

        Diag.AppendLine("[RT2] 类型全部解析完成");

        // ---- 条件用的 ModelKey：直接取 RT2 自带的常量 ----
        object K(string nested, string field)
        {
            var t = _rt2.GetType($"RolltheSpire2.Core.Identity.BaseGameModelKeys+{nested}", false);
            var f = t?.GetField(field, BindingFlags.Public | BindingFlags.Static);
            if (f == null)
                Diag.AppendLine($"[RT2] ✗ BaseGameModelKeys+{nested}.{field} 不存在");
            return f?.GetValue(null);
        }

        _bonesKey = K("Relics", "NeowsBones");
        _shearsKey = K("Relics", "PrecariousShears");
        _leafKey = K("Relics", "NewLeaf");
        _guiltyKey = K("Cards", "Guilty");
        _charKey = K("Characters", Character());

        Diag.AppendLine($"[RT2] key: 骨骰={_bonesKey} 羊毛剪={_shearsKey} 新叶={_leafKey} " +
                        $"愧疚={_guiltyKey} 角色={_charKey}");

        if (_bonesKey == null || _shearsKey == null || _leafKey == null || _guiltyKey == null || _charKey == null)
            throw new InvalidOperationException(
                "BaseGameModelKeys 里缺少必要的 key（见诊断）—— RT2 可能改了这些常量的名字或位置。");

        // ---- 版本探测：★ P0 改动 ----
        // 原来这里写死 "0.111.0"。那是很危险的：游戏升到 0.112 之后，
        // 代码会照着【旧版本】的规则构造运行时快照 —— 结果可能静默算错，
        // 比崩溃还糟糕（给你错的种子）。
        //
        // 现在改成用 RT2 自己的探测器 GameVersionDetector.Detect()（已核实 public static），
        // 它有自己的候选路径发现逻辑（BuildCandidatePaths）。拿不到就依次回退，
        // 全都失败就【明确报错并停止】，绝不猜。
        GameVersion = ResolveGameVersion();
        Diag.AppendLine($"[版本] 探测结果 = \"{GameVersion}\"");
        // ★ 成功也要打日志 —— 之前只进 Diag 缓冲，成功时看不到，
        //   导致"版本到底探测出什么"这个问题没法从日志确认。
        SeedStore.Log($"[版本] 探测结果 = \"{GameVersion}\"");
        if (!string.IsNullOrWhiteSpace(_lastVersionProbe))
            SeedStore.Log($"[版本] 来源: {_lastVersionProbe}");
        if (string.IsNullOrWhiteSpace(GameVersion))
            throw new InvalidOperationException(
                "无法探测游戏版本号。拒绝用一个猜测的版本继续（那会静默算错种子）。" +
                "详见诊断日志。");

        _detection = BuildDetection(GameVersion);
        Diag.AppendLine($"[版本] detection = {_detection}");

        _predict = _registryT.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .First(m => m.Name == "Predict" && m.GetParameters().Length == 2);

        _profile = _registryT.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .First(m => m.Name == "Select" && m.GetParameters().Length == 1
                        && m.GetParameters()[0].ParameterType == _detectionT)
            .Invoke(null, new[] { _detection });
        Diag.AppendLine($"[RT2] profile = {_profile?.GetType().Name}");

        _charFromKey = _charIdT.GetMethod("FromKey", BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException("CharacterIdentity.FromKey 不存在");

        _requestIdCreate = _requestIdT.GetMethod("Create", BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException("PredictionRequestId.Create 不存在");

        _broadDefault = _ancientT.GetProperty("BroadDefault", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);

        _neowDomain = Enum.Parse(_domainSelT, "Neow");
        _requestCtor = _requestT.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .OrderByDescending(c => c.GetParameters().Length).First();
        Diag.AppendLine($"[RT2] SeedPredictionRequest 参数数 = {_requestCtor.GetParameters().Length}");
    }

    const string _rt2_marker = "JimiRoll";

    /// <summary>
    /// 探测到的游戏版本（规范化后的，如 "0.111.0"）。
    /// ★ 不再是 const —— 由 <see cref="ResolveGameVersion"/> 在绑定时探测填入。
    /// </summary>
    static string GameVersion = "";

    /// <summary>版本号是从哪一级探测到的（打日志用）。</summary>
    static string _lastVersionProbe = "";

    /// <summary>
    /// 探测游戏版本。三级尝试，全失败返回空串（调用方会报错并停止）：
    /// <list type="number">
    /// <item>RT2 自己的 <c>GameVersionDetector.Detect()</c>（首选，它自带候选路径发现）</item>
    /// <item>游戏自己的 <c>NGame.GetGameVersion()</c></item>
    /// <item>读游戏根目录的 <c>release_info.json</c>（由已加载程序集位置反推，不写死盘符）</item>
    /// </list>
    /// </summary>
    static string ResolveGameVersion()
    {
        // ---- 1) RT2 的探测器 ----
        try
        {
            var detT = _rt2!.GetType("RolltheSpire2.Compatibility.GameVersionDetector", false);
            var detect = detT?.GetMethod("Detect", BindingFlags.Public | BindingFlags.Static);
            if (detect != null)
            {
                var det = detect.Invoke(null, null);
                if (det != null)
                {
                    var norm = Reflect.GetMember(det, "NormalizedVersion")?.ToString();
                    var exact = Reflect.GetMember(det, "IsExact");
                    Diag.AppendLine($"[版本] RT2 探测: normalized=\"{norm}\" isExact={exact} raw={Reflect.GetMember(det, "RawVersion")}");
                    if (!string.IsNullOrWhiteSpace(norm) && (exact is not bool b || b))
                    {
                        _lastVersionProbe = "RT2 的 GameVersionDetector.Detect()（首选）";
                        return norm;
                    }
                }
            }
            else Diag.AppendLine("[版本] 找不到 GameVersionDetector.Detect，跳过");
        }
        catch (Exception ex) { Diag.AppendLine("[版本] RT2 探测失败: " + ex.GetType().Name); }

        // ---- 2) 游戏自己的 ----
        try
        {
            var ngT = _rt2!.GetType("MegaCrit.Sts2.Core.Nodes.NGame") ??
                      typeof(MegaCrit.Sts2.Core.Random.Rng).Assembly.GetType("MegaCrit.Sts2.Core.Nodes.NGame", false);
            var gv = ngT?.GetMethod("GetGameVersion", BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
            if (gv != null)
            {
                var v = gv.Invoke(null, null)?.ToString();
                Diag.AppendLine($"[版本] NGame.GetGameVersion() = \"{v}\"");
                if (!string.IsNullOrWhiteSpace(v))
                {
                    _lastVersionProbe = "游戏的 NGame.GetGameVersion()";
                    return v;
                }
            }
            else Diag.AppendLine("[版本] 找不到 NGame.GetGameVersion，跳过");
        }
        catch (Exception ex) { Diag.AppendLine("[版本] NGame.GetGameVersion 失败: " + ex.GetType().Name); }

        // ---- 3) release_info.json（路径由已加载程序集反推，不写死盘符） ----
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
                            Diag.AppendLine($"[版本] release_info.json 的 {name} = \"{v}\"  ({f})");
                            if (!string.IsNullOrWhiteSpace(v))
                            {
                                _lastVersionProbe = "游戏根目录的 release_info.json";
                                return v;
                            }
                        }
                }
                var parent = Path.GetDirectoryName(dir);
                if (parent == dir) break;
                dir = parent;
            }
            Diag.AppendLine("[版本] 找不到 release_info.json 或里面没有版本字段");
        }
        catch (Exception ex) { Diag.AppendLine("[版本] 读 release_info.json 失败: " + ex.GetType().Name); }

        return "";
    }

    /// <summary>用探测到的版本号构造 RT2 的 GameVersionDetection（优先让 RT2 自己造）。</summary>
    static object BuildDetection(string version)
    {
        // 优先：直接用 RT2 探测出来的那个 detection 对象（信息最全，带它的 Evidence/IsExact）
        try
        {
            var detT = _rt2!.GetType("RolltheSpire2.Compatibility.GameVersionDetector", false);
            var detect = detT?.GetMethod("Detect", BindingFlags.Public | BindingFlags.Static);
            var det = detect?.Invoke(null, null);
            if (det != null && det.GetType() == _detectionT) return det;
        }
        catch { }

        // 回退：自己按 6 参构造造一个（字段名已用探针核实）
        return _detectionT.GetConstructor(new[]
            { typeof(string), typeof(string), typeof(string), typeof(string), typeof(bool), typeof(string) })!
            .Invoke(new object[] { version, version, "", "JimiRoll(probed)", true, "" });
    }

    static string Character()
    {
        // Conditions.CharacterKey 形如 "CHARACTER:SILENT"，转成 BaseGameModelKeys+Characters 的字段名
        var e = Conditions.CharacterKey;
        var i = e.IndexOf(':');
        var entry = i >= 0 ? e[(i + 1)..] : e;
        var parts = entry.Split(new[] { '_' }, StringSplitOptions.RemoveEmptyEntries);
        return string.Concat(parts.Select(p => char.ToUpperInvariant(p[0]) + p[1..].ToLowerInvariant()));
    }

    // ==================================================================
    // 第二步：主线程抓运行时快照
    // ==================================================================

    /// <summary>
    /// <b>必须在 Godot 主线程调用</b>（在 <c>NMainMenu._Ready</c> 里做正好）。
    ///
    /// 之所以必须：骨骰效果的真值来自 <c>NeowsBones.GetValidRelics(Player)</c>，
    /// 它需要活着的 Player；而 RT2 的运行时快照抓取器有主线程守卫，
    /// 非主线程会抛 <c>RuntimeSnapshotCaptureRequiresGodotMainThread</c>。
    ///
    /// 抓到的快照会被缓存下来，之后 <see cref="IsMatch"/> 可以在后台线程随便跑。
    /// </summary>
    public static bool InitializeOnMainThread()
    {
        if (_initTried && _authority != null) return true;
        _initTried = true;
        _error = null;

        try
        {
            BindTypes();

            var capT = _rt2!.GetType("RolltheSpire2.Infrastructure.Snapshots.RuntimeContextAuthorityCapture")
                ?? throw new InvalidOperationException("找不到 RuntimeContextAuthorityCapture");

            var character = _charFromKey.Invoke(null, new[] { _charKey });

            // 首选 CaptureRuntimeReadOnly（含骨骰池，但必须在主线程）
            var readOnly = capT.GetMethod("CaptureRuntimeReadOnly", BindingFlags.Public | BindingFlags.Static);
            if (readOnly != null)
            {
                var ps = readOnly.GetParameters();
                var args = new object[ps.Length];
                for (var i = 0; i < ps.Length; i++)
                {
                    var n = ps[i].Name!;
                    args[i] = n switch
                    {
                        "profile" => _profile,
                        "rawSeed" => SeedCodec.Format(0),      // 这里与种子无关；骨骰池是按稀有度筛的
                        "character" => character,
                        "ascension" => Conditions.Ascension,
                        "gameVersion" => GameVersion,
                        "playersCount" => Conditions.PlayersCount,
                        "playerSlotIndex" => 0,
                        _ => ps[i].HasDefaultValue ? ps[i].DefaultValue : null,
                    };
                }

                try
                {
                    _authority = readOnly.Invoke(null, args);
                    Diag.AppendLine("[authority] 用 CaptureRuntimeReadOnly 成功（含骨骰池）");
                }
                catch (TargetInvocationException tie)
                {
                    var inner = tie.InnerException ?? tie;
                    Diag.AppendLine($"[authority] CaptureRuntimeReadOnly 失败: {inner.GetType().Name}: {inner.Message}");
                    _error = "抓运行时快照失败（是否不在主线程？）: " + inner.Message;
                    if (_error.Contains("MainThread"))
                        _error += "\n  ⇒ 把 Rt2Binding.InitializeOnMainThread() 放到 Godot 主线程调用（例如 NMainMenu._Ready）。";
                    return false;
                }
            }

            if (_authority == null)
            {
                _error = "拿不到 RuntimeContextAuthoritySnapshot";
                return false;
            }

            // 确认骨骰池在不在（这决定 BonesOutcome 能不能算出来）
            var ea = Reflect.GetMember(_authority, "EffectAuthority");
            var bonesPool = ea != null ? Reflect.GetMember(ea, "BonesEligibleRelics") : null;
            var hasExact = ea != null ? Reflect.GetMember(ea, "HasExactBonesPools", false) : false;
            var poolCount = bonesPool is System.Collections.IEnumerable en
                ? en.Cast<object>().Count(x => x != null) : 0;

            Diag.AppendLine($"[authority] BonesEligibleRelics = {poolCount} 个  HasExactBonesPools = {hasExact}");

            if (poolCount == 0)
            {
                _error = "快照里 BonesEligibleRelics 是空的 —— 骨骰条件无法判定。" +
                         "（骨骰池来自 NeowsBones.GetValidRelics(Player)，需要活着的 Player；" +
                         "确认是在主线程、且游戏已完成初始化。）";
                return false;
            }

            Diag.AppendLine("[authority] ✓ 就绪");
            return true;
        }
        catch (Exception ex)
        {
            _error = ex.GetType().Name + ": " + ex.Message;
            Diag.AppendLine("[RT2] 初始化异常: " + ex);
            return false;
        }
    }

    // ==================================================================
    // 第三步：判定（任意线程）
    // ==================================================================

    /// <summary>判定。返回的 <paramref name="evidence"/> 由调用方自己持有，不受其它线程影响。</summary>
    public static bool IsMatch(string seed, out string evidence)
    {
        evidence = "";
        if (!Ready) { evidence = "未就绪"; return false; }

        try
        {
            var doc = Predict(seed);
            if (doc == null) { evidence = "Predict 返回 null"; return false; }

            var choice = FindBonesChoice(doc);
            if (choice == null) { evidence = "Neow 未给骨骰"; return false; }

            var bones = Reflect.GetMember(choice, "BonesOutcome");
            if (bones == null)
            {
                evidence = "骨骰选项没有 BonesOutcome（快照缺骨骰池）";
                return false;
            }

            var offered = (Reflect.GetMember(bones, "OfferedRelics") as System.Collections.IEnumerable)?
                .Cast<object>().Where(x => x != null).ToList() ?? new List<object>();

            var summary = "骨骰给出[" + string.Join("+", offered.Select(x => x.ToString())) + "]";

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

    /// <summary>兼容旧签名（内部仍走线程静态 _evidence）。</summary>
    public static bool IsMatch(string seed)
    {
        var ok = IsMatch(seed, out var ev);
        _evidence = ev;
        return ok;
    }

    static object Predict(string seed)
    {
        var character = _charFromKey.Invoke(null, new[] { _charKey });

        var ps = _requestCtor.GetParameters();
        var args = new object[ps.Length];
        for (var i = 0; i < ps.Length; i++)
        {
            var n = ps[i].Name!;
            args[i] = n switch
            {
                "requestId" => _requestIdCreate.Invoke(null, null),
                "originalSeed" => seed,
                "character" => character,
                "ascension" => Conditions.Ascension,
                "playersCount" => Conditions.PlayersCount,
                "playerSlotIndex" => 0,
                "authority" => _authority,
                "ancientOptionConditions" => _broadDefault,
                "domains" => _neowDomain,
                "relicSequencePreviewCount" => 0,
                "includeDiagnostics" => false,
                "enableComplexBonesDeckInteractions" => true,
                _ => ps[i].HasDefaultValue ? ps[i].DefaultValue : null,
            };
        }

        // ★ 串行化：RT2 的 Predict 内部有共享缓存/状态，多线程并发调用是结果不可信的隐患。
        //   实测瓶颈本来就在这一层（流水线整体 46 万种子/秒 ≈ 单线程 Predict 的吞吐），
        //   所以加锁【不损失任何性能】，却能把整类并发风险直接消掉。
        lock (PredictGate)
        {
            return _predict.Invoke(null, new[] { _detection, _requestCtor.Invoke(args) });
        }
    }

    /// <summary>在 NeowIdentity 段里找 RelicKey == 骨骰 的那个选项。</summary>
    static object FindBonesChoice(object doc)
    {
        if (Reflect.GetMember(doc, "Sections") is not System.Collections.IEnumerable sections) return null;

        foreach (var sec in sections)
        {
            if (sec == null) continue;
            if (Reflect.GetMember(sec, "Kind")?.ToString() != "NeowIdentity") continue;

            if (Reflect.GetMember(sec, "NeowChoices") is not System.Collections.IEnumerable choices) continue;
            foreach (var c in choices)
            {
                if (c == null) continue;
                if (KeyEq(Reflect.GetMember(c, "RelicKey"), _bonesKey)) return c;
            }
        }
        return null;
    }

    /// <summary>诅咒：在 EffectGroups 里找 Kind=AddCard 且 Target=Guilty。</summary>
    static bool HasCurse(object choice, object target)
    {
        if (Reflect.GetMember(choice, "EffectGroups") is not System.Collections.IEnumerable groups) return false;
        foreach (var g in groups)
        {
            if (g == null) continue;
            if (Reflect.GetMember(g, "OrderedItems") is not System.Collections.IEnumerable items) continue;
            foreach (var it in items)
            {
                if (it == null) continue;
                if (Reflect.GetMember(it, "Kind")?.ToString() == "AddCard"
                    && KeyEq(Reflect.GetMember(it, "TargetKey"), target))
                    return true;
            }
        }
        return false;
    }

    /// <summary>用 Serialized 比较 ModelKey（不依赖它的 Equals 实现）。</summary>
    static bool KeyEq(object a, object b)
    {
        if (a == null || b == null) return false;
        var sa = Reflect.GetMember(a, "Serialized")?.ToString() ?? a.ToString();
        var sb = Reflect.GetMember(b, "Serialized")?.ToString() ?? b.ToString();
        return string.Equals(sa, sb, StringComparison.OrdinalIgnoreCase);
    }
}
