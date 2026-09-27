# 去 RT2 化方案（自研骨骰判定）

> 目标：让 JimiRoll **完全不再依赖 RolltheSpire2**。
>
> **状态：★★★ 条件②③已跑通并验证 14/14（2026-09-27）★★★**
> 所有结论都有 IL 实证 + 可复现的对拍证据。工具：`_tools/BonesProbe`

---

## 〇、当前进度速览

```
① 涅奥是否给出骨骰        IL 已完全解出，待与 RT2 逐种子对拍
② 骨骰给的两个遗物        ✅ 已实现，14/14 地面真值全中
③ 骨骰给的诅咒            ✅ 已实现（池子 1:1 解出），逻辑同路
④ 第一章地图/出怪占比     ✅ 自研完成（ActMapPredict，v2.2.0）
```

**已实证的完整判定链**（`skipRelics = 0`, `skipCurses = 0` —— 两条流在骨骰之前都不被消耗）：

```csharp
// ---- ② 遗物 ----
// 池子（顺序即 AllPossibleOptions 的顺序，28 个）
var valid = Neow.AllPossibleOptions
                .Where(o => o.Relic != null
                         && o.Relic.IsAllowedAtNeow(owner)
                         && !(o.Relic is NeowsBones))
                .Select(o => o.Relic).OfType<RelicModel>().ToList();
owner.PlayerRng.Rewards.Shuffle(valid);          // Fisher-Yates, 27 次 NextInt(i+1)
var picked = valid.Take(2);

// ---- ③ 诅咒 ----
var curses = ModelDb.CardPool<CurseCardPool>()
                .GetUnlockedCards(owner.UnlockState, runState.CardMultiplayerConstraint)
                .Where(c => c.CanBeGeneratedByModifiers())
                .OrderBy(c => c.Id)
                .ToList();
var curse = runState.Rng.Niche.NextItem(curses);
```

### 那 28 个遗物（**顺序至关重要**）

`CurseOptions(10) + PositiveOptions(14) + Singles(6) = 30`，减去 `NeowsBones` 自己，
再减去 `MassiveScroll`（它的 `IsAllowed` 是 `players.Count > 1`，多人专属）
⇒ **28**，与游戏内实测的 `BonesEligibleRelics = 28` **完全吻合**。

```
 1 CursedPearl        6 NeowsSacrifice    11 BoomingConch       16 LostCoffer      21 ScrollBoxes      26 Pomander
 2 DowsingRod         7 ★PrecariousShears 12 FishingRod         17 NeowsTorment    22 WingedBoots      27 SmallCapsule
 3 HeftyTablet        8 SilkenTress       13 GoldenPearl        18 ★NewLeaf        23 LavaRock         28 StoneHumidifier
 4 LargeCapsule       9 SilverCrucible    14 Kaleidoscope       19 PhialHolster    24 NeowsTalisman
 5 LeafyPoultice     10 ArcaneScroll      15 LeadPaperweight    20 PreciseScissors 25 NutritiousOyster
```

（★ = 我们的两个目标遗物）

### 靠假设搜索钉死「前置消耗次数」

`BonesProbe hypo` 枚举 5 种抽取类型 × 0~8 次前置消耗，用地面真值一票否决：

```
14/14  kind=nextint  skipRelics=0 skipCurses=0     ← 唯一最干净的胜出组合
```

**⇒ 两条流在骨骰之前都消耗 0 次。** 这符合直觉：骨骰是全局第一件事。

---

## 一、为什么要去依赖

| 现状 | 问题 |
|---|---|
| manifest `dependencies: [RolltheSpire2]` | 少装一个就整个 mod 不加载 |
| csproj `HintPath` 引用 `RolltheSpire2.dll` | 编译期就绑死了 |
| 每个种子的判定都调 RT2 | RT2 一崩，搜索立刻停 |

RT2 是优秀的工程，但**我们只需要它 7 个族里的极小一块**。把它整块背在身上，
换来的是「它更新我就得重新验证」，而这部分我们完全有能力自己做。

---

## 二、RT2 到底替我们做了什么

反汇编 + 离线实测（`_tools/QnProbe`）确认，我们真正用到 RT2 的只有 4 件事：

| # | 条件 | 谁在算 | 难度 |
|---|---|---|---|
| ① | 涅奥给出的选项里有**骨骰** | RT2 `NeowFamily` | ★★☆ |
| ② | 骨骰给的 2 个遗物 = 松动羊毛剪 + 新叶 | RT2 `NeowFamily` | ★★☆ |
| ③ | 骨骰给的诅咒 = 愧疚 | RT2 `NeowFamily` | ★★☆ |
| ④ | 第一章地图（出怪占比） | **已自研**（`ActMapPredict`，见文末附录） | 已完成 |

⇒ 也就是说 **只剩 ①②③**，而它们全在同一个方法链上。

---

## 三、★ 核心发现：骨骰根本不需要「重放整个游戏」

之前的判断（「自研 = 人月级重放器」）是针对 RT2 **完整**能力的。
逐条反汇编之后，骨骰这条链短得惊人。

### 3.1 骨骰的效果（`NeowsBones.AfterObtained` 的 async 状态机 `<AfterObtained>d__7::MoveNext`）

```csharp
// 【遗物部分】IL_0020 ~ IL_0062
var valid = NeowsBones.GetValidRelics(Owner).ToList();   // IL_0026
Owner.PlayerRng.Rewards.Shuffle(valid);                  // IL_0037/003C/0042  ← 玩家级 Rewards 流
var picked = valid.Take(DynamicVars["Relics"].IntValue); // IL_004E/0058/005D  ← 取前 2 个

// 【诅咒部分】IL_00EE ~ IL_0197
var availableCurses = ModelDb.CardPool<ColorlessCardPool>()
        .GetUnlockedCards(Owner.UnlockState, ...)        // IL_00EE ~ IL_010E
        .Where(是诅咒);                                   // IL_0132
var curse = Owner.RunState.Rng.Niche.NextItem(availableCurses);  // IL_0187/018C/0197
```

**没有重放，没有世界模拟，就是一个 shuffle + 一个 NextItem。**

### 3.2 底层构件（全部可以照抄或直接调用）

| 构件 | 反汇编结果 | 证据 |
|---|---|---|
| `Rng.Shuffle(list)` | **教科书 Fisher–Yates**：<br>`for (i = n-1; i > 0; i--) { j = NextInt(i+1); swap(list[i], list[j]); }` | `Rng::Shuffle` IL_0000~IL_0045 |
| `Rng.NextItem(en)` | `array = en.ToArray(); if (len == 0) return default;`<br>`return array[NextInt(0, len)];` | `Rng::NextItem` IL_0000~IL_0035 |
| `PlayerRngSet..ctor(seed)` | 内部 3 条流：`Rewards` / `Shops` / `Transformations` | `PlayerRngType` 枚举（只有这 3 个） |
| `PlayerRngSet.CreateRng(type)` | `new Rng(Seed, SnakeCase(type.ToString()))`<br>⇒ **Rewards 流名 = `"rewards"`** | `PlayerRngSet::CreateRng` IL_0000~IL_001F |
| `Rng..ctor(seed, name)` | `MegaRandom(seed + GetDeterministicHashCode(name))`，splitmix64 → xoshiro256\*\* | 前序结论（已对拍） |

### 3.3 ★ 最关键的一点：这些类型**全是 public，可以直接 new**

| 类型 | 可见性 |
|---|---|
| `MegaCrit.Sts2.Core.Random.Rng` | public |
| `MegaRandom` | public |
| `RunRngSet` | public |
| `PlayerRngSet` | public |

⇒ **连 RNG 都不用自己写。** 直接：

```csharp
var run = new RunRngSet(StringHelper.GetDeterministicHashCode(seed));
var niche = run.Niche;                                   // 诅咒流

var plr = new PlayerRngSet(StringHelper.GetDeterministicHashCode(seed) + slotIndex);
var rewards = plr.Rewards;                               // 遗物流
```

加上 P3 已经自研并**对拍过 2007 个样本完全一致**的 `XxHash64`，种子哈希那一段也是现成的。

---

## 四、缺口清单（还没实证的，逐条列出来）

> 下面每一条都可能成为「算错种子」的来源，必须逐条实证 + 对拍，**不允许猜**。

| # | 缺口 | 怎么补 | 风险 |
|---|---|---|---|
| G1 | `NeowsBones.GetValidRelics(Player)` 的**完整过滤逻辑** | `dis NeowsBones GetValidRelics` + `<>c__DisplayClass6_0::<GetValidRelics>b__0` | 🔴 池子错了 → 遗物全错 |
| G2 | 诅咒池 `availableCurses` 的**完整过滤逻辑** | 反汇编 `AfterObtained` 的 IL_00EE~IL_0160 段 + 看 `Where` 的 lambda | 🔴 |
| G3 | `EventModel.Rng` 是怎么派生的（种子 + 流名 + mixin 常量） | `dis EventModel get_Rng` / `dis AncientEventModel` | 🔴 涅奥选项全错 |
| G4 | `ListExtensions.UnstableShuffle(list, rng)` 的算法 | `dis ListExtensions UnstableShuffle` | 🔴 |
| G5 | `Neow.PositiveOptions` / `CurseOptions` / `AllPossibleOptions` 的池子构成 | `dis Neow get_PositiveOptions` 等 | 🔴 |
| G6 | `Neow.GenerateInitialOptions` 里 8 个 lambda 谓词（`b__35_0` ~ `b__35_7`） | 逐个 `dis` | 🟡 |
| G7 | `Rng.NextBool()` 的实现 | `dis Rng NextBool` | 🟡 |
| G8 | **骨骰之前 `Rewards` / `Niche` 分别被消耗了几次** | **对拍**（见第五节） | 🔴 最隐蔽的坑 |

### 4.1 已经能推断出来的部分（来自前序笔记，仍需 IL 复核）

**`RunRngSet.Niche`（诅咒流）** —— 链条已完全清楚，可以直接写：

```csharp
RunRngSet.Seed       = StringHelper.GetDeterministicHashCode(种子串);
RunRngSet.CreateRng(t) = new Rng(Seed, SnakeCase(t.ToString()));   // 已反汇编确认
⇒ Niche 流 = new Rng(RunRngSet.Seed, "niche")
```

**`EventModel.Rng`（涅奥选项流）** —— 强烈怀疑走的是「按模型派生」那个构造函数变体：

```csharp
// 已知变体（前序笔记已反汇编确认）
Rng..ctor(Player player, ModelId id, UInt64 mixin):
      new Rng(runState.Rng.Seed
              + GetPlayerSlotIndex(player)
              + GetDeterministicHashCode(id.Entry)
              + mixin)
```

事件是 Model，所以极可能是 `new Rng(owner, 事件的 ModelId, <某个 mixin 常量>)`。
**待办：`dis EventModel get_Rng` 确认 id 用的是哪个（事件自身 Id？）+ mixin 常量的值。**
这个 mixin 是必查项 —— 猜错一位，所有涅奥选项全错。


---

## 五、怎么验证（有现成的对拍底座）

`_tools/QnProbe` 已经能跑 RT2 的完整预测，而且**能给出结构化的结果**：

```powershell
# 单个种子的完整 Neow 预测（含每个选项的 SlotIndex / RelicKey / BonesOutcome）
& "_tools\qnprobe.ps1" neow 00000005N9C0 Silent 10

# 批量扫描：qn 预筛 → RT2 精确判定 → 打印完全命中
& "_tools\qnprobe.ps1" hunt 00000005N9C0 30000 Silent 10 14
```

### 5.1 已经在上一轮拿到的实测数据

```
扫描种子      : 30,000
预筛通过      : 1,912  (6.37%)        ← 问号 == 14 的比例
其中出骨骰    : 193    (10.094%)      ← ★ 骨骰出现率 ≈ 10.1%
完全命中      : 0
整体速度      : 265,729 种子/秒
预测平均耗时  : 0.041 ms
```

★ **重要观察：骨骰出现时 `SlotIndex` 恒为 3。**
这可能说明涅奥的选项顺序是固定的（骨骰永远排在第 4 位），
若是如此，条件①的判定可以简化成「第 3 号位是不是骨骰」——待用更多样本确认。

★ **离线限制**：`CaptureAuditedVanillaAllUnlocks` 拿不到 `BonesEligibleRelics`
（警告 `beta111.neow-effect.neows-bones.bones-eligible-relic-pool-required`），
所以**离线的 RT2 算不出 BonesOutcome**，`完全命中` 恒为 0。
⇒ 条件①（有没有骨骰）**可以离线对拍**；条件②③需要另找标准答案（见 5.2）。

### 5.2 现成的地面真值：我们自己的储备池

`seeds_found.json` + `seeds_used.json` 里累积的 **~109 个种子**，
全都是**在游戏里用真实骨骰池子判定过 `✓全中` 的**。

⇒ 这就是最好的地面真值：
**把这 109 个种子喂给自研实现，必须 100% 全部判为「命中」。**
漏掉任何一个，说明 G1/G2/G8 里有错。

反向样本（骨骰给的遗物不对）可以这样造：扫描一批种子，把自研实现判为
「出骨骰但遗物不对」的种子记下来，再挑几个**进游戏实测**验证。

---

## 六、实施步骤（依赖顺序）

```
S1  补齐 G1~G7 的 IL 实证（把每个方法的完整 IL 打出来存档）        ~0.5 天
    ↳ 工具已备好：_tools/去RT2侦察.ps1（一条命令全打出来）
S2  写 _tools/BonesProbe：纯离线自研预测器，命令行给种子 → 打印判定   ~1 天
    ↳ ★ 已完成（待编译验证）：见 _tools/BonesProbe/README.md
S3  对拍 A：用 109 个已知命中种子验证「必须全中」                  ~0.5 天
    ↳ 命令已备好：run.ps1 hypo（假设搜索，自动枚举并一票否决）
S4  对拍 B：用条件①（有没有骨骰）与 RT2 逐种子比对（几百万个）        ~0.5 天
S5  对拍 C：抽查若干负样本，进游戏实测                             ~0.5 天
S6  把自研预测器搬进 JimiRoll（替换 Rt2Binding），保留 RT2 路径做回退  ~1 天
S7  去掉依赖：csproj 引用 + manifest dependencies                 ~0.5 天
S8  P6 发布前验证清单（含「只装 JimiRoll」的干净环境测试）           ~0.5 天
```

### 6.1 两个未知量，和「用可判定性代替猜测」

| 未知量 | 处理方式 |
|---|---|
| **G8：骨骰之前 `Rewards`/`Niche` 各被消耗了几次** | `BonesProbe hypo` —— 枚举假设空间（消耗次数 0~8 × 5 种抽取类型），用**上百个已知命中种子**一票否决。能同时让 100% 样本成立的组合就是答案。**可判定、可证伪**，不是猜。 |
| **G1/G2/G5：两个池子的内容与顺序** | 只能靠 IL（顺序错一位整条 shuffle 全错）。这是剩下最硬的一块。 |

★ 一个让事情简单很多的性质（IL 实证）：
`Shuffle<T>(IList<T>)` 与 `NextItem<T>(IEnumerable<T>)` **只做位置交换/按下标取值，与 T 无关**，
抽取次数只取决于 `Count`。⇒ 离线复现时**不需要造出 `RelicModel`/`CardModel` 实例**，
直接拿 `List<string>` 装 key，排列结果与游戏一致。这条把「离线没有 ModelDb」这个障碍直接消掉了。

**关键原则**：S3 不过就不许进 S6。**宁可停在「依赖 RT2」，也不要给错种子。**

---

## 七、关于 RT2 的版本限制（答用户提问）

RT2 当前只支持 **Beta 111（游戏 0.111.0）**。分两个层面看：

### 7.1 我们**自己**的部分，跨版本表现

| 组件 | 跨版本行为 |
|---|---|
| 种子编解码（`SeedCodec`） | ✅ 稳定。`Radix=34` / `SeedLength=12` / 字母表都是**游戏常量**，基本不会动 |
| 种子哈希（`XxHash64`） | ✅ 稳定。就是 xxHash64 标准算法 |
| 种子→RNG（`Rng`/`MegaRandom`） | ✅ **运行时调游戏自己的类**，游戏怎么变都跟着变 |
| 第一章地图 / 出怪占比 | ⚠️ 依赖 `"act_1_map"` 流名与 `StandardActMap` 的构造签名。另有**游戏自身不可复现**的风险（见文末附录）—— 已用「主线程复核」兜底 |

⇒ 前三项天然跨版本；**第四项会随游戏版本漂移**，但它是**廉价预筛**，
真错了只会「多算几个种子」，不会给错种子（最终判定不在它）。

### 7.2 RT2 的部分，跨版本表现

- RT2 的 `RuntimeProfileRegistry.Select(detection)` 会按游戏版本挑 profile
  （现在是 `Beta111Profile`）。
- **游戏更新后**：
  - 若 RT2 也更新支持了新版本 → 挑到新 profile，我们**可能**继续工作；
    但 profile 变了意味着 RT2 内部的重放逻辑变了，**我们必须重新对拍**。
  - 若 RT2 没更新 → `Select` 失败 / 拿到不支持的结果 → 我们记
    `RT2 接线状态: ✗` 并**拒绝搜索**（不出错种子）。
- 别忘了 **manifest 的 `min_game_version: 0.111.0`** ——
  游戏版本低于它会警告，高于它游戏只会在模组列表里提示，不阻止加载。

⇒ **结论：当前版本（0.111.0）完全正常；换版本后，安全性有保障（不会给错种子），
但可用性取决于 RT2 是否跟得上。** 这也正是要去掉依赖的最强理由 ——
去掉了，就只剩 7.1 里那一条良性风险，而它是可控的。

---

## 八、回退策略

- 打标签 `v1.1.0-rt2`（依赖 RT2 的最后一版），随时可回退。
- 自研版同时保留 RT2 路径：**有 RT2 就用 RT2 对拍自身**（自检），
  没有就用自研。等对拍稳定 N 个版本后，再彻底删掉 RT2 代码。

---

## ★ 附录（2026-09-27）：第二章「地图」预测的验证与踩坑

v2.2.0 把筛选指标从「问号数最多」换成「出怪房间占比最低」，于是要**离线生成整张 Act1 地图**。

### 复刻方法（IL 实证）

```csharp
var rng = new Rng(RunRngSeed(seed), "act_1_map");
var map = new StandardActMap(rng, ModelDb.Act<Overgrowth>(),
                             isMultiplayer: false, shouldReplaceTreasureWithElites: false,
                             hasSecondBoss: false, mapPointTypeCountsOverride: null,
                             enablePruning: true);
```

- `StandardActMap..ctor`：`_mapLength = act.GetNumberOfRooms(mp) + 1`（Overgrowth 的
  `BaseNumberOfRooms` = 15，多人 −1）；`_pointTypeCounts = ov ?? act.GetMapPointTypes(_rng)`；
  然后 `GenerateMap → AssignPointTypes → PruneAndRepair(CenterGrid/Spread/Straighten)`。
- `Overgrowth.GetMapPointTypes(Rng)`：**先** `NextGaussianInt(7,1,6,7)` 抽**精英数**，
  **再** `StandardRandomUnknownCount` 抽**问号数**，`new MapPointTypeCounts(问号, 精英)`。
- `AssignPointTypes` 用 `ListExtensions.StableShuffle`（= 先按 `coord` 排序再 Fisher-Yates）
  分配固定行，再 `AssignRemainingTypesToRandomPoints` 把 休息/商店/精英/问号 填进剩余格子。

### 验证（`QnProbe mapval`）

地面真值 = `saves/history/*.run` 里 `map_point_history[act][i].map_point_type`（含首格 ancient、
末格 boss）。判定 = 我们生成的地图里，是否存在一条**沿 `MapPoint.Children` 边、类型序列逐项相同**
的路径（DAG 记忆化 DP；错误图里碰巧存在 15 位序列的概率 ≈ (2/7)^15 ≈ 1e-8）。

**结果：42 个样本命中 28 个（67%），失败的每一条都只差 1~2 位（共 15~17 位）。**
⇒ 地图的**形状（格子+连线）100% 正确**，只有个别格子的 `PointType` 不同。

### ★★ 根因（P6 验收后订正）：**是"装了别的模组"**

2026-09-27 的 P6 干净环境验收给出了决定性证据：
**只启用 JimiRoll 时，游戏内算出的地图统计与离线复算 4/4 完全一致**
（`000000N4QW0D` 12/44、`000000N5YC0H` 13/44、`000000N6R9X4` 20/56、`000000N8LYH1` 14/46，全等）；
而在加载了 RitsuLib / UnlockTheSpire / Exusiai 的环境里同一颗种子会出现偏差
（`000000N6R9X4` 进程内曾算出 16/54）。

⇒ **偏差主要来自"其它模组在运行期改动了 act 内容"**（RitsuLib 的
`Dynamic act content patching initialized for 5 act type(s)` 属于这类行为），
而不是纯随机的哈希抖动。下面那条哈希机制是**候选解释之一**，保留备查。

### ★ 根因（候选机制）：游戏的地图生成可能不是完全可复现的

- `MapPoint` 重写了 `Equals(MapPoint)` 却**没有重写 `GetHashCode`**（探针 `dump` 已确认），
  而地图生成/裁剪里大量使用 `HashSet<MapPoint>`（`Children`、`parents`、`startMapPoints`…）。
  于是"哪些格子被当成重复项"取决于**对象身份哈希**，而身份哈希是 .NET 按线程本地随机种子
  惰性分配的（`hashprobe` 实证：不同线程的 `new object().GetHashCode()` 序列完全不同）。
- 实测直接抓到抖动：同一颗种子 `000000M8LTZB` 在同一局游戏里先后算出 `14/47` 和 `12/48`；
  搜索时的"三连"诊断也抓到 `27/60 → 23/59 → 23/59`、`16/50 → 18/49 → 18/49`。
- **推论**：一部分种子没有"正确的怪物占比"可言。

### 对策（三道闸）

1. **搜索侧**：同一颗种子连算 3 次，只要有一次不一致就整颗淘汰。
2. **交付侧**：进「自定义开局」时在**游戏主线程**复核 2 次，两次一致且 ≤ 门槛才交付，
   否则丢掉换下一个（最多 12 个）。玩家这一局的地图就在主线程生成，这一步的值才作数。
3. **黄金自检**：6 颗已知种子的 `(怪物格, 总格)` 必须一致（干净环境下由探针反复核对过）。

### ⚠️ 注意：装别的模组会改变地图

本机同时装了 **RitsuLib**（日志里有 `Dynamic act content patching initialized for 5 act type(s)`）
和 **UnlockTheSpire**。装了它们之后，部分种子在**游戏进程内**算出来的地图与**干净探针**
算出来的不一样（同一颗种子：进程内 `9/44`、探针 `12/44`；另一个 `15/51` vs `20/54`，且流程内
自洽）。所以：

- 离线探针只能当"量级参考"，**不能当验收标准**；
- 真正的验收标准是**游戏主线程上的复核值**（也就是模组交付时用的那个）。

### 工具命令速查

| 命令 | 作用 |
|---|---|
| `QnProbe map <起种子> <数量>` | 地图统计 + 占比分布 + 与问号数的相关性 |
| `QnProbe mapcdf <起种子> <数量>` | 只出占比 CDF（Stage2 通过率） |
| `QnProbe mapval [上限] [最短路径]` | 对真实存档做路径验证 |
| `QnProbe combo <种子>` | 穷举 32 组构造参数，看哪组能得到某个 `(怪物,总格)` |
| `QnProbe threadcmp <起种子> <数量>` | 主线程 vs 新线程逐颗比对 |
| `QnProbe nondet <种子>` / `hashprobe` | 抖动测试 / 身份哈希按线程随机性的实证 |