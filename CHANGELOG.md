# 更新日志

本项目遵循 [语义化版本](https://semver.org/lang/zh-CN/)：`主版本.次版本.修订号`。

---

## [2.2.0] — 2026-09-27

### 💥 筛选指标大改：「第一章问号数最多」→「第一章出怪房间占比最低」

**用户反馈**：问题不在问号房间的数量，而在于**出怪房间（Monster）占整个楼层格子的比重**。
实测也证实了这一点 —— 两者几乎不相关，而且「问号 ≥14」这一档的怪物占比**最低值反而更差**：

| 问号数 | 样本 | 怪物占比平均 | 最低 |
|---|---|---|---|
| 11 | 78 | 44.23% | 34.04% |
| 12 | 118 | 42.55% | 30.00% |
| 13 | 77 | 43.05% | 31.11% |
| **14** | 19 | 43.90% | **39.62%** |

⇒ 旧的问号预筛**把怪物占比最低的种子直接排除掉了**，已整条删除。
「第一个问号房必须是变形灵林谷」那条条件也按要求删掉（代码文件已移除）。

### ✨ 新增：`ActMapPredict` —— 直接生成整张 Act1 地图来数房间

- 复刻游戏：`new Rng(RunRngSeed(seed), "act_1_map")` + `new StandardActMap(rng, Act<Overgrowth>(),
  多人?, replaceTreasureWithElites:false, hasSecondBoss:false, null, enablePruning:true)`
- `Overgrowth.GetMapPointTypes(Rng)` 的 IL 实证：先 `NextGaussianInt(7,1,6,7)` 出**精英数**，
  再 `StandardRandomUnknownCount` 出**问号数** —— 顺带证明旧的问号预筛连抽取次序都是错的。
- 新增黄金自检：6 颗已知种子的 `(怪物格, 总格)` 必须全部命中，否则告警。

### ✨ 新增：`MaxMonsterPercent` 设置项（默认 30）

实测 CDF（2000 颗种子的 Act1 地图；总格 37~64、怪物占比 24%~58%、平均约 43%）：

```
≤24% 0.05%   ≤26% 0.05%   ≤28% 0.25%   ≤30% 0.60%
≤32% 1.65%   ≤33% 2.10%   ≤34% 3.20%   ≤35% 4.10%
≤38% 10.5%   ≤40% 21.5%   ≤45% 63.4%
```

单张地图生成 ~70ms（比骨骰判定贵约 3 万倍），所以第②级是真正的瓶颈。

### ⚡ `WorkerCount` 默认改成 0 = 自动（`min(逻辑核-1, 8)`）

v2.0 默认 1 是因为瓶颈在 RT2 的串行 Predict；现在瓶颈是纯 CPU 的地图生成、没有共享锁，
多线程是线性收益（8 线程 ≈ 100+ 张/秒）。

### 🛡️ 交付前的**主线程复核**（这一步是最终保证）

进「自定义开局」界面时，在**主线程**上把队首种子重新算 2 次：两次必须一致且都 ≤ 门槛，
否则丢掉换下一个（最多试 12 个）。

**为什么必须有**：游戏的地图生成**不是完全可复现的** —— 同一颗种子同一局游戏里
前后算出了 `14/47` 和 `12/48`；也抓到过连算三次 `27/60 → 23/59 → 23/59`。
根因是 `MapPoint` 重写了 `Equals` 但**没重写 `GetHashCode`**，而地图生成大量使用
`HashSet<MapPoint>`，于是结果取决于运行期状态。玩家这一局的地图是在主线程生成的，
所以只有主线程算出来的值才作数。搜索侧另外还有「连算 3 次必须一致否则淘汰」的前置过滤。

### 📊 实测战绩（本机 16 核）

储备 10 个约 **30~60 秒**凑齐；命中样本的怪物占比集中在 **24%~30%**（全服平均 43%）；
托管内存 60~105 MB 稳定不涨。

### 🧹 其它

- `SeedHit` 字段由 `Act1UnknownRooms` 换成 `Act1Monsters / Act1Total / Act1MonsterPercent`；
  储备池按**怪物占比从低到高**排序（以前是问号数从高到低）。
- 旧口径的记录在启动时会被补算统计；补不出统计的按 `stale` 归档，不再参与排序。
- 状态面板、日志、`seeds.txt` 的展示口径全部改成"怪物 x/y = z%"。
- 新增离线验证工具命令：`QnProbe mapcdf`（占比 CDF）、`threadcmp`（跨线程一致性）、
  `nondet`、`hashprobe`、`combo`（构造参数穷举）、`mapval`（对真实存档的行走路径验证）。

---

## [2.1.0] — 2026-09-27

### 🚧 「第一个事件」条件**已实现但默认关闭**（2026-09-27）

条件「第一个问号房间是事件，且该事件为变形灵林谷」**代码写好了，但预测还没验证通过**，
**默认关闭**（`settings.json` 的 `RequireFirstEvent = false`）。开启它会给出**错误的种子**。

**已独立验证通过的部分：**
- ✅ 事件池 = 31 个，与游戏自己的 `GenerateRooms` 逐项比对**完全一致**
- ✅ 问号房间类型的掷骰（赔率 Monster .10 / Treasure .02 / Shop .03，默认 Event）——
  真实存档样本 `PBMP7ETLAR0F`：第 1 个问号房我们算 roll=0.1176 ≤0.15「不是事件」，
  实际那一格确实没出事件（玩家在第 2 个问号房才遇到事件）—— **对上了**
- ✅ **Act 1 预测**（`GetRandomList` + `act_selection` 独立流）—— 真实存档 **3/3**

**❌ 还没解开的：** 「第一个事件是队列里的哪一张」。
用真实存档里 4 个 Act1=Overgrowth 且第一个事件房就是第一个问号房的样本验证，**0/4**：
```
seed 00000009DF8P  实际=ThisOrThat            我们预测=SunkenStatue
seed 00000001J8RG  实际=JungleMazeAdventure   我们预测=WhisperingHollow
seed 00000000Q6L2  实际=TheFutureOfPotions    我们预测=TeaMaster
seed PBMP7ETLAR0F  实际=SapphireSeed          我们预测=(第一个问号房不是事件)
```
已经排除的假设（大搜索 12 条 `RunRngType` 流 × k(0..300) × 列表内偏移(0..5) ×
24 种池子顺序/epoch 组合，用**游戏自己的 `UnstableShuffle`**）—— **全部对不上**。

⇒ 问题不在「流 / k / 池子顺序 / epoch 过滤」。最可疑的是
`EnsureNextEventIsValid` 会跳过 `!IsAllowed(runState) || 已访问` 的事件
（`MorphicGrove.IsAllowed` 要求 `Gold >= 100` 且牌组满足条件），
或者 `MarkVisited(Event)` 会推进 `eventsVisited` —— 这条链还没理顺。

**地面真值的来源（重要）**：游戏存档 `saves/history/*.run` 是 **JSON**，里面有
`seed` / `acts` / `map_point_history[*].player_stats[*].event_choices.title.key`，
**记录了每一格实际发生的事件**。这是比任何对拍都硬的真值，`_tools/QnProbe` 的
`actcheck` / `eventcheck` / `eventk` 就是基于它。

---

## [2.1.0] - 2026-09-27

### 新增条件：第一个问号房间必须是事件，且该事件为**变形灵林谷**（MorphicGrove）

> ⚠️ **该条件默认关闭**，原因见上面「未发布」一节。储备池容量已从 15 调整为 **10**。

#### 机制（全部逐条 IL 反汇编 + 真实存档实证）

**① 事件不是进房随机抽的，而是每个 Act 开局预生成一个洗过的队列**

```
ActModel.GenerateRooms(rng, unlockState, isMultiplayer):
    evts = AllEvents.Concat(ModelDb.AllSharedEvents)      // 顺序即洗牌输入顺序
    _rooms.events.AddRange(UnstableShuffle(evts, rng))    // Fisher-Yates
RoomSet.NextEvent = events[eventsVisited % Count]
```

⇒ **第一个事件 = 洗完队列的第一张。**

**② 这条队列用的是 `RunRngSet.UpFront`，前置消耗 k = 2**

```
RunManager.GenerateRooms:
    UnstableShuffle(SharedAncients.ToList(), UpFront)              // A-1 次
    foreach (act in Acts.Skip(1)) UpFront.NextInt(ancients.Count+1) // Acts-1 次
    for (i = 0; i < Acts.Count; i++) Acts[i].GenerateRooms(UpFront, ...)
```

实测 `SharedAncients = 1`（只有 Darv），存档里 `"acts": ["ACT.OVERGROWTH","ACT.HIVE","ACT.GLORY"]`
⇒ **k = (1−1) + (3−1) = 2**。

**③ 问号房间的类型是进房时掷骰决定的**

```
UnknownMapPointOdds 的赔率表：Monster 0.10 / Elite -1 / Treasure 0.02 / Shop 0.03，默认结果是 Event
累计判定：roll ≤0.10→怪物 | ≤0.12→宝箱 | ≤0.15→商店 | 否则→事件
⇒ P(事件) = 0.85
```

★ 保守且安全：即使 Shop 被黑名单屏蔽（`BuildRoomTypeBlacklist` 只在"该节点子节点全是商店"时屏蔽），
累计只到 0.12，`roll > 0.15` 仍然落在默认值 Event。**用 0.15 判定在两种情况下都正确。**

**④ Act 1 是哪一本 —— 这一步差点酿成"给出错种子"**

`MorphicGrove` **只在 Overgrowth 里**，而 Act 1 是 `[Overgrowth, Underdocks]` 里**随机抽的**：

```
StartRunLobby.BeginRunLocally(seed, ...):
    var rng = new Rng(GetDeterministicHashCode(seed), "act_selection");   // ★ 独立流，不消耗 UpFront
    var acts = ActModel.GetRandomList(rng, unlockState, isMultiplayer);
ActModel.GetRandomList: 没发现过的非默认 Act 优先，否则 rng.NextItem(unlocked)
```

**这是开发中真实踩到的坑**：第一版没考虑 Act 1，用户实测的一局
（seed `00000046UCKY`）Act 1 就是 `Underdocks` —— 那种种子**永远不可能**出变形灵林谷。
现已修好，并用**用户真实存档的三局**验证：

```
✓ 0000000BZAE2  预期=Overgrowth   实际=Overgrowth
✓ 00000046UCKY  预期=Underdocks   实际=Underdocks
✓ U4L48R8MJLU3  预期=Underdocks   实际=Underdocks
```

#### 验证

| 验证 | 结果 |
|---|---|
| 事件池 vs 游戏自己的 `GenerateRooms`（用同一个 rng 洗一遍，逐项比对） | **完全一致** |
| Act1 预测 vs **真实存档三局** | **3/3** |
| 旧条件自检（13 个已知命中种子） | **13/13 仍然通过** |
| 进游戏实测 | 命中 12 个 / 5.37 亿种子（1/4480 万）；补满 10 个用 **90 秒**；速度 ~6.0 M 种子/秒 |

命中证据现在长这样：

```
★ 命中 #12: 000000JZDDFE（问号 14）骨骰给出[RELIC:PrecariousShears+RELIC:NewLeaf] + 诅咒=GUILTY  ✓全中 ；
            Act1=Overgrowth；第一个问号房=事件（roll=0.4063）→ 第一个事件=MorphicGrove  ✓全中
```

#### 新增的开发工具（不随模组发布）

- **`ModelDb` 可以离线初始化** —— 这打开了"离线跑游戏自己的模型逻辑"这条路：
  `ModelDb.Init()` 会走 `ReflectionHelper.ModTypes`，那里第一句是
  `if (ModManager.State == 0) throw`。绕过方法：把 `ModManager` 的私有静态
  `<State>k__BackingField` 设为 `Initialized`，并把 `ReflectionHelper._modTypes` 填成空数组。
- `_tools/QnProbe` 新增 `events`（打印各 Act 的事件池）/ `actcheck`（用真实存档验证 Act1）。
- `QN_DOMAIN` 环境变量现在生效（可传 `World` / `All`）。

---

## [2.0.1] - 2026-09-27

### 第一章问号门槛 12 → 14

**先说结论：CPU 反而更快，内存没有变化。**

门槛的作用是「决定哪些种子进入第②级（骨骰判定）」：

| 门槛 | 扫描速度 | ②级调用次数 | ②级耗时 | 命中率（不含诅咒） |
|---|---|---|---|---|
| 12 | 1,357,385/s | 1,386,814 | 752 ms | 0.0189% |
| 13 | 3,119,441/s | 612,090 | 133 ms | 0.0089% |
| **14** | **3,861,912/s** | **122,523** | **29 ms** | 0.0021% |

（每档扫 2,000,000 个种子，离线基准 `QnProbe bench`）

- 门槛 12 会让 **69.4%** 的种子进第②级；门槛 14 只有 **6.4%** ⇒ 第②级调用少了 **11.3 倍**。
- 连第①级本身都更快了（619 ms → 427 ms），因为少了 11 倍的缓存污染。
- **⇒ 整体快 2.85 倍**，不是变慢。
- **内存**：两档在整段扫描期间的托管内存增长都是几 MB 量级（纯 GC 噪声）。
  真正的稳态占用是「两个池子（28 个遗物名 + 10 张诅咒）+ 储备池 ≤15 条」，
  **门槛改不改都一样**。

**代价只有命中率**：真实命中率（再 × 1/10 诅咒）从 1/5.3万 降到 1/47.6万。
但整体快 2.85 倍，所以**补满 15 个储备：0.58 秒 → 1.85 秒**，完全无感。

**实测验证**（进游戏）：

```
已扫 5,656,768  本轮命中 12  托管内存 69.0 MB
⇒ 命中率 1/471,397（离线预测 1/476,000，几乎完全一致）
⇒ 补满 15 个储备用了 2 秒
```

储备池里低于新门槛的旧种子会被自动移出归档（`seeds_used.json`，`Reason = below-threshold`）。

### 🐛 修复：状态面板的字号改了没反应

`PanelFontSize` 从 22 改到 12 却几乎看不出变化 —— **不是字号设得不够小，而是被踩掉了**：

```csharp
MegaLabel..ctor()  { _autoSizeEnabled = 1; _minFontSize = 8; _maxFontSize = 100; }  // 默认开自动缩放
MegaLabel..cctor() { _cachedParagraph = new TextParagraph(); }                      // 它不是 null
```

于是 `_Ready()` 里的 `AdjustFontSize()` 两个早退条件**都不成立**，
它会**按矩形把字号自动缩放到 8~100 之间**，把 `AddThemeFontSizeOverride` 设的值直接覆盖掉。

修法：`AutoSizeEnabled = false`（并把 `Min/MaxFontSize` 都钉到目标值做双保险）。

- 默认字号 22 → 12 → **10**
- 新增诊断日志，把**实际生效的字号**打出来：
  `（要求字号 10；实际生效 10/font_size 10；AutoSize=False）`
  —— 以后再出现"改了没反应"，这一行能立刻定位。

---

## [2.0.0] - 2026-09-27

### ★★★ 不再依赖任何第三方模组 —— RolltheSpire2 已完全去掉 ★★★

这是本项目最大的一次改动。骨骰判定整套自研实现（`src/NeowPredict.cs`），
`csproj` 的 `RolltheSpire2` 引用与 manifest 的 `dependencies` 都已删除，
**DLL 里已经搜不到 `RolltheSpire2` 这个字符串**。

#### 自研的四条判定链（全部逐条 IL 反汇编实证）

| # | 条件 | 依据 |
|---|---|---|
| ① | 涅奥是否给出骨骰 | `EventModel+<BeginEvent>d__28::MoveNext` 里 `set_Rng(new Rng(RunRngSet.Seed + slotIndex + hash("NEOW")))`；`Neow.GenerateInitialOptions` 第一次 `NextItem(CurseOptions)` 抽到的就是那个"特殊选项"；骨骰在 `CurseOptions` 里 ⇒ 抽到它 = 给出骨骰 |
| ② | 骨骰给的两个遗物 | `NeowsBones.AfterObtained`: `PlayerRng.Rewards.Shuffle(pool).Take(2)`（Fisher-Yates） |
| ③ | 骨骰给的诅咒 | 同处: `RunRngSet.Niche.NextItem(CurseCardPool 过滤+排序后的列表)`，**前置消耗 1 次** |
| ④ | 第一章问号数 | 早已自研（`act_1_map` 域） |

#### 遗物池 = 28 个，顺序至关重要

`Neow.AllPossibleOptions` = `CurseOptions(10) + PositiveOptions(14) + 单例(6)` = 30，
减去 `NeowsBones` 自己，再减去 `MassiveScroll`（它的 `IsAllowed` 是 `players.Count > 1`，
多人专属）⇒ **28**，与游戏内实测的 `BonesEligibleRelics = 28` 完全吻合。

#### 验证（全部可复现）

| 验证 | 结果 |
|---|---|
| 条件①：自研 vs RT2，200,000 个种子 | **一致率 100.0000%** |
| 条件①：**已上线的 JimiRoll.dll** vs RT2，100,000 个种子 | **一致率 100.0000%** |
| 条件②：13 个游戏内实测命中种子 | **13/13 通过** |
| 条件②③：假设搜索 14 个地面真值种子 | **14/14 全中**（`skipRelics=0`, `skipCurses=1`） |
| 条件②宏观命中率 200,000 种子 | 0.289%（理论 0.2646%，同量级） |
| **干净环境验收（无 RT2）** | **启动 / 自检 / 搜索 / 命中 / 填种子框 / 状态面板 全部正常** |

#### 施工中踩的三个坑（都写进代码注释了）

1. **`CardModel.CanBeGeneratedByModifiers` 是属性不是方法**（编不过才发现）。
2. **归一化比对漏了前缀**：`"RELIC:PRECARIOUS_SHEARS"` 必须能匹配类名
   `"PrecariousShears"`，第一版只去了 `_:.-` 却没去 `RELIC`，导致 13 个种子
   全被判「遗物不对」——**而日志里明明打印着正确的遗物**。
   ⇒ 教训：判定失败时必须把"它实际算出了什么"打出来。
3. **`skipCurses` 从没被真正确定过**：早期假设搜索的诅咒池只有 1 个元素，
   那条搜索是**空的**。直到进游戏跑起来，日志同时给出了真实诅咒池（10 张）
   和"13 个种子只有 1 个对"（纯随机水平），才发现 Niche 流前置消耗其实是 **1**。
   ⇒ 这正是内置自检存在的意义：**它把两个潜伏的 bug 都当场抓住了。**

#### 新增

- `src/NeowPredict.cs` —— 自研骨骰判定（含内置自检：13 个游戏内实测命中种子）
- 自检失败时**拒绝启动搜索**（宁可不能用，也绝不给出错种子）

#### 新增的开发工具（不随模组发布）

- `_tools/BonesProbe/` —— 独立对拍工具（刻意不引用 RT2）
- `_tools/rt2_recon.ps1` —— 一条命令把全部需要的 IL 打出来
- `_tools/QnProbe` 新增 `bonescmp` / `bonesentry` / `jimicmp` / `jimirelic` 四条对拍命令

#### 兼容性

`docs/去RT2化方案.md` 第七节分析了跨游戏版本的行为。
一句话：自研之后，**跨版本风险只剩「第一章问号数的 `act_1_map` 域参数」这一条良性风险**
（错了只会少算几个种子），而且它是**可以自己跟进的**，不再受第三方模组是否更新制约。

---

## [1.1.0] - 2026-09-26

### P5 —— 游戏内状态面板
- 「自定义开局」界面里，种子输入框正下方挂一行只读小字：
  `基米roll种器　储备 14/15　·　搜索中 3.5 M/s　｜　本局 00000004ME1V（问号 14）`
- 每秒自动刷新（搜索是后台线程在跑，数量会自己变）
- 新增设置项 `ShowStatusPanel`（默认开）与 `PanelFontSize`（默认 22）
- 新增运行期开关 `DISABLE_PANEL.txt`（建个空文件立刻停用，不用重编译）
- 新增 `Loc`（极简双语）与 `Loc.Rate`（速率格式化）

**这一版踩到的两个坑（都写进代码注释了）：**
1. `MegaLabel._Ready()` 第一句是 `MegaLabelHelper.AssertThemeFontOverride`，
   它在没有字体覆盖时**直接抛 `InvalidOperationException`**。
   Godot 的 `AddChild` 在父节点已入树时会**同步**调用子节点 `_Ready`，
   所以裸 `new MegaLabel()` 会**当场把界面打崩**。
   ⇒ 必须**先** `AddThemeFontOverride`、**再** `AddChild`。
2. Godot 4.5 的 `Control` 只有 `HasThemeFontOverride`，**没有** `GetThemeFontOverride`。
   取字体要用 `GetThemeFont(name)` —— 它的查找顺序是「自身 override → Theme → 祖先」，
   对一个确实持有 override 的控件调用，拿到的就是那个 override。

**安全性设计：** 只新增节点、绝不改动游戏已有节点；面板挂在屏幕本身而不是
`LineEdit` 的父容器（后者会被 Container 当成"要参与排版的孩子"，可能挤坏原有布局）；
`MouseFilter = Ignore` 不抢点击；任何一步失败都只记一条日志然后放弃，绝不影响筛种。

---

## [1.0.0] - 2026-09-26

### 工程（P0 / P1 / P2 / P3 / P4 已完成）

**P4 —— 日志分级 + 轮转 + 降噪**
- 新增 `Logger`：`Debug` / `Info` / `Warn` / `Error` / `Off` 五级，每行带级别标签
  （`[DBG]` / `[INF]` / `[WRN]` / `[ERR]`）
- **修复日志无限增长**：原来每行 `AppendAllText` 且从不清理，实测 11 小时涨到 87 KB。
  现在超过 2 MB 自动轮转到 `.1`
- `settings.json` 新增 `LogLevel` 与 `LogLanguage`（留空则跟随游戏语言）
- **降噪**：储备池全量清单、冒烟测试完整分布移到 `Debug` 级；
  `Info` 只留一行摘要（实测这两处是主要噪音来源）
- 8 处关键消息升级到 `Warn` / `Error`
- 日志语言的基础设施（`Logger.English` + 跟随游戏 `TranslationServer.GetLocale()`）
  已就位，消息本身的双语翻译留待后续增量补齐


### 工程（P0 / P1 / P2 / P3 已完成）

**P0 —— 去掉硬编码**
- 运行时不再写死任何盘符路径（原来有 3 处）；编译期路径可由
  `STS2_GAME_DIR` / `STS2_RT2_DIR` 环境变量覆盖
- 游戏版本号**实时探测**（三级：RT2 探测器 → `NGame.GetGameVersion` → `release_info.json`），
  全失败则明确报错并停止，**绝不使用猜测值**
- 默认自动运行，不再需要开关文件

**P1 —— 工程配置**
- 仓库结构：`src/`、`docs/`、`.gitignore`、`.gitattributes`
- MIT 许可证、完整 `README.md`、本 `CHANGELOG.md`
- `build.ps1` 一键打包 → `dist/<id>-<version>.zip`

**P2 —— 可调参数外部化**
- 问号门槛、角色、难度、储备池容量、线程数、单次时间上限
  改为从 `settings.json` 读，改完不用重新编译
- 数值不合理会自动纠正并记日志
- 筛种条件本身（哪些遗物、哪个诅咒）**故意**仍留在代码里

**P3 —— 反射改为编译期强类型引用**
- `Rt2Binding` 从 400 行反射改为直接引用 RT2 类型：
  **名字写错 = 编译不过**，而不是运行时静默失效
- 删除已无人调用的 `src/Reflect.cs`（159 行死代码）
- `GameInterop` 的种子哈希改为直接调用 `StringHelper.GetDeterministicHashCode`
- 代码量 3542 → 1952 行（净 -267 行）

**P3 过程中发现并记录的事实**
- `ModelKey` 是 **struct**（值类型），不是类 —— 反射时被装箱所以一直没暴露。
  因此不能用 `== null` 判断，要用 `IsValid`。
- `SeedPredictionRequest` 构造函数的第 13 个参数类型
  `RolltheSpire2.Core.Prediction.TrustedRootHashInput` 是 **internal**，
  导致构造函数对外不可见、**编译期无法调用**。
  ⇒ 该处保留反射，按**参数名**匹配填入，并在启动时校验构造函数签名。
- `BaseGameModelKeys.Characters` 只有 5 个原版角色：
  `Ironclad / Silent / Defect / Necrobinder / Regent`（观者 Watcher 是 Mod 角色，不在其中）。



首个可用版本。

### 功能
- **固定条件筛种**：涅奥骨骰开局，骨骰给出的两个遗物必须是松动羊毛剪（`PrecariousShears`）
  与新叶（`NewLeaf`），最终诅咒必须是愧疚（`Guilty`），且第一章问号房间 ≥ 12 个。
- **两级漏斗**：
  - 第①级按 `act_1_map` 随机域算第一章问号数（约 370 万种子/秒）
  - 第②级交给 RolltheSpire2 做骨骰遗物/诅咒的精确判定
  - 实测整体约 45 万种子/秒，比不用第①级快约 19 倍
- **储备池机制**：储备池恒定维持 15 个种子。进入「自定义开局」界面时自动取走队首那个
  （取走即从储备池删除并归档到 `seeds_used.json`），掉到 14 个后下次回主菜单自动补搜。
- **种子框自动填充**：取走的种子直接填进自定义开局的种子输入框，不覆盖手动输入。
- **自动运行**：装好即用，无需开关。储备池满了就空闲待命，不占资源。
- **系统剪贴板**：启动时把储备池清单放进剪贴板，游戏里可直接 Ctrl+V。

### 稳健性
- 自研 XxHash64 与游戏 `GetDeterministicHashCode` **强制对拍**，不一致自动回退到游戏实现
  （写错不会导致错误种子，只会失去加速）。
- 游戏版本号**从游戏/RT2 实时探测**，探测失败则明确报错并停止，绝不使用猜测值。
- 搜索有双重上限：储备池满 / 单次 3 分钟。防止内存持续上涨。
- 结果去重（储备池 + 归档全局去重），并保留优先级排序（问号多 → 证据确凿 → 序号小）。
- 所有 Harmony 补丁包裹异常，绝不把异常抛回游戏回调。
- 排障时可用 `Pause.cmd` 运行时暂停搜索，无需重新编译。

### 依赖
- RolltheSpire2 1.2.0+

### 已验证
- 游戏版本 0.111.0
- 种子哈希链条与游戏逐值一致（2007 个样本）
- 储备池「取走 → 补搜 → 回满」循环跑通（多次会话，跨数小时）
- 托管内存稳定在 66~68 MB，长时间运行无增长
