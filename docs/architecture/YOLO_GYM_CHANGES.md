# MapSimulator — YOLO 数据集生成 & RL Gym IPC 改动文档

> **版本基础**：commit `96b15108`（staging merge）  
> **改动提交**：commit `5efdbb85`（本地开发）+ 若干未提交新文件  
> **日期**：2026-05-05  
> **目标**：为 YOLO 怪物检测模型训练提供自动标注数据，并为 PPO 强化学习 Agent 提供 IPC 通信接口。

---

## 目录

1. [新增文件](#1-新增文件)
2. [改动文件](#2-改动文件)
3. [系统架构概览](#3-系统架构概览)
4. [Gym IPC 通信协议](#4-gym-ipc-通信协议)
5. [观测空间说明（14 维）](#5-观测空间说明14-维)
6. [YOLO 数据集生成流程](#6-yolo-数据集生成流程)
7. [热键速查](#7-热键速查)
8. [已知事项与后续工作](#8-已知事项与后续工作)

---

## 1. 新增文件

### `HaCreator/MapSimulator/IPC/GymServer.cs`
**状态**：未提交（新增）

提供 MapSimulator 与 Python 训练脚本之间的 TCP IPC 服务器。

#### 核心类

| 类 | 说明 |
|---|---|
| `GymAction` | Python → C# 的动作指令（left/right/up/down/jump/reset/targetX/targetY） |
| `GymState`  | C# → Python 的环境状态（14 维观测 + isDone） |
| `GymServer` | TCP 服务器，端口 5555，异步接受连接，维护 `PendingAction` |

#### 关键设计
- 每帧 C# 主循环检查 `PendingAction`，处理后调用 `ClearAction()`，再通过 `SendState()` 回传状态。
- 使用 `JsonNamingPolicy.CamelCase` 序列化，确保 Python `snake_case ↔ C# PascalCase` 字段映射正确。
- 接受端口 **5555**（与 Python 侧 `mxd_env.py` 保持一致）。

---

### `HaCreator/MapSimulator/Core/DatasetGenerator.cs`
**状态**：未提交（新增）

YOLO 标注数据生成器，从 MapSimulator 实时帧中抓取屏幕截图并写出 `.txt` 标签文件。

> 口径说明（2026-05）：
> 本节描述的是早期 Gym/YOLO 实验链路。
> 当前离线 AutoCap 的正式操作口径，已经切换到单路径固定网格方案，请以 `docs/architecture/MAPSIMULATOR_AUTOCAP_RUNBOOK_2026-05.md` 为准。
> 下文中涉及旧三类标签、F4 手动采集、`YOLODataset/data.yaml` 的描述，均不代表当前 AutoCap 主链路。

#### 关键方法

| 方法 | 说明 |
|---|---|
| `ToggleGeneration()` | 开/关数据集采集（热键 **F4**） |
| `ShouldCaptureFrame()` | 每 30 帧（约 0.5 秒 @60FPS）采集一次 |
| `SaveFrameAndLabels(...)` | 从 BackBuffer 保存 PNG，同时写出归一化 YOLO `.txt` 标签 |

#### 输出目录结构
```
<exe目录>/YOLODataset/
├── data.yaml
├── images/
│   ├── train/  # 训练图像
│   └── val/    # 验证图像
└── labels/
    ├── train/  # 对应 train 图像标签
    └── val/    # 对应 val 图像标签
```
> 采集时会自动按约 80/20 划分 train/val，并自动更新 `data.yaml`。

#### 类别 ID
| ID | 含义 |
|---|---|
| 0 | 历史实验标签：怪物 Mob（旧链路） |
| 1 | 历史实验标签：怪物 HP 条（旧链路） |

---

## 2. 改动文件

### `HaCreator/MapSimulator/MapSimulator.cs`

#### 2.1 GymServer 初始化
```csharp
// Initialize 方法中
_gymServer = new IPC.GymServer();
_gymServer.Start(5555);
```
新增字段：`_gymServer`、`_gymMode`、`_gymTargetX`、`_gymTargetY`。

#### 2.2 Gym 主循环集成（`Update` 末尾）
每帧检查 `_gymServer.PendingAction`，执行以下流程：
1. 读取玩家状态（位置、速度、Grounded、平台边距）
2. 扫描地图 Ropes（楼梯/绳子）→ 计算最近梯子信息
3. 扫描 PortalPool Portals → 计算最近传送门信息（忽略 `StartPoint` 类型）
4. 判断是否到达目标（`50px` 半径）
5. 填充 `GymState` 并调用 `_gymServer.SendState(state)`

#### 2.3 数据集生成器集成（`Draw` 末尾）
```csharp
if (_datasetGenerator.IsGenerating && _datasetGenerator.ShouldCaptureFrame())
{
    // 收集所有活跃 Mob 的屏幕边框（class 0）
    // 收集所有活跃 HP 条的屏幕边框（class 1）
    _datasetGenerator.SaveFrameAndLabels(GraphicsDevice, boundsList);
}
```

#### 2.4 Dataset 模式下的 Mob 随机化
在 `Update` 中，若 `_datasetGenerator.IsGenerating`，对每个 Mob：
- **随机动画状态**：随机选择 `stand/move/attack/hit/die` 之一，通过 `ForceStateForDataset()` 强制播放
- **随机命中特效（已过时）**：该阶段曾尝试随机偏移位置添加 `AddHitEffect()`；当前 AutoCap 已完全移除此类程序化命中特效增强
- **随机 HP 条**：以 50% 概率调用 `OnMobDamaged()` + `RandomizeMobHPBarForDataset()`，产生不同颜色/尺寸的 HP 条

---

### `HaCreator/MapSimulator/Entities/MobItem.cs`

新增两个公共方法供 `DatasetGenerator` 调用：

#### `ForceStateForDataset(string action)`
在 `UpdateAnimationState()` 中优先级仅次于死亡状态，高于 AI 状态：
```csharp
if (_datasetForcedState != null && _animationSet.HasAnimation(_datasetForcedState))
{
    SetAction(_datasetForcedState);
    return;
}
```

#### `GetScreenBounds(int mapShiftX, int mapShiftY, int centerX, int centerY, float scale)`
计算当前帧在屏幕空间的像素包围盒（`Rectangle`），正确处理：
- Mob 的实际运动位置偏移（`MovementInfo.X/Y` vs 实例初始坐标）
- 翻转（`flip`）时的 X 镜像偏移
- 渲染缩放因子（`scale`）

---

### `HaCreator/MapSimulator/Effects/CombatEffects.cs`

#### 2.5 HP 条自定义外观（Dataset 数据增强）
`MobHPBarDisplay` 新增可选字段：
```csharp
public int? CustomWidth;
public int? CustomHeight;
public Color? CustomBorderColor;
public Color? CustomBgColor;
public Color? CustomHpColor;
```
`DrawSingleMobHPBar()` 优先使用 Custom 字段，回退到默认颜色/尺寸。

#### 2.6 `RandomizeMobHPBarForDataset(int poolId, Random rand)`（新增）
随机化指定 Mob HP 条的外观：
- 随机宽度：`40–100 px`
- 随机高度：`6–14 px`
- 随机边框颜色（深色系）
- 随机 HP 颜色（绿/蓝/紫色系）

#### 2.7 `GetActiveHPBarBounds(...)` （新增）
遍历所有 `_mobHPBars`，返回当前可见 HP 条在屏幕空间的 `List<Rectangle>`，供 `DatasetGenerator` 写入 YOLO 标签。

#### 2.8 `DrawHitEffects()` 兜底渲染
当 `effect.Frames == null || Frames.Count == 0` 时（纯程序化特效），改为绘制 `_glowTexture`（128×128）而非跳过，保证数据集帧中有视觉多样性。

---

### `HaCreator/GUI/Initialization.cs`

本次改动与 Gym/YOLO 无关，为 WZ 兼容性修复：

| 改动 | 说明 |
|---|---|
| `FindWzObjectDeep()` | 新增递归深度搜索 WZ 目录树，支持拆分 WZ 的嵌套结构 |
| `ExtractMapPortals()` | `MapHelper.img` 查找增加多级 fallback（`FindWzImageByName` → `FindWzObjectDeep`），找不到时改为警告而非抛异常 |
| `ExtractStringFile_ProcessPetItem()` | Pet WZ 解析改用动态类型判断，支持 `WzImage` / `WzDirectory` 两种结构 |
| `ExtractStringFile_ProcessMapNode()` | 新增递归节点处理，支持嵌套 Map 字符串节点 |
| `ExtractMaps()` | 增加调试弹窗（TODO：正式版删除） |

---

## 3. 系统架构概览

```
┌─────────────────────────┐        TCP :5555 (JSON Lines)       ┌──────────────────────┐
│    MapSimulator (C#)    │ ◄──────────────────────────────────► │  Python RL Env       │
│                         │                                       │  (mxd_env.py)        │
│  GymServer              │   GymAction (left/right/jump/...)    │                      │
│  ├─ AcceptClientAsync   │ ◄──────────────────────────────────  │  step(action)        │
│  └─ ReadLoopAsync       │                                       │  reset()             │
│                         │   GymState (14-dim obs + isDone)     │                      │
│  MapSimulator.Update()  │ ──────────────────────────────────►  │  _format_obs(state)  │
│  ├─ PendingAction check │                                       │                      │
│  ├─ Ladder scan         │                                       │  SB3 PPO Agent       │
│  └─ Portal scan         │                                       │  train_ppo_nav.py    │
└─────────────────────────┘                                       └──────────────────────┘

┌─────────────────────────┐
│    DatasetGenerator     │   F4 热键触发
│  ├─ ShouldCaptureFrame  │   每 30 帧一次
│  ├─ SaveFrameAndLabels  │   PNG + YOLO txt
│  └─ YOLODataset/        │
│      ├─ images/         │
│      └─ labels/         │
└─────────────────────────┘
```

---

## 4. Gym IPC 通信协议

### Python → C#：`GymAction`（JSON）
```json
{
  "left":   false,
  "right":  true,
  "up":     false,
  "down":   false,
  "jump":   false,
  "reset":  false,
  "targetX": 1200.0,
  "targetY": -300.0
}
```

### C# → Python：`GymState`（JSON，camelCase）
```json
{
  "x": 1023.5,
  "y": -284.0,
  "vX": 120.0,
  "vY": 0.0,
  "isGrounded": true,
  "distLeftEdge": 450.0,
  "distRightEdge": 230.0,
  "targetX": 1200.0,
  "targetY": -300.0,
  "isDone": false,
  "nearestLadderX": 1100.0,
  "nearestLadderTop": -400.0,
  "nearestLadderBottom": -200.0,
  "isOverlappingLadder": false,
  "nearestPortalX": 9999.0,
  "nearestPortalY": 9999.0,
  "isOverlappingPortal": false
}
```
> `9999.0` 表示该类型对象在地图中不存在或超出感知范围。

---

## 5. 观测空间说明（14 维）

| 索引 | 字段 | 说明 |
|------|------|------|
| 0 | `targetX - x` | 目标 X 偏移 |
| 1 | `targetY - y` | 目标 Y 偏移 |
| 2 | `vX` | 水平速度 |
| 3 | `vY` | 垂直速度 |
| 4 | `isGrounded` | 是否踩地（0/1） |
| 5 | `distLeftEdge` | 距当前平台左边缘距离 |
| 6 | `distRightEdge` | 距当前平台右边缘距离 |
| 7 | `nearestLadderX - x` | 最近梯子 X 偏移 |
| 8 | `nearestLadderTop - y` | 最近梯子顶部 Y 偏移 |
| 9 | `nearestLadderBottom - y` | 最近梯子底部 Y 偏移 |
| 10 | `isOverlappingLadder` | 是否在梯子重叠区（±50px，0/1） |
| 11 | `nearestPortalX - x` | 最近传送门 X 偏移 |
| 12 | `nearestPortalY - y` | 最近传送门 Y 偏移 |
| 13 | `isOverlappingPortal` | 是否在传送门重叠区（±30/40px，0/1） |

**动作空间**：`Discrete(9)`

| ID | 动作 |
|---|---|
| 0 | 无操作 |
| 1 | 向左 |
| 2 | 向右 |
| 3 | 向上（上梯） |
| 4 | 向下 |
| 5 | 跳跃 |
| 6 | 左跳 |
| 7 | 右跳 |
| 8 | 下跳（下平台） |

---

## 6. YOLO 数据集生成流程

> 说明：
> 本节保留的是早期 F4 手动采集流程。
> 当前 AutoCap 已改为 `job.json + run_autocap.ps1` 的离线任务模式，且标签契约固定为 `mob_dead / mob_active` 两类。
> 当前执行方式、相机路径、失败语义，请直接参考 `docs/architecture/MAPSIMULATOR_AUTOCAP_RUNBOOK_2026-05.md`。

### 触发方式
1. 在 MapSimulator 中打开目标地图
2. 按 **F4** 开始/停止采集
3. 数据自动写入 `<exe目录>/YOLODataset/`

### 数据增强策略（Dataset 模式特有）
当 `_datasetGenerator.IsGenerating == true` 时，MapSimulator 每帧自动：
- 随机强制 Mob 播放不同动画（stand/move/attack/hit/die）
- 历史上曾尝试在 Mob 附近生成程序化命中特效；当前 AutoCap 已移除该增强项
- 随机显示带有多样化颜色/尺寸的 HP 条

> 目的：在不需要真实战斗的情况下，产生涵盖多种视觉状态的训练样本。

### 标签格式
```
# 以下为历史 Gym/YOLO 实验标签示例，不是当前 AutoCap 契约
# <class_id> <cx_norm> <cy_norm> <w_norm> <h_norm>
0 0.512345 0.423456 0.089012 0.156789   # Mob
1 0.512345 0.321234 0.078900 0.023400   # HP 条
```

### 与 Python 训练对接
```yaml
# 以下为历史 Gym/YOLO 实验 data.yaml 示例，不是当前 AutoCap 产物定义
# YOLODataset/data.yaml（自动生成）
path: .
nc: 2
names: ['mob_dead', 'mob_active']
train: images/train
val: images/val
```

---

## 7. 热键速查

| 热键 | 功能 |
|---|---|
| **F3** | 开启 GymServer IPC（等待 Python 客户端连接） |
| **F4** | 切换 YOLO 数据集采集（开/关） |
| **F5** | 切换调试显示模式 |
| **F6** | 切换 Mob 移动开关 |

---

## 8. 已知事项与后续工作

### 待修复
- `Initialization.cs` 中的 `ExtractMaps()` 调试弹窗（`MessageBox.Show`）**需要在合并前删除**。
- `DatasetGenerator.cs` 和 `IPC/` 目录尚未提交，需在下一个 commit 一并纳入。

### 建议后续工作
- **课程学习（Curriculum Learning）**：在 HaCreator 中制作专用训练地图（平地 → 单梯 → 单传送门 → 组合），避免 PPO 在复杂地图中陷入局部最优。
- **时间加速（Fast Forward）**：在 `IsFixedTimeStep = false` 基础上，添加 `_gymMode` 下的帧率解锁逻辑，允许以 10x 速度推进仿真。
- **无头渲染（Headless）**：探索将 MonoGame 切换为 Offscreen RenderTarget，在无显示器的服务器上运行数据集生成。
- **YOLO 类别扩展**：添加 NPC（class 2）、Drop 掉落物（class 3）等类别，扩充数据集覆盖范围。
- **TensorBoard 监控**：训练脚本（`train_ppo_nav.py`）已集成 SB3 logger，观察 `ep_rew_mean` 趋势评估 Agent 学习效果。
---

## 更新说明（2026-05）
- AutoCap 当前正式操作口径请参考：`docs/architecture/MAPSIMULATOR_AUTOCAP_RUNBOOK_2026-05.md`
- 本文档中的 Gym/YOLO F4 手工采集描述仅作为历史架构背景，不应替代当前 AutoCap 运行说明
