# JimiRoll —— 上手与验证 runbook

> 我（AI）在本轮**无法执行 shell**（连续三轮 `pwsh` 被拒），所以下面每一步都需要你来跑。
> 所有验证脚本都已写好，**你只需要复制粘贴命令**。
> 设计上刻意做到：**验证失败会大声报错并中止，绝不静默给出错误种子**。

---

## 第 0 步：先看仓库里有什么

```
杀戮尖塔/
├── JimiRoll/                  新 Mod（本目标）
│   ├── JimiRoll.csproj
│   ├── JimiRoll.json          manifest（dependencies: RolltheSpire2）
│   ├── SeedCodec.cs               34 进制种子编解码 + 边界自检
│   ├── XxHash64.cs                自研哈希（会被对拍，写错也不会出错结果）
│   ├── GameInterop.cs             与游戏交互 + ★关键对拍
│   ├── Reflect.cs                 ★ 按【参数名】匹配的反射构造器/赋值器
│   ├── Rt2Binding.cs              ★ 在运行时自动发现 RT2 API
│   ├── Conditions.cs              ★ 你要的条件写在这里
│   ├── SeedStore.cs               命中落盘 + 游标持久化
│   ├── SearchDriver.cs            两级漏斗
│   ├── MainFile.cs                入口（不注册任何 UI）
│   └── RUNBOOK.md                 本文档
├── _tools/
│   ├── probe/                     反射探针（已加 --rt2 / vis / subs / enums / mgr / proto）
│   ├── QnProbe/                   问号数离线验证器（独立小程序）
│   ├── qnprobe.ps1                QnProbe 快捷入口
│   └── notes/
│       ├── RollTheSpire2-architecture.md   RT2 架构逆向（含全部一手证据）
│       └── JimiRoll-design.md          设计备忘录 + 待补空白清单
```

### 关键设计：为什么 Mod 能"自动适配" RT2 的 API

有两个地方我<b>没有</b>硬编码，而是做成运行时发现 —— 因为我在没有编译器的环境里写的这些代码：

1. **`Reflect.cs` 按参数名匹配构造函数**，不按位置。
   `NeowSearchFilter` 有 12 个参数，若作者调整顺序，按位置的反射会<b>静默传错值</b>；
   按名字匹配则会在参数缺失时大声报错。这是"宁可失败也不要给错种子"的落实。

2. **`Rt2Binding.cs` 在启动时自动探测**：类型是否存在、ModelKey 怎么造、
   有哪些构造函数、枚举有哪些取值 —— 全部写进诊断日志。
   所以即使我猜错了某个名字，你会看到一份「缺了什么、有哪些候选」的清单，而不是一个静默失效的搜索器。

3. **`ModelKey` 优先从游戏的 `ModelDb` 按类名直查**（拿真实实例的 `Id`），
   字符串拼法只作为回退 —— 因为 `ModelKey` 的字符串表示法我们没见过，猜错会静默拿不到条件。

---

## 第 1 步：验证「问号数」链条（最优先，纯离线）

**要看到的**：

| 检查项 | 期望 |
|---|---|
| 34 进制往返 | `✓ 20000 次往返全部一致` |
| `Format(0)` | `000000000000` |
| `Format(SpaceSize-1)` | `ZZZZZZZZZZZZ`（同时确认字节序）|
| **种子哈希对拍** | 5 个样本「游戏 = 我们」，逐位相同 |
| 问号数分布 | 全部落在 `[10,14]` |

**如果哈希对拍失败**，说明 `XxHash64` 那条路不对（但那不影响正确性 —— 只是没加速）。
先别继续，把输出发我。

然后量分布和速度：

```powershell
& "_tools\qnprobe.ps1" scan PJJBSGHQ1N2Q 500000
& "_tools\qnprobe.ps1" find PJJBSGHQ1N2Q 2000000 14 10
```

**要拿到两个数字**：
1. **问号 = 14 的实际占比**（预期约 1/5，但高斯截断会偏斜）
2. **每秒能算多少种子**

这两个数字决定「第①级预筛是否真的划算」。

---

## 第 2 步：补齐 RT2 的三个 API 空白（决定 Phase 1 路线）

`_tools/probe` 已经加好了 `proto` 命令，一条命令问全部：

```powershell
& "_tools\probe.ps1" --rt2 proto
```

它会打印：
- 游戏侧：`StringHelper` / `RunRngSet` / `SeedHelper` / `Rng` / `MapPointTypeCounts` / `MapPointType`
- RT2 侧：`LegacySearchInput` / `RuntimeProfileId` / `RuntimeVersionResolution` / `IRuntimeProfile`

**我要从输出里找三件事**：

### 2.1 `ModelKey` 怎么构造（★ 最高优先级）

```powershell
& "_tools\probe.ps1" vis  "MegaCrit.Sts2.Core.Models.ModelKey"
& "_tools\probe.ps1" dump "MegaCrit.Sts2.Core.Models.ModelKey"
```

要判断：是 `ModelKey.Parse("RELIC:PRECARIOUS_SHEARS")` 这种字符串工厂，
还是要从 `ModelDb` 查。**这决定条件怎么写。**

### 2.2 `RuntimeProfileId` 是不是枚举、`Beta111` 怎么取

`proto` 的输出里会直接列出枚举成员。

### 2.3 ★ `LegacySearchInput` 之后那条链能不能走通

`ProductionSearchSession` 的构造需要一个 **internal** 的 `FamilyExecutionPlan`。
所以关键是搞清「谁把 `LegacySearchInput` 变成 `ProductionSearchSession`」：

```powershell
& "_tools\probe.ps1" --rt2 usecase "CompileLegacy"
& "_tools\probe.ps1" --rt2 usecase "CompileExact"
& "_tools\probe.ps1" --rt2 usecase "IdentityCpuPlan"
```

⚠️ 现有 `usecase` 只扫 `DeclaredOnly` **方法**，扫不到**构造函数**。
如果找不出来，需要给探针的 `usecase` 加上「也扫 `.ctor`」的能力 —— 告诉我，我来改。

**这一步的结论决定 Phase 1 走哪条路**：

| 路线 | 条件 | 代价 |
|---|---|---|
| **A** | 那条链有 public 入口 | 最干净，直接引用 |
| **A'** | 只有 internal 入口 | 反射调用，RT2 重构时要修 |
| **A''** | 入口被封死 | 复用 RT2 自己的会话，用 Harmony 去掉「关面板即取消」+ 隐藏 UI |

---

## 第 3 步：编译 Mod

```powershell
cd "C:\Users\rebeccon\Desktop\丁嘉跃\杀戮尖塔\JimiRoll"
dotnet build
```

⚠️ 两个已知的沙箱/环境坑（`AllCardsPurge` 踩过）：
1. 部署到 `D:\steam\...\mods\` 在工作区之外，**需要 `danger-full-access`**；
   权限被拒时 MSBuild 会「先删后写失败」，导致 mods 里的 dll 消失 —— **构建后核对**。
2. 只想编译不部署：`dotnet build -p:SkipDeploy=true`

---

## 第 4 步：进游戏验证

1. 启动游戏，看日志
   `C:\Users\rebeccon\AppData\Roaming\SlayTheSpire2\logs\godot.log`
   应能看到 `JimiRoll` 的加载与自检输出。
2. **自检必须全绿**。任何一条 `[GameInterop]` 或 `[SeedCodec]` 的问题都会打到这里。
3. 确认 `%APPDATA%\SlayTheSpire2\JimiRoll\` 下生成了
   `cursor.json`（游标）和 `seeds_found.json`（命中）。

---

## 当前状态（诚实说明）

| 项 | 状态 |
|---|---|
| 问号数链条（`act_1_map` + 2 次 NextDouble）| **代码已写，未运行验证** |
| 34 进制编解码 | 代码已写，含边界自检 |
| 自研 XxHash64 | 代码已写，**带强制对拍**，错也不会出错结果 |
| RT2 条件构造 | **未完成** —— 卡在第 2.1/2.3 的 API 空白 |
| 后台搜索循环 + 落盘 | 骨架已写，依赖上一步 |
| 编译 / 进游戏 | **从未做过** |

**最大风险提示**：RT2 的 `FamilyExecutionPlan` 是 internal，
原计划「`LegacySearchInput` → `CompileLegacy` → `new ProductionSearchSession`」这条链
**后半段可能碰不到**。第 2.3 步就是为它准备的。如果确认走 A''，
方案要改成「复用 RT2 会话 + Harmony 解除关面板取消」。
