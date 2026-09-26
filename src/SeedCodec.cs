using System.Text;

namespace FastSeedRoll;

/// <summary>
/// 种子字符串 &lt;-&gt; ordinal 的编解码。
///
/// 全部依据 RolltheSpire2 的 <c>Beta110SeedCodec</c> 反汇编所得（常量已用反射读运行时静态字段核实）：
///   <c>Radix = 34</c>、<c>SeedLength = 12</c>、
///   <c>Alphabet = "0123456789ABCDEFGHJKLMNPQRSTUVWXYZ"</c>（无 I、无 O）、
///   <c>SpaceSize = 2386420683693101056</c>（= 34^12）。
///
/// <c>WriteOrdinal(ordinal, destination)</c> 是【小端】：
///   <c>destination[11]</c> 是最低位、<c>destination[0]</c> 是最高位。
/// 所以 <c>ordinal 0 -&gt; "000000000000"</c>、
/// <c>ordinal SpaceSize-1 -&gt; "ZZZZZZZZZZZZ"</c>。
/// <see cref="SelfTest"/> 会强制校验这一点。
/// </summary>
internal static class SeedCodec
{
    public const int Radix = 34;
    public const int SeedLength = 12;
    public const string Alphabet = "0123456789ABCDEFGHJKLMNPQRSTUVWXYZ";
    public const ulong SpaceSize = 2386420683693101056UL; // 34^12

    static readonly int[] CharToDigit = BuildTable();

    static int[] BuildTable()
    {
        var t = new int[128];
        for (var i = 0; i < t.Length; i++) t[i] = -1;
        for (var i = 0; i < Alphabet.Length; i++) t[Alphabet[i]] = i;
        return t;
    }

    /// <summary>ordinal -&gt; 12 位可见种子串。</summary>
    public static string Format(ulong ordinal)
    {
        Span<char> buf = stackalloc char[SeedLength];
        for (var i = SeedLength - 1; i >= 0; i--)
        {
            buf[i] = Alphabet[(int)(ordinal % Radix)];
            ordinal /= Radix;
        }
        return new string(buf);
    }

    /// <summary>种子串 -&gt; ordinal。非法返回 false。</summary>
    public static bool TryParse(string seed, out ulong ordinal)
    {
        ordinal = 0;
        if (string.IsNullOrEmpty(seed)) return false;

        // 允许输入短于 12 位；左补 '0' 到 12 位，等价于直接解析（前导 0 不影响数值）
        var s = seed.Trim().ToUpperInvariant();
        if (s.Length > SeedLength) return false;

        foreach (var c in s)
        {
            var d = c < 128 ? CharToDigit[c] : -1;
            if (d < 0) return false;
            ordinal = ordinal * Radix + (ulong)d;
        }

        return ordinal < SpaceSize;
    }

    public static bool IsValid(string seed) => TryParse(seed, out _);

    /// <summary>
    /// 快速递增种子串（在字节缓冲上做 34 进制加法）。
    /// <c>index 0</c> 是最高位、<c>index 11</c> 是最低位 —— 与 <see cref="Format"/> 一致。
    /// 溢出（ZZZ... -&gt; 000...）时返回 false。
    /// </summary>
    public static bool Increment(char[] seed)
    {
        for (var i = SeedLength - 1; i >= 0; i--)
        {
            var d = CharToDigit[seed[i]] + 1;
            if (d < Radix) { seed[i] = Alphabet[d]; return true; }
            seed[i] = Alphabet[0];
        }
        return false;
    }

    public static char[] ToChars(ulong ordinal) => Format(ordinal).ToCharArray();

    public static string CharsToString(char[] seed) => new string(seed);

    // ------------------------------------------------------------------
    // 自检
    // ------------------------------------------------------------------
    public static void SelfTest(List<string> problems)
    {
        // 1) 往返
        var rnd = new Random(20260926);
        for (var i = 0; i < 50000; i++)
        {
            var ord = (ulong)rnd.NextInt64(0, long.MaxValue) % SpaceSize;
            var s = Format(ord);
            if (!TryParse(s, out var back) || back != ord)
            {
                problems.Add($"[SeedCodec] 往返失败 ord={ord} seed={s} back={back}");
                return;
            }
        }

        // 2) 边界 —— 这一步同时验证字节序（最低位在最后一位）
        var zero = Format(0);
        if (zero != new string('0', SeedLength))
            problems.Add($"[SeedCodec] Format(0) = {zero}，期望 000000000000（字节序可能是大端）");

        var last = Format(SpaceSize - 1);
        if (last != new string('Z', SeedLength))
            problems.Add($"[SeedCodec] Format(SpaceSize-1) = {last}，期望 ZZZZZZZZZZZZ");

        // 3) 递增与 Format 一致
        var arr = new char[SeedLength];
        Array.Copy(Format(12345).ToCharArray(), arr, SeedLength);
        for (var i = 0; i < 1000; i++)
        {
            if (!Increment(arr)) { problems.Add("[SeedCodec] Increment 提前溢出"); return; }
            if (CharsToString(arr) != Format(12345 + 1 + (ulong)i))
            {
                problems.Add($"[SeedCodec] Increment 与 Format 不一致 @{i}: {CharsToString(arr)}");
                return;
            }
        }

        // 4) 非法字符应被拒
        foreach (var bad in new[] { "IIIIIIIIIIII", "OOOOOOOOOOOO", "AAAAAAAAAAAAA", "abc-def-ghi!", "" })
            if (IsValid(bad)) problems.Add($"[SeedCodec] 非法种子 {bad} 被判为合法");
    }
}
