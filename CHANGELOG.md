# 更新日志

本项目遵循 [语义化版本](https://semver.org/lang/zh-CN/)：`主版本.次版本.修订号`。

---

## [未发布]

### 计划中
- P6：发布前验证清单 + GitHub

详见 `docs/正式化方案.md`。

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
