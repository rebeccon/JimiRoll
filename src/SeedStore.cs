using System.Text;
using System.Text.Json;

namespace JimiRoll;

/// <summary>一条命中记录（储备池里的一项）。</summary>
internal sealed class SeedHit
{
    public string Seed { get; set; } = "";
    public ulong Ordinal { get; set; }
    public int Act1UnknownRooms { get; set; }
    public string FoundAtUtc { get; set; } = "";
    /// <summary>RT2 给出的命中证据。</summary>
    public string Evidence { get; set; } = "";
}

/// <summary>离开储备池的种子（用掉的，或超过上限被移出的）。不删，只归档。</summary>
internal sealed class ArchiveEntry
{
    public string Seed { get; set; } = "";
    public ulong Ordinal { get; set; }
    public int Act1UnknownRooms { get; set; }
    /// <summary>"used" = 进入开局界面取走的；"overflow" = 超过 15 个上限被移出的。</summary>
    public string Reason { get; set; } = "";
    public string AtUtc { get; set; } = "";
}

/// <summary>搜索游标 —— 跨会话续扫，永不重复。</summary>
internal sealed class SearchCursor
{
    public int SchemaVersion { get; set; } = 1;
    public ulong NextOrdinal { get; set; }
    public long TotalScanned { get; set; }
    public long TotalMatched { get; set; }
    public string UpdatedAtUtc { get; set; } = "";
}

/// <summary>
/// 落盘 + 储备池管理。
///
/// 数据模型（消耗式）：
/// <code>
///   seeds_found.json  ← 储备池，数量恒定不超过 Conditions.ReserveCapacity（15）
///   seeds_used.json   ← 离开储备池的（用掉的 / 超上限移出的），只归档不删
///   seeds.txt         ← 储备池的纯文本清单，一行一个，方便肉眼对
///   cursor.json       ← 扫到哪儿了
/// </code>
///
/// 每次进入自定义开局界面会 <see cref="TakeNext"/> 取走队首的那个（最优先的），
/// 取走即从储备池删除并归档。储备池因此掉到 14 个 → 下次回主菜单会自动补搜。
///
/// 这样"储备数量恒定为 15"是自然结果，不需要额外的轮换游标。
/// </summary>
internal static class SeedStore
{
    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        // 让中文原样输出，而不是 \u9AA8\u9AB0 这种转义
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Dir { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SlayTheSpire2", "JimiRoll");

    public static string CursorPath => Path.Combine(Dir, "cursor.json");
    public static string ResultsPath => Path.Combine(Dir, "seeds_found.json");
    public static string UsedPath => Path.Combine(Dir, "seeds_used.json");
    public static string SeedsTextPath => Path.Combine(Dir, "seeds.txt");
    public static string LogPath => Path.Combine(Dir, "JimiRoll.log");

    static readonly object FileGate = new();
    static List<SeedHit> _reserve;
    static List<ArchiveEntry> _archive;
    /// <summary>已见过（在储备池或归档里）的种子串 —— 用来去重。
    ///
    /// 为什么必须有：游戏若在游标落盘前退出（崩溃/强退），下次启动会从旧位置重扫，
    /// 把同一批种子又收一遍。实测出现过同一个种子被塞进储备池 3 次的情况。
    /// 储备池要的是 15 个【不同】的种子，重复项等于白占位置。</summary>
    static HashSet<string> _seen;

    public static void EnsureDir()
    {
        try { Directory.CreateDirectory(Dir); }
        catch (Exception ex) { Console.WriteLine("[JimiRoll] 建目录失败: " + ex.Message); }
    }

    // ==================================================================
    // 储备池
    // ==================================================================

    static List<SeedHit> ReserveLocked()
    {
        if (_reserve != null) return _reserve;
        try
        {
            _reserve = File.Exists(ResultsPath)
                ? JsonSerializer.Deserialize<List<SeedHit>>(File.ReadAllText(ResultsPath), Json) ?? new List<SeedHit>()
                : new List<SeedHit>();
        }
        catch (Exception ex)
        {
            Log($"读取储备池失败，按空的处理: {ex.Message}");
            _reserve = new List<SeedHit>();
        }
        return _reserve;
    }

    static List<ArchiveEntry> ArchiveLocked()
    {
        if (_archive != null) return _archive;
        try
        {
            _archive = File.Exists(UsedPath)
                ? JsonSerializer.Deserialize<List<ArchiveEntry>>(File.ReadAllText(UsedPath), Json) ?? new List<ArchiveEntry>()
                : new List<ArchiveEntry>();
        }
        catch { _archive = new List<ArchiveEntry>(); }
        return _archive;
    }

    /// <summary>储备池里的种子数。</summary>
    public static int ReserveCount
    {
        get { lock (FileGate) return ReserveLocked().Count; }
    }

    /// <summary>储备池是否已满（满了就不用再搜）。</summary>
    public static bool IsReserveFull => ReserveCount >= Conditions.ReserveCapacity;

    /// <summary>储备池快照（副本，按优先级排好：问号多 → 证据确凿 → 序号小）。</summary>
    public static List<SeedHit> LoadHits()
    {
        lock (FileGate)
            return ReserveLocked()
                .Where(h => h != null && !string.IsNullOrWhiteSpace(h.Seed))
                .OrderByDescending(h => h.Act1UnknownRooms)
                .ThenByDescending(h => h.Evidence != null && h.Evidence.Contains("全中"))
                .ThenBy(h => h.Ordinal)
                .ToList();
    }

    /// <summary>加一个命中（重复的直接丢掉；超上限就归档为 overflow）。</summary>
    public static void AddHit(SeedHit hit)
    {
        lock (FileGate)
        {
            if (hit == null || string.IsNullOrWhiteSpace(hit.Seed)) return;

            if (SeenLocked().Contains(hit.Seed))
            {
                DumpCount++;
                return;   // 已经有过 → 丢弃，不占储备池位置
            }
            SeenLocked().Add(hit.Seed);

            var reserve = ReserveLocked();
            if (reserve.Count >= Conditions.ReserveCapacity)
            {
                ArchiveLocked().Add(new ArchiveEntry
                {
                    Seed = hit.Seed,
                    Ordinal = hit.Ordinal,
                    Act1UnknownRooms = hit.Act1UnknownRooms,
                    Reason = "overflow",
                    AtUtc = DateTime.UtcNow.ToString("O"),
                });
            }
            else
            {
                reserve.Add(hit);
            }
            _dirty = true;
        }
    }

    /// <summary>因为重复而被丢弃的条数（诊断用）。</summary>
    public static int DumpCount { get; private set; }

    /// <summary>已见种子集合（储备池 + 归档，全部去重）。</summary>
    static HashSet<string> SeenLocked()
    {
        if (_seen != null) return _seen;
        _seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var h in ReserveLocked()) if (h?.Seed != null) _seen.Add(h.Seed);
        foreach (var a in ArchiveLocked()) if (a?.Seed != null) _seen.Add(a.Seed);
        return _seen;
    }

    /// <summary>
    /// ★ 取走队首（最优先的）那个种子：从储备池删除，并归档为 "used"。
    /// 返回 null 表示储备池是空的。
    /// </summary>
    public static SeedHit TakeNext(out int remaining)
    {
        lock (FileGate)
        {
            var ordered = LoadHits();
            if (ordered.Count == 0) { remaining = 0; return null; }

            var head = ordered[0];
            var reserve = ReserveLocked();

            // 按「种子串」删，避免因为有重复项而删错
            var idx = reserve.FindIndex(h => h != null && h.Seed == head.Seed);
            if (idx >= 0) reserve.RemoveAt(idx);
            else reserve.RemoveAll(h => h != null && h.Seed == head.Seed);

            ArchiveLocked().Add(new ArchiveEntry
            {
                Seed = head.Seed,
                Ordinal = head.Ordinal,
                Act1UnknownRooms = head.Act1UnknownRooms,
                Reason = "used",
                AtUtc = DateTime.UtcNow.ToString("O"),
            });

            _dirty = true;
            remaining = reserve.Count;
            return head;
        }
    }

    /// <summary>
    /// 整理储备池：先<b>去重</b>，再裁到上限，多出来的归档为 overflow。
    /// 启动时调一次，能自动清掉历史遗留的重复项（游戏在游标落盘前退出会产生）。
    /// 返回 (去重丢掉数, 超上限移出数)。
    /// </summary>
    public static (int dupes, int overflow) TrimToCapacity()
    {
        lock (FileGate)
        {
            var ordered = LoadHits();
            var reserve = ReserveLocked();
            var cap = Conditions.ReserveCapacity;

            // ---- 先去重（保留优先级最高的那个）----
            var distinct = new List<SeedHit>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var dupes = 0;
            foreach (var h in ordered)
            {
                if (!seen.Add(h.Seed)) { dupes++; continue; }
                distinct.Add(h);
            }

            // ---- 再裁上限 ----
            var removed = distinct.Skip(cap).ToList();

            foreach (var h in removed)
                ArchiveLocked().Add(new ArchiveEntry
                {
                    Seed = h.Seed, Ordinal = h.Ordinal, Act1UnknownRooms = h.Act1UnknownRooms,
                    Reason = "overflow", AtUtc = DateTime.UtcNow.ToString("O"),
                });

            reserve.Clear();
            reserve.AddRange(distinct.Take(cap));

            // 重建去重集合（储备池 + 归档）
            _seen = null;

            if (dupes > 0 || removed.Count > 0) _dirty = true;
            return (dupes, removed.Count);
        }
    }

    static bool _dirty;

    /// <summary>把内存状态写回磁盘（有任何变更时才写）。</summary>
    public static void Flush()
    {
        lock (FileGate)
        {
            if (!_dirty) return;
            try
            {
                EnsureDir();
                WriteJsonAtomic(ResultsPath, ReserveLocked());
                WriteJsonAtomic(UsedPath, ArchiveLocked());
                WriteTextList(ReserveLocked());
                _dirty = false;
            }
            catch (Exception ex) { Log($"写盘失败: {ex.Message}"); }
        }
    }

    /// <summary>无条件写盘（首次或需要强制刷新时用）。</summary>
    public static void FlushForce()
    {
        lock (FileGate) { _dirty = true; }
        Flush();
    }

    static void WriteJsonAtomic<T>(string path, T value)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, Json));
        File.Move(tmp, path, overwrite: true);
    }

    static void WriteTextList(List<SeedHit> hits)
    {
        var ordered = hits.Where(h => h != null && !string.IsNullOrWhiteSpace(h.Seed))
                          .OrderByDescending(h => h.Act1UnknownRooms)
                          .ThenByDescending(h => h.Evidence != null && h.Evidence.Contains("全中"))
                          .ThenBy(h => h.Ordinal)
                          .ToList();

        var lines = new List<string>
        {
            "# JimiRoll 储备种子（每行一个，直接复制到游戏的种子输入框）",
            "# 条件: " + Conditions.Describe(),
            $"# 储备数量: {ordered.Count} / {Conditions.ReserveCapacity}",
            "#",
        };
        lines.AddRange(ordered.Select(h => h.Seed));

        var tmp = SeedsTextPath + ".tmp";
        File.WriteAllLines(tmp, lines, new UTF8Encoding(false));
        File.Move(tmp, SeedsTextPath, overwrite: true);
    }

    /// <summary>归档里的条数（用掉的 + 超上限移出的）。</summary>
    public static int ArchiveCount { get { lock (FileGate) return ArchiveLocked().Count; } }

    // ==================================================================
    // 游标
    // ==================================================================

    public static SearchCursor LoadCursor()
    {
        try
        {
            if (!File.Exists(CursorPath)) return new SearchCursor();
            return JsonSerializer.Deserialize<SearchCursor>(File.ReadAllText(CursorPath), Json) ?? new SearchCursor();
        }
        catch (Exception ex)
        {
            Log($"读取游标失败，从 0 开始: {ex.Message}");
            return new SearchCursor();
        }
    }

    public static void SaveCursor(SearchCursor cursor)
    {
        try
        {
            lock (FileGate)
            {
                EnsureDir();
                cursor.UpdatedAtUtc = DateTime.UtcNow.ToString("O");
                WriteJsonAtomic(CursorPath, cursor);
            }
        }
        catch (Exception ex) { Log($"写游标失败: {ex.Message}"); }
    }

    // ==================================================================
    // 日志
    // ==================================================================

    /// <summary>
    /// 写日志（Info 级别）。
    ///
    /// ★ P4：实际输出交给 <see cref="Logger"/> —— 支持分级、轮转、语言。
    ///   保留这个方法是为了不惊动已有的 70 多个调用点。
    ///   需要更高/更低级别时用 <see cref="Warn"/> / <see cref="Error"/> / <see cref="Debug"/>。
    /// </summary>
    public static void Log(string message) => Logger.Info(message);

    /// <summary>可恢复的问题（比 Info 更醒目）。</summary>
    public static void Warn(string message) => Logger.Warn(message);

    /// <summary>出错（功能可能不可用）。</summary>
    public static void Error(string message) => Logger.Error(message);

    /// <summary>详细诊断（默认不输出，settings.json 里把 LogLevel 调成 Debug 才看得到）。</summary>
    public static void Debug(string message) => Logger.Debug(message);
}
