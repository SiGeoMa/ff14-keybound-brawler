# ff14-keybound-brawler — 「魔光键影」自动打字插件

FF14 金碟游乐场打字玩法 **「魔光键影」（Keybound Brawler）** 的 Dalamud 插件。

**v0.4.0**：精简重构版——只保留核心：自动读词 → 按距离选最近的敌人 → 逐字写入输入框 → 调游戏回调提交；带防 AFK 和防误退本保护。

---

## 功能

| 模块 | 说明 |
|---|---|
| 自动读词 | 读 `KTGEWord` 的 20 个词条槽位（精确布局），每个词条带 `EntityID` |
| 按距离集火 | 用 `EntityID` + ObjectTable 算敌人与你的距离，**优先打最近的**；打 3 次不死换下一个 |
| 逐字写入 | 一个字一个字写进 `KTGInput`（每字 90~220ms 随机），不做"瞬间消词" |
| 游戏回调提交 | 写完调 `AtkComponentTextInput.Callback(Enter)`，返回 `ClearText` 即成功 |
| 防 AFK | 每 20 秒（可调）发 F13/F14/Shift + 左右方向键，避免无动作被踢 |
| 防误退本 | 只在 Territory 1359 输入；结算面板出现/输入框消失/确认对话框出现时一律停手 |
| 自动弹窗 | 进「键斗广场/金碟游乐场」自动打开窗口 |
| 词表校正 | UI 读到的文本对到 1210 条词表标准词 |

## 安装

### 方式 A：Dalamud 第三方仓库（推荐）

1. `/xlplugins` → 设置 → 第三方插件仓库 → 添加：
   ```
   https://cdn.jsdelivr.net/gh/SiGeoMa/ff14-keybound-brawler@main/pluginmaster.json
   ```
2. 搜索 `KeyboundBrawler` → 安装 → 重启游戏

### 方式 B：本地构建

```powershell
dotnet build ff14-keybound-brawler/dalamud-plugin/KeyboundBrawler/KeyboundBrawler.csproj -c Release
```

把 `bin/Release/` 里的 dll、json、icon.png、data/ 拷到 Dalamud 开发插件目录。

## 命令

| 命令 | 说明 |
|---|---|
| `/kbb` | 打开 / 关闭窗口 |
| `/kbb auto on\|off` | 自动打字开 / 关 |
| `/kbb ktg` | 打印当前面板状态（区域/词/目标/分数） |
| `/kbb reload` | 重载词库 |

## 使用

1. 进键斗广场，窗口自动弹出（或 `/kbb`）
2. 点「▶ 开始自动」
3. 「自动」Tab 可调：逐字间隔、反应延迟范围、拟人化/演都不演、防 AFK 间隔、选词顺序

## 技术要点（逆向自 DailyRoutines 的扩展库）

- `AddonKTGEWord.WordEntry`：20 槽位（偏移 568，每项 200 字节），含 `NameTextNode`(112)、`EntityID`(168)、`State`(176)、`Obstructed`(180)
- `KTGInput` 节点 7 = `AtkComponentTextInput`，`SetText` 写入、`Callback(InputCallbackType.Enter)` 提交
- 区域：战斗 `1359`、报名大厅 `1360`
- 词库：1210 条（光之键盘侠词库合集，`data/source/输入法词库/` 归档）

## 目录结构

```
ff14-keybound-brawler/
├─ dalamud-plugin/KeyboundBrawler/
│  ├─ Plugin.cs              入口 / 自动逻辑 / 防 AFK / 安全防护
│  ├─ PluginConfiguration.cs 配置
│  ├─ Core/
│  │  ├─ KtgMonitor.cs       KTG 面板读取（词条/输入框/回调提交）
│  │  ├─ AutoTyper.cs        防 AFK 心跳 + 拟人化延迟
│  │  └─ WordBook.cs         词库加载 + 文本校正
│  ├─ UI/MainWindow.cs       自动 / 设置
│  ├─ data/words.json        1210 条词库
│  └─ icon.png               插件图标
├─ data/source/              原始词库归档
├─ tools/                    scel/tsv 转换、图标生成、发布、自检
└─ docs/                     调研与实测记录
```

## 合规

- 只读取游戏 UI 文本（AtkAddon），不修改游戏数据
- 自动输入只在你自己的游戏窗口前台时生效
- 不注入游戏内存、不修改游戏文件

## 版本记录

| 版本 | 说明 |
|---|---|
| 0.4.0 | 精简重构：移除探测/日志采集/关键字/练习等冗余模块，只保留核心自动流程 |
| 0.3.6 | 逐字写入 + 方向键防 AFK |
| 0.3.5 | 实体距离选词 + 插件图标 |
| 0.3.0 | 直接写入输入框 + 游戏回调提交 |
