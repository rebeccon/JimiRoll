using System.Buffers.Binary;
using System.Numerics;

namespace FastSeedRoll;

/// <summary>
/// XxHash64（seed = 0）的托管实现 —— 等价于
/// <c>System.IO.Hashing.XxHash64.HashToUInt64(span, 0)</c>，
/// 也就是游戏 <c>StringHelper.GetDeterministicHashCode</c> 内部用的东西
/// （反汇编：<c>GetBytes</c> → <c>XxHash64::HashToUInt64(span, 0)</c>）。
///
/// <b>它不是被信任的，而是被对拍的</b>：<see cref="GameInterop.VerifyAgainstGame"/>
/// 会拿游戏实现逐值比对，不一致就整体禁用、回退到游戏实现。
/// 所以这里写错不会导致错误结果，只会导致"没加速"。
/// </summary>
internal static class XxHash64
{
    const ulong Prime1 = 0x9E3779B185EBCA87UL;
    const ulong Prime2 = 0xC2B2AE3D27D4EB4FUL;
    const ulong Prime3 = 0x165667B19E3779F9UL;
    const ulong Prime4 = 0x85EBCA77C2B2AE63UL;
    const ulong Prime5 = 0x27D4EB2F165667C5UL;

    public static ulong HashToUInt64(ReadOnlySpan<byte> data, ulong seed = 0)
    {
        var len = data.Length;
        ulong h;
        var p = 0;

        if (len >= 32)
        {
            var v1 = seed + Prime1 + Prime2;
            var v2 = seed + Prime2;
            var v3 = seed;
            var v4 = seed - Prime1;

            var limit = len - 32;
            while (p <= limit)
            {
                v1 = Round(v1, BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(p, 8)));
                v2 = Round(v2, BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(p + 8, 8)));
                v3 = Round(v3, BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(p + 16, 8)));
                v4 = Round(v4, BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(p + 24, 8)));
                p += 32;
            }

            h = BitOperations.RotateLeft(v1, 1) + BitOperations.RotateLeft(v2, 7)
              + BitOperations.RotateLeft(v3, 12) + BitOperations.RotateLeft(v4, 18);

            h = MergeRound(h, v1);
            h = MergeRound(h, v2);
            h = MergeRound(h, v3);
            h = MergeRound(h, v4);
        }
        else
        {
            h = seed + Prime5;
        }

        h += (ulong)len;

        // 尾部：8 字节一组 —— 标准 xxHash64 用的是「整个 span 从头扫剩余部分」，
        // 这里用游标 p 继续即可（p 已走过所有完整的 32 字节块）。
        var rem = len - p;
        while (rem >= 8)
        {
            var k1 = Round(0, BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(p, 8)));
            h ^= k1;
            h = BitOperations.RotateLeft(h, 27) * Prime1 + Prime4;
            p += 8;
            rem -= 8;
        }
        if (rem >= 4)
        {
            h ^= (ulong)BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(p, 4)) * Prime1;
            h = BitOperations.RotateLeft(h, 23) * Prime2 + Prime3;
            p += 4;
            rem -= 4;
        }
        while (rem > 0)
        {
            h ^= data[p] * Prime5;
            h = BitOperations.RotateLeft(h, 11) * Prime1;
            p++;
            rem--;
        }

        // 雪崩
        h ^= h >> 33;
        h *= Prime2;
        h ^= h >> 29;
        h *= Prime3;
        h ^= h >> 32;
        return h;
    }

    static ulong Round(ulong acc, ulong input)
    {
        acc += input * Prime2;
        acc = BitOperations.RotateLeft(acc, 31);
        acc *= Prime1;
        return acc;
    }

    static ulong MergeRound(ulong acc, ulong val)
    {
        val = Round(0, val);
        acc ^= val;
        acc = acc * Prime1 + Prime4;
        return acc;
    }
}
