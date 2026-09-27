# 基米roll种器（JimiRoll）

> 也许是最基米的roll种器喵

**不需要任何其它模组。** 装它自己就能用（v2.0.0 起已彻底去掉对 RolltheSpire2 的依赖）。

---

## 它筛什么

| 条件 | 值 |
|---|---|
| 开局 | 涅奥骨骰（`NeowsBones`） |
| 骨骰给出的两个遗物 | 松动羊毛剪 `PrecariousShears` + 新叶 `NewLeaf` |
| 最终诅咒 | 愧疚 `Guilty` |
| 第一章**出怪房间占比** | **≤ 30%**（越低越好；全服平均约 43%） |
| 角色 / 难度 | 沉默猎手 / 进阶 10 |

---

## 怎么用

1. 把 `JimiRoll` 文件夹放进游戏 mods 目录
2. 在 mod 列表里启用 **基米roll种器**（不需要任何其它模组）
3. 进游戏，回一次主菜单 —— 它就自动开始干活了

**之后什么都不用做。** 储备池满了它就空闲待命；你每开一次「自定义开局」它取走一个种子，
储备掉到 9 个，下次回主菜单自动补上。

### 取种子

打开 **自定义开局** 界面 → 种子输入框里已经自动填好了。
退出来再进，会换成下一个（上一个已经被取走）。

储备池的完整清单也在系统剪贴板里，可以直接 `Ctrl+V`。

---

## 状态面板

进「自定义开局」界面时，种子框正下方会挂一行小字：

```
基米roll种器　储备 10/10　·　搜索中 14 张/秒　｜　本局 000000LQBCPA（怪物 9.0%）
```

从左到右是：储备还剩几个、搜索在不在跑（跑多快）、这一局用的是哪个种子。

它是**只读的** —— 不点它、不挡操作，也不会动游戏原有的界面。
搜索是后台跑的，所以数字会自己跳动。

不想看到它就改 `settings.json` 的 `"ShowStatusPanel": false`；
想临时关掉就建个空文件 `%APPDATA%\SlayTheSpire2\JimiRoll\DISABLE_PANEL.txt`。

---

## 数据文件

都在 `%APPDATA%\SlayTheSpire2\JimiRoll\`：

| 文件 | 内容 |
|---|---|
| `seeds_found.json` | 储备池（恒定 ≤ 10 个，按怪物占比从低到高排） |
| `seeds.txt` | 储备池的纯文本清单，一行一个 |
| `seeds_used.json` | 归档：用掉的 + 超上限移出的，只增不删 |
| `cursor.json` | 扫到哪个位置了（跨会话续扫，不重复） |
| `JimiRoll.log` | 运行日志 |
| `settings.json` | 可调参数 |

（取走的种子**不会丢**，全部归档在 `seeds_used.json`。）

---

## 排障

### 想让它暂时别跑

双击 mod 目录下的 `Pause.cmd`（再双击恢复）。

它做的事就是在数据目录下建/删一个 `DISABLE_SEARCH.txt`。回到主菜单即生效，不用重启。

### 看日志

```
%APPDATA%\SlayTheSpire2\JimiRoll\JimiRoll.log
```

正常启动长这样：

```
[23:34:25] [INF] ==================== JimiRoll 启动 ====================
[23:34:25] [INF] [设置] 已加载: ...\settings.json
[23:34:25] [INF] 设置: 储备池=15; 问号>=12; CHARACTER:SILENT A10; 线程=1; 单次上限=3分钟; 面板=开
[23:34:25] [INF] 条件: 涅奥骨骰=RELIC:NEOWS_BONES; 遗物=...; 诅咒=CARD:GUILTY; 第一章问号>=12
[23:34:25] [INF] 运行模式: 默认自动运行（储备池不满就补搜，满了就空闲待命）
[23:34:25] [INF] 储备池 15/15（队首 00000004ME1V 问号14）｜归档 94
[23:34:25] [INF] 种子哈希: ✓ 自研 XxHash64 与游戏 GetDeterministicHashCode 在 2007 个样本上完全一致
[23:34:25] [INF] ✓ 自检通过（种子哈希与游戏一致）
[23:34:25] [INF] [版本] 探测结果 = "0.111.0"
[23:34:26] [INF] [authority] BonesEligibleRelics = 28 个  HasExactBonesPools = True
[23:34:26] [INF] RT2 接线状态: ✓ 已就绪
[23:34:26] [INF] ✓✓ 全部就绪 —— 储备池不满时会自动补搜
[23:34:26] [INF] 储备池已满（15/15）→ 不搜索，空闲待命。
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
`%APPDATA%\SlayTheSpire2\JimiRoll\DISABLE_SEED_PATCH.txt` 单独关掉它。

### 状态面板没出现

看日志里有没有 `[面板]` 开头的行：

- `[面板] 未挂载状态面板：现场没有任何带字体覆盖的 MegaLabel…` → 游戏改版了，
  界面里找不到可复用的字体，就干脆不挂（**不影响筛种和自动填种子**）。
- 建个空文件 `%APPDATA%\SlayTheSpire2\JimiRoll\DISABLE_PANEL.txt` 可以主动关掉它。

---

## 改条件

### 可调参数（不用重新编译）

改 `%APPDATA%\SlayTheSpire2\JimiRoll\settings.json`：

```jsonc
{
  "ReserveCapacity": 10,          // 储备池容量（恒定维持这个数量）
  "MaxMonsterPercent": 30,        // ★ 第一章出怪房间占比上限（越小越好、越难找）
  "CharacterKey": "CHARACTER:SILENT",
  "Ascension": 10,
  "PlayersCount": 1,
  "MaxRunMinutes": 3,             // 单次搜索时间上限
  "WorkerCount": 0,               // 0 = 自动（min(逻辑核-1, 8)）
  "LogLevel": "Info",             // Debug / Info / Warn / Error / Off
  "LogLanguage": "",              // zh / en；留空则跟随游戏语言
  "ShowStatusPanel": true,        // 是否显示「自定义开局」界面上的状态小面板
  "PanelFontSize": 10             // 面板字号
}
```

改完**重启游戏**生效（配置在启动时读一次）。
数值写得不合理会自动纠正回默认值，并在日志里说明。

### 日志

日志带级别标签，方便直接筛选：

```
[23:27:38] [INF] ==================== JimiRoll 启动 ====================
[15:41:42] [INF] 储备池 10/10（队首 000000LQBCPA 怪物占比9.0%）｜归档 7
[23:27:38] [WRN] 储备池里有 2 条证据不完整（排序已靠后，不会优先被取走）
[23:27:38] [ERR] ...
```

- **排障时**把 `LogLevel` 调成 `Debug`，会打印储备池完整清单、冒烟测试的完整分布等细节
- **不想看日志**就设成 `Off`
- 日志文件超过 **2 MB** 会自动轮转到 `JimiRoll.log.1`，不会无限增长

### 筛种条件本身（需要重新编译）

哪些遗物、哪个诅咒**故意**留在代码里 —— 这部分不该随便改，改错代价大。
在 `src/Conditions.cs`：

```csharp
public const string RequireRelicKey      = "RELIC:NEOWS_BONES";       // 涅奥骨骰
public static readonly string[] RequiredBonesRelics = {               // 骨骰要给的两个
    "RELIC:PRECARIOUS_SHEARS",   // 松动羊毛剪
    "RELIC:NEW_LEAF",            // 新叶
};
public const string RequiredFinalCurseKey = "CARD:GUILTY";            // 愧疚
```

改完 `dotnet build -c Release` 即可。


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
   │ ① 骨骰判定（纯自研，约 40 万种子/秒）
   │    涅奥给骨骰 → 遗物 = 松动羊毛剪 + 新叶 → 诅咒 = 愧疚
   │    只留约 1/4 万
   ▼
   │ ② 生成第一章地图、数出怪房间占比（约 14 张/秒/线程）
   │    要求 ≤ MaxMonsterPercent，并且连算 3 次结果必须一致
   ▼
   │ ③ 交付前在【主线程】再复核 2 次（这一步才是最终保证）
   ▼
命中 → 储备池 → 进「自定义开局」时取队首填进种子框
```

两级漏斗的排序是刻意的：**最便宜的放前面**。第①级只放 1/4 万的种子过去，
所以极慢的地图生成实际只承担很小的负载（实测储备 10 个约 30-60 秒凑齐）。

---

## 已知限制（务必读一下）

### 1. 别的模组会影响地图生成

**同一颗种子，在"只装 JimiRoll"和"装了一堆模组"两种环境下，算出来的怪物占比可能不一样。**

- 实测（P6 验收）：只启用 JimiRoll 时，游戏内算出的值与我方独立离线复算**4/4 完全一致**；
  而同时加载 RitsuLib / UnlockTheSpire / Exusiai 时，同一颗种子会出现偏差
  （例：`000000N6R9X4` 进程内算出 16/54，干净环境与离线复算都是 20/56）。
- 原因在于别的模组会在运行期改动 act 内容（RitsuLib 日志里的
  `Dynamic act content patching initialized for 5 act type(s)` 就是这类行为）。

**所以本模组在"交付给你之前"会在游戏主线程上再复核一遍** —— 玩家这一局的图就是在主线程、
在同一个模组环境下生成的，那一次的值才作数。复核不过的种子直接丢掉换下一个。
搜索侧另外还有「连算 3 次必须一致，否则整颗淘汰」的前置过滤。

⇒ 你拿到手的每一颗种子，都是"在即将开始这一局的同一个进程、同一个线程、同一个模组环境下复核通过"的。
如果你觉得某一局的怪明显偏多，退出来重进会换下一个种子（那一个已经被归档，不会再给你）。

### 2. 不要开多线程

`WorkerCount` 保持 **1**（默认）。实测把它提到 8 之后，游戏在一次搜索里**无声无息地退出了**
（`godot.log` 里既没有异常也没有退出日志，两次都是；单线程则稳定）。
单线程也够用 —— 储备池从 0 填到 10 个只要约 60 秒。

---

## 发布前验收

见 [`docs/发布前验收清单.md`](docs/发布前验收清单.md)：打包卫生、自检、搜索链路、
干净环境、真实存档交叉验证、健壮性，六类全部通过并附实测日志。

---

## 致谢与依赖

- 【v2.0.0 起已不再依赖 RolltheSpire2】种子预测、地图生成全部自研。
- 早期的种子预测思路参考过 **[RolltheSpire2](https://steamcommunity.com/sharedfiles/filedetails/?id=3755166888)**（FalseOcean），在此致谢。

---

## 许可证

[MIT](LICENSE) —— 随便用喵，作者不担责。

使用本模组造成的任何后果（存档异常、游戏崩溃等）由使用者自行承担。
