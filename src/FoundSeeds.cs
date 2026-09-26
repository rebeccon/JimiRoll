using System.Text;
using Godot;

namespace JimiRoll;

/// <summary>
/// 储备池的视图 + 对外动作（打日志、放剪贴板、取走下一个）。
///
/// 真正的存储与增删在 <see cref="SeedStore"/>；这里只做"给人看/给人用"的部分，
/// 所以不存在"缓存住旧数据"的问题 —— 每次读都是最新的。
/// </summary>
internal static class FoundSeeds
{
    /// <summary>当前储备池（按优先级排好的快照）。</summary>
    public static IReadOnlyList<SeedHit> All => SeedStore.LoadHits();

    public static int Count => SeedStore.ReserveCount;
    public static bool Any => SeedStore.ReserveCount > 0;
    public static bool IsFull => SeedStore.IsReserveFull;

    // ==================================================================
    // 取走一个（★ 删除式）
    // ==================================================================

    /// <summary>
    /// 取走队首（最优先的）种子：从储备池删除并归档，然后返回它。
    /// 储备池空了返回 null。
    /// </summary>
    public static SeedHit TakeNext()
    {
        var hit = SeedStore.TakeNext(out var remaining);
        if (hit == null) return null;

        SeedStore.Flush();   // 立刻写盘，保证"取走即删除"是持久的
        SeedStore.Log($"[取走] {hit.Seed}（问号 {hit.Act1UnknownRooms}）→ 已从储备池删除并归档；" +
                      $"剩余 {remaining}/{Conditions.ReserveCapacity}");
        return hit;
    }

    // ==================================================================
    // 展示
    // ==================================================================

    /// <summary>把整个储备池打进日志（启动时看一眼用）。</summary>
    public static void LogReserve()
    {
        var all = All;
        if (all.Count == 0)
        {
            SeedStore.Log("储备池: 0 个（需要搜索来补充）");
            return;
        }

        var solid = all.Count(h => h.Evidence != null && h.Evidence.Contains("全中"));

        var sb = new StringBuilder();
        sb.AppendLine($"储备池: {all.Count}/{Conditions.ReserveCapacity} 个（按优先级排序）");
        if (solid < all.Count)
            sb.AppendLine($"  其中 {all.Count - solid} 条证据不完整（旧版本并发 bug 留下的），已排在后面");

        for (var i = 0; i < all.Count; i++)
        {
            var h = all[i];
            var mark = h.Evidence != null && h.Evidence.Contains("全中") ? ' ' : '?';
            sb.AppendLine($"  {i + 1,3}.{mark} 问号{h.Act1UnknownRooms}   {h.Seed}");
        }
        sb.AppendLine($"  （每次进入自定义开局会取走第 1 个并删除；归档总数 {SeedStore.ArchiveCount}）");
        SeedStore.Log(sb.ToString());
    }

    /// <summary>把储备池放进系统剪贴板。</summary>
    public static void CopyToClipboard()
    {
        try
        {
            var all = All;
            if (all.Count == 0) return;
            DisplayServer.ClipboardSet(string.Join(System.Environment.NewLine, all.Select(h => h.Seed)));
            SeedStore.Log($"已把 {all.Count} 个储备种子放进剪贴板 —— 游戏里直接 Ctrl+V 即可");
        }
        catch (Exception ex)
        {
            SeedStore.Log("放剪贴板失败（不影响其它功能）: " + ex.Message);
        }
    }

    public static string Summary()
        => Count == 0
            ? "0 个（需要补充）"
            : $"{Count}/{Conditions.ReserveCapacity} 个，队首是 {All[0].Seed}（问号 {All[0].Act1UnknownRooms}）";
}
