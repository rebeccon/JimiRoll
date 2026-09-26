using System.Reflection;
using System.Text;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Random;

namespace FastSeedRoll;

/// <summary>
/// 与游戏侧交互的入口。
///
/// 设计原则：<b>绑定失败立刻抛异常，绝不 swallow 后给出错误结果</b> ——
/// 筛种器一旦静默算错，用户会拿到错误的种子。
///
/// 为什么只用【一个】反射点：
///   <c>Rng</c> 已核实为 public（探针 <c>vis</c> 确认），所以直接引用即可，零反射开销；
///   唯一不确定的是 <c>StringHelper.GetDeterministicHashCode</c>，
///   它走反射 + 自研 XxHash64 对拍，对拍不过就回退到游戏实现（慢但一定对）。
/// </summary>
internal static class GameInterop
{
    // ★ P0 改动：原来这里写死了 @"D:\steam\...\sts2.dll"，现在完全删掉。
    //   理由：这个 mod 跑在游戏进程【内部】，sts2.dll 早就加载好了 ——
    //   我们只需要 typeof(Rng).Assembly（见 Bind），根本不需要知道游戏装在哪。
    //   写死盘符会导致换机器 / Steam 库换盘 / 创意工坊编号变化时直接失效。

    public const string Act1MapDomain = "act_1_map";

    /// <summary>问号数量的取值范围（来自 StandardRandomUnknownCount 的 NextGaussianInt(12,1,10,14)）。</summary>
    public const int UnknownRoomsMin = 10;
    public const int UnknownRoomsMax = 14;

    static bool _ready;
    static string _failure;
    static readonly object Gate = new();

    static MethodInfo _getDeterministicHashCode;   // StringHelper.GetDeterministicHashCode(string) -> ulong
    static ulong? _domainHash;                     // GetDeterministicHashCode("act_1_map")

    public static bool Ready { get { Ensure(); return _ready; } }
    public static string Failure { get { Ensure(); return _failure; } }

    static void Ensure()
    {
        if (_ready || _failure != null) return;
        lock (Gate)
        {
            if (_ready || _failure != null) return;
            try { Bind(); _ready = true; }
            catch (Exception ex) { _failure = ex.GetType().Name + ": " + ex.Message; }
        }
    }

    static void Bind()
    {
        var helper = typeof(Rng).Assembly.GetType("MegaCrit.Sts2.Core.Helpers.StringHelper", false)
            ?? throw new InvalidOperationException("找不到 MegaCrit.Sts2.Core.Helpers.StringHelper");

        _getDeterministicHashCode = helper.GetMethod(
            "GetDeterministicHashCode",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
            null, new[] { typeof(string) }, null)
            ?? throw new InvalidOperationException("找不到 StringHelper.GetDeterministicHashCode(string)");

        if (_getDeterministicHashCode.ReturnType != typeof(ulong))
            throw new InvalidOperationException(
                $"GetDeterministicHashCode 返回 {_getDeterministicHashCode.ReturnType}，期望 UInt64");

        // 立刻取一次域名哈希，顺便验证绑定可用
        _domainHash = (ulong)_getDeterministicHashCode.Invoke(null, new object[] { Act1MapDomain });
    }

    // ------------------------------------------------------------------
    // 种子串 -> RunRngSet.Seed
    // ------------------------------------------------------------------

    /// <summary>游戏原始实现：<c>StringHelper.GetDeterministicHashCode(seed)</c> = <c>RunRngSet.Seed</c>。</summary>
    public static ulong RunRngSeedViaGame(string seed)
    {
        Ensure();
        if (!_ready) throw new InvalidOperationException("GameInterop 未就绪: " + _failure);
        return (ulong)_getDeterministicHashCode.Invoke(null, new object[] { seed });
    }

    /// <summary>
    /// 取种子对应的 <c>RunRngSet.Seed</c>。
    /// 自研 XxHash64 对拍通过时走快路径，否则透明回退到游戏实现。
    /// </summary>
    public static ulong RunRngSeed(string seed)
        => _useManagedHash ? XxHash64.HashToUInt64(Encoding.UTF8.GetBytes(seed)) : RunRngSeedViaGame(seed);

    static bool _useManagedHash;
    static bool _verifyDone;
    public static bool ManagedHashEnabled => _useManagedHash;
    public static string VerifyReport { get; private set; } = "(未对拍)";

    /// <summary>
    /// ★ 关键对拍：确认自研 XxHash64 与游戏 <c>GetDeterministicHashCode</c> 逐值一致。
    /// 不一致时自动禁用快路径（回退到游戏实现），并在 <see cref="VerifyReport"/> 里说明原因。
    /// </summary>
    public static bool VerifyAgainstGame(int samples = 2000, ulong startOrdinal = 1234567890123UL)
    {
        Ensure();
        if (!_ready) { VerifyReport = "GameInterop 未就绪: " + _failure; return false; }
        if (_verifyDone) return _useManagedHash;

        var mismatches = 0;
        var firstBad = "";
        void Check(string s)
        {
            var game = RunRngSeedViaGame(s);
            var mine = XxHash64.HashToUInt64(Encoding.UTF8.GetBytes(s));
            if (game != mine)
            {
                if (mismatches == 0) firstBad = $"\"{s}\": 游戏={game} 自研={mine}";
                mismatches++;
            }
        }

        for (ulong i = 0; i < (ulong)samples; i++)
            Check(SeedCodec.Format((startOrdinal + i) % SeedCodec.SpaceSize));

        // 边界与非 ASCII 也过一遍（真实种子只会是 ASCII，但能暴露编码错误）
        foreach (var s in new[] { "", "0", "A", "ZZZZZZZZZZZZ", "000000000000", Act1MapDomain, "中文" })
            Check(s);

        _useManagedHash = mismatches == 0;
        _verifyDone = true;
        VerifyReport = _useManagedHash
            ? $"✓ 自研 XxHash64 与游戏 GetDeterministicHashCode 在 {samples + 7} 个样本上完全一致 —— 启用快路径"
            : $"✗ 有 {mismatches} 处不一致（首个：{firstBad}）—— 已回退到游戏实现（慢但正确）";
        return _useManagedHash;
    }

    // ------------------------------------------------------------------
    // act_1_map
    // ------------------------------------------------------------------

    /// <summary>
    /// <c>new Rng(RunRngSeed(seed), "act_1_map")</c> 内部传给 <c>MegaRandom</c> 的种子：
    /// <c>RunRngSet.Seed + GetDeterministicHashCode("act_1_map")</c>。
    /// 依据：<c>Rng(ulong seed, string name) =&gt; MegaRandom(seed + Hash(name))</c>（反汇编）。
    /// </summary>
    public static ulong Act1MapMegaSeed(string seed)
    {
        Ensure();
        if (!_ready) throw new InvalidOperationException("GameInterop 未就绪: " + _failure);
        return RunRngSeed(seed) + _domainHash.Value;
    }

    /// <summary>
    /// 第一章问号房间数量。
    ///
    /// <b>直接调用游戏自己的 public 方法</b> <c>MapPointTypeCounts.StandardRandomUnknownCount(Rng)</c>
    /// （已用探针 <c>proto</c> 核实为 public static），而不是硬编码它内部的高斯参数 ——
    /// 这样万一游戏改了参数，我们自动跟随。
    ///
    /// 该方法内部就是 <c>rng.NextGaussianInt(mean:12, stdDev:1, min:10, max:14)</c>，
    /// 实测取值恒在 [10,14]，问号=14 的概率约 6.1%。
    /// </summary>
    public static int Act1UnknownRooms(string seed)
        => MapPointTypeCounts.StandardRandomUnknownCount(new Rng(Act1MapMegaSeed(seed), Act1MapDomain));

    // ------------------------------------------------------------------
    // 自检
    // ------------------------------------------------------------------
    public static void SelfTest(List<string> problems)
    {
        if (!Ready)
        {
            problems.Add($"[GameInterop] 绑定失败: {Failure}");
            return;
        }

        // 1) 种子哈希对拍（最关键）
        VerifyAgainstGame();
        if (!ManagedHashEnabled)
            problems.Add("[GameInterop] " + VerifyReport);

        // 2) 问号数必须落在 [10,14]
        var rng = new Random(7);
        for (var i = 0; i < 3000; i++)
        {
            var seed = SeedCodec.Format((ulong)rng.NextInt64(0, long.MaxValue) % SeedCodec.SpaceSize);
            var qn = Act1UnknownRooms(seed);
            if (qn < UnknownRoomsMin || qn > UnknownRoomsMax)
            {
                problems.Add($"[GameInterop] 问号数越界: seed={seed} qn={qn}（应在 [{UnknownRoomsMin},{UnknownRoomsMax}]）");
                break;
            }
        }

        // 3) 同一个种子必须给出同一个结果（确定性）
        var probe = "PJJBSGHQ1N2Q";
        var a = Act1UnknownRooms(probe);
        var b = Act1UnknownRooms(probe);
        if (a != b) problems.Add($"[GameInterop] 不确定: {probe} 得到 {a} 和 {b}");
    }
}

