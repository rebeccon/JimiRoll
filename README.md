# 基米roll种器（FastSeedRoll）

> 为《杀戮尖塔 2》自动筛选固定条件的开局种子 —— 装好即用，不需要任何操作。

需要第三方模组 **[RolltheSpire2](https://steamcommunity.com/sharedfiles/filedetails/?id=3755166888)**（作者 FalseOcean）。

---

## 它筛什么

| 条件 | 值 |
|---|---|
| 开局 | 涅奥骨骰（`NeowsBones`） |
| 骨骰给出的两个遗物 | 松动羊毛剪 `PrecariousShears` + 新叶 `NewLeaf` |
| 最终诅咒 | 愧疚 `Guilty` |
| 第一章问号房间 | ≥ 12 个 |
| 角色 / 难度 | 沉默猎手 / 进阶 10 |

---

## 怎么用

1. 把 `FastSeedRoll` 文件夹放进游戏 mods 目录
2. 确认 mod 列表里 **RolltheSpire2** 和 **基米roll种器** 都是启用状态
3. 进游戏，回一次主菜单 —— 它就自动开始干活了

**之后什么都不用做。** 储备池满了它就空闲待命；你每开一次「自定义开局」它取走一个种子，
储备掉到 14 个，下次回主菜单自动补上。

### 取种子

打开 **自定义开局** 界面 → 种子输入框里已经自动填好了。
退出来再进，会换成下一个（上一个已经被取走）。

储备池的完整清单也在系统剪贴板里，可以直接 `Ctrl+V`。

---

## 核心机制：储备池

```
储备池恒定维持 15 个
      ↓
进「自定义开局」→ 取走队首那个（最优先的）
                → 从储备池删除，归档到 seeds_used.json
                → 填进种子框
      ↓
储备掉到 14 → 回主菜单时自动补搜 → 补满 15 就停手
      ↓
储备是满的 15 个 → 完全不搜索，零资源占用
```

取走的种子**不会丢**，全部归档在 `seeds_used.json`。

---

## 数据文件

都在 `%APPDATA%\SlayTheSpire2\FastSeedRoll\`：

| 文件 | 内容 |
|---|---|
| `seeds_found.json` | 储备池（恒定 ≤ 15 个） |
| `seeds.txt` | 储备池的纯文本清单，一行一个 |
| `seeds_used.json` | 归档：用掉的 + 超上限移出的，只增不删 |
| `cursor.json` | 扫到哪个位置了（跨会话续扫，不重复） |
| `fastseedroll.log` | 运行日志 |

---

## 排障

### 想让它暂时别跑

双击 mod 目录下的 `Pause.cmd`（再双击恢复）。

它做的事就是在数据目录下建/删一个 `DISABLE_SEARCH.txt`。回到主菜单即生效，不用重启。

### 看日志

```
%APPDATA%\SlayTheSpire2\FastSeedRoll\fastseedroll.log
```

正常启动长这样：

```
==================== FastSeedRoll 启动 ====================
条件: 涅奥骨骰=RELIC:NEOWS_BONES; 遗物=...; 诅咒=CARD:GUILTY; 第一章问号>=12; ...
运行模式: 默认自动运行（储备池不满就补搜，满了就空闲待命）
储备池: 13/15 个（按优先级排序）
    1.  问号14   000000048UGP
    ...
种子哈希: ✓ 自研 XxHash64 与游戏 GetDeterministicHashCode 在 2007 个样本上完全一致
✓ 自检通过（种子哈希与游戏一致）
[版本] 探测结果 = "0.111.0"
RT2 接线状态: ✓ 已就绪
✓✓ 全部就绪 —— 储备池不满时会自动补搜
```

### 自检失败怎么办

为避免给出**错误的种子**，自检不通过时搜索会**拒绝启动**。日志里会说明是哪一项失败：

- **种子哈希对拍失败** → 自研哈希与游戏不一致，会自动回退到游戏实现（慢但正确），
  日志会出现 `✗ 有 N 处不一致`。此时功能仍可用，只是慢一些。
- **`RT2 接线状态: ✗`** → 后面会跟一段诊断，说明缺了哪个类型/方法。
  通常是 RolltheSpire2 更新导致 API 变化。
- **`无法探测游戏版本号`** → 游戏更新后版本探测失败。**这时不会搜索**（宁可不动，不出错种子）。

### 种子框没自动填

看日志里有没有 `[种子框]` 开头的行，会说明卡在哪一步。
如果这个功能本身出问题，可以建一个空文件
`%APPDATA%\SlayTheSpire2\FastSeedRoll\DISABLE_SEED_PATCH.txt` 单独关掉它。

---

## 改条件

条件写在 `src/Conditions.cs` 里，改完重新编译：

```csharp
public const int  TargetAct1UnknownRooms = 12;    // 问号房间阈值
public const bool RequireExactUnknownRooms = false; // false = ≥，true = 必须恰好等于
public const int  ReserveCapacity = 15;            // 储备池数量
public const int  MaxRunMinutes = 3;               // 单次搜索时间上限
public const string CharacterKey = "CHARACTER:SILENT";
public const int  Ascension = 10;
```

> 改成 `settings.json` 外部化在计划中（见 `CHANGELOG.md`）。

---

## 从源码构建

需要 .NET 9 SDK。

```powershell
# 默认路径（可用环境变量覆盖）
$env:STS2_GAME_DIR = "D:\steam\steamapps\common\Slay the Spire 2"
$env:STS2_RT2_DIR  = "D:\steam\steamapps\workshop\content\2868840\3755166888\RolltheSpire2"

dotnet build -c Release              # 编译并自动部署到 mods 目录
dotnet build -c Release -p:SkipDeploy=true   # 只编译
.\build.ps1                          # 打包成可发布的 zip
```

编译期路径只影响编译，**不会进入 dll**；运行时完全从游戏进程自身取信息。

---

## 工作原理（简述）

```
全种子空间（34^12）
   │ ① 极廉价预筛：算 act_1_map 的问号数（每种子 2 次 NextDouble）
   │    只留 ≥12 的（约 69%）
   ▼
   │ ② 交给 RolltheSpire2 做骨骰遗物/诅咒的精确判定
   ▼
命中 → 储备池
```

问号数与骨骰结果互不相关，所以第①级的作用是**省 CPU**：
它比第②级快 100 倍以上，却砍掉了大量无谓的精确判定。

---

## 致谢与依赖

- **[RolltheSpire2](https://steamcommunity.com/sharedfiles/filedetails/?id=3755166888)**（FalseOcean）——
  本模组完全依赖它的种子预测能力。没有它就没有这个模组。
- 游戏内自动运行、储备池、种子框填充等则是本模组自己实现的。

---

## 许可证

[MIT](LICENSE) —— 随便用，作者不担责。

使用本模组造成的任何后果（存档异常、游戏崩溃等）由使用者自行承担。
