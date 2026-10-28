# ff14-keybound-brawler — 「魔光键影」自动打字插件

FF14 金碟游乐场打字玩法 **「魔光键影」（Keybound Brawler）** 的 Dalamud 插件。

**v0.2**：定位改为**只做自动**——进本后自动读取敌人词并自动输入提交；本地练习界面已移除。
数据来源是实测确认的 `KTG*` 面板（2026-10-28 首轮实测采集）。

---

## 1. 实测数据（本插件采集）

| 面板 | 用途 | 实测 AtkValues |
|---|---|---|
| `KTGEWord` | 敌人词显示 | `100 \| 0 \| 1 \| 199 \| 100`（无文本，词在 **AtkTextNode**） |
| `KTGInput` | 输入框 | `6 \|  \| False \| False \| 0`（下标 1 = 当前输入文本） |
| `_KTGWave` | 波次 | — |
| `KTGHudScore` | 分数 | `1 \| 0 \| 0 \| 0 \| 10000 \| 10 \| 0 \| 100 \| 0` |
| `KTGHudGaugeHp` | 屏障 HP | `1 \| 40000 \| 40000 \| 1.99` |
| `KTGHudTodo` | 当前目标 | `1 \| 0 \| 0 \| 25` |
| `KTGHudLog` | 面板日志 | `3 \| 39 \| 221 \| 1.99` |
| `KTGEnemyCastBar` / `KTGIGaugeHp` / `KTGScreenTextFever` / `_KTGCountDown` / `KTGPlayGuide` / `KTGPlayerName` | 敌人咏唱 / 波次血条 / Fever / 倒计时 / 引导 / 玩家名 | — |
| `_KTGGameover` / `KTGResult` | 结算 | — |
| `_KTGLocationTitle` | 场地标题 | — |

- 地图：**键斗广场**；任务名「魔光键影」；进入地图时插件自动弹窗。
- 聊天里**没有**题目/得分（只有任务开始消息），所以题目必须从 UI 面板读。

## 2. 功能（v0.2）

| 模块 | 说明 |
|---|---|
| **自动打字** | 轮询 `KTGEWord` 文本节点拿当前词 → `SendInput` 自动输入 → 回车提交；同一个词只打一次 |
| **开始 / 停止按钮** | 「自动」Tab 顶部有大号「▶ 开始自动 / ■ 停止自动」按钮，也可 `/kbb auto on\|off` |
| **拟人化输入** | 随机反应延迟（默认 350~900ms）+ 按键抖动（默认 ±45%）+ 5% 概率"手滑停顿" + 提交前随机停顿（150~400ms），避免固定机械节奏 |
| 自动弹窗 | 进「键斗广场」或检测到 `KTG*` 面板时自动打开窗口 |
| 实时状态 | 窗口显示当前词、输入框内容、波次、分数、屏障 HP、目标 |
| 手动测试 | 窗口里「发送并回车」按钮、`/kbb type <文本>`，验证输入是否生效 |
| 数据采集 | 聊天 / dalamud.log / 面板名 / 面板文本 / 自动输入日志，全部写 `logs\` |
| 关键字事件 | 按规则匹配文本，判断"进入玩法/波次/得分/完成"等事件 |

### 自动输入方式

| 方式 | 说明 |
|---|---|
| `逐字粘贴`（默认） | 一个字符一次 Ctrl+V，字符间隔随机（默认 90ms ±40%），像真人一个字一个字打出来 |
| `整词粘贴` | 一次性粘贴整词，最快 |
| `Unicode` | `SendInput` + `KEYEVENTF_UNICODE`（实测游戏输入框可能不接收） |
| `Keystrokes` | 逐字符虚拟键，仅 ASCII（拼音/英文场景） |
| `只监控不输入` | 只读词、记录日志，不发送任何按键 |

- 输入内容可选：**原词（中文）** 或 **拼音（用内置词库映射）**。
- 只在游戏窗口位于前台时发送；自动开关、方式、间隔都在「自动」Tab 里。

### 拟人化（避免被抓机械节奏）

| 参数 | 默认 | 说明 |
|---|---|---|
| 反应延迟 | 400 ~ 1000ms | 看到新词后随机等待（区间每次随机） |
| 字符间隔 | 90ms，抖动 ±40% | 每个字间隔随机；另有 5% 概率"手滑停顿" 80~260ms |
| 提交停顿 | 150 ~ 400ms | 打完最后一个字到按回车之间随机停顿 |
| 触发节流 | 900 ~ 1500ms | 两次自动输入之间的随机间隔 |

- 全部在「自动」Tab 里可调；不想要抖动可以直接关掉「拟人化」开关（固定节奏，更快）。
- **「演都不演了」开关**：勾上后关掉所有拟人化延迟（反应 0ms、提交 15ms），节流从 900ms 压到 150ms，全速连打。
- 同词不会重复输入；反应延迟期间如果词已经变了（敌人死了/下一波），会跳过而不是打旧词。

### 选词轮换

`KTGEWord` 面板里是**所有存活敌人的词拼接**（如 `"地狱屋 血祭 季托夫 希瓦 弗雷沙"`），插件会：
- 按空格拆开、用词表校正，只保留词表里存在的词；
- 优先打「还没打过 → 打得少 → 词短」的，避免反复打同一个已死的词；
- 每个词最多打 3 次，还打不动就换下一个；
- 敌人死了词从列表消失后，计数自动清理。

## 3. 安装

### 方式 A：从 Dalamud 第三方仓库安装（推荐）

> 国内 `raw.githubusercontent.com` 经常不可达，所以清单用 **jsdelivr CDN** 分发。

1. 游戏内 `/xlplugins` → 设置 → **第三方插件仓库（Third-party Repositories）** → 添加：
   ```
   https://cdn.jsdelivr.net/gh/SiGeoMa/ff14-keybound-brawler@fbb39c25af8eb1976f92f9c4eeedb6202f23e3fb/pluginmaster.json
   ```
2. 在插件列表里搜 `KeyboundBrawler` → 安装 → 启用（重启游戏后生效）。
3. 游戏内 `/kbb` 打开窗口。

- 仓库：<https://github.com/SiGeoMa/ff14-keybound-brawler>
- 发布页：<https://github.com/SiGeoMa/ff14-keybound-brawler/releases>
- 清单原始地址（GitHub 可直连时用）：`https://raw.githubusercontent.com/SiGeoMa/ff14-keybound-brawler/main/pluginmaster.json`

### 方式 B：本地构建

```powershell
dotnet build ff14-keybound-brawler/dalamud-plugin/KeyboundBrawler/KeyboundBrawler.csproj -c Release

# 复制到桌面本地插件目录（本机 Dalamud 的开发插件路径）
Copy-Item ff14-keybound-brawler/dalamud-plugin/KeyboundBrawler/bin/Release/* -Destination "C:\Users\12841\Desktop\ff14本地插件\KeyboundBrawler\" -Recurse -Force
```

改完代码后**重启游戏**（或在 `/xlplugins` 里重载插件）生效。

## 4. 命令

| 命令 | 说明 |
|---|---|
| `/kbb` | 打开 / 关闭窗口 |
| `/kbb auto on\|off` | 自动打字开 / 关 |
| `/kbb type <文本>` | 手动测试：模拟输入并回车 |
| `/kbb ktg` | 打印当前 KTG 面板状态（词/输入/波次/分数/HP/目标） |
| `/kbb text <面板名>` | 打印面板所有文本节点（如 `/kbb text KTGEWord`） |
| `/kbb dump <面板名>` | 打印面板 AtkValues |
| `/kbb log` | 打开日志目录 |
| `/kbb addons` / `/kbb events` | 最近面板名 / 关键字事件 |
| `/kbb reload` / `/kbb words` | 重载词库 / 词库信息 |

## 5. 词库

内置 `data/words.json`：**1210 条**（词 + 拼音 + 词频），来源是「光之键盘侠词库合集」的
`词条总表.tsv`（词语 / 全拼 / 词频），拼音经过多音字上下文标注（如 克劳德→ke lao de）。

- 原始词库合集归档在 `data/source/输入法词库/`（含 12 种输入法格式，供玩家手动导入输入法用）。
- 转换脚本：`tools/tsv_to_json.py`（总表 → words.json）；早期版本还有 `tools/scel_to_json.py`。
- 插件用词库做三件事：
  1. **读词校正**：游戏 UI 读到的文本可能带噪声，自动对到词表标准词（精确 → 包含 → 编辑距离 ≤2）。
  2. **拼音映射**：`输入内容 = 拼音` 时把当前词转成拼音发送。
  3. **词长/词频信息**，便于后续做难度统计。
- 用户目录版本优先：`pluginConfigs\KeyboundBrawler\data\words.json`，可用 `/kbb reload` 重载。

## 6. 日志（`pluginConfigs\KeyboundBrawler\logs\`）

| 文件 | 内容 |
|---|---|
| `ktg-*` / `auto-*.log` | 词变化、自动输入结果（OK/FAIL、实际发送的文本） |
| `addons-*.log` | 新出现的面板名 |
| `addon-text-*.log` | 可疑面板文本快照 |
| `chat-*.log` / `dalamud-*.log` | 聊天 / Dalamud 日志 |
| `events-*.log` | 关键字事件 |

## 7. 实测调试点

如果自动没生效，按顺序检查：

1. `/kbb ktg` → 看 `词「...」` 是否真的有词；空说明文本节点读取位置不对，用 `/kbb text KTGEWord` 看节点内容。
2. `logs\auto-*.log` → 看自动输入是否发送（`[自动] OK/FAIL`），FAIL 会带原因（如"游戏不在前台"）。
3. 游戏里手动按 Enter 试试：确认输入框本身能接受 `SendInput`（若不行，改用窗口里的「发送并回车」再验证）。
4. 如果 Unicode 输入不生效：在「自动」Tab 把输入方式换成 `虚拟键（ASCII）`，或输入内容换成 `拼音`。
5. 每次改动都会记录到 `auto-*.log`，把日志发我即可继续调。

## 8. 目录结构

```
ff14-keybound-brawler/
├─ dalamud-plugin/KeyboundBrawler/
│  ├─ Plugin.cs                 入口、自动逻辑、命令
│  ├─ PluginConfiguration.cs    配置
│  ├─ Core/
│  │  ├─ KtgMonitor.cs          KTG* 面板读取（文本节点 + AtkValues）
│  │  ├─ AutoTyper.cs           SendInput 自动输入（Unicode / 虚拟键 / 粘贴）
│  │  ├─ AddonProbe.cs          面板探测 / 快照
│  │  ├─ ChatLogRecorder.cs     聊天采集
│  │  ├─ DalamudLogTailer.cs    dalamud.log 采集
│  │  ├─ KeywordEngine.cs       关键字事件引擎
│  │  ├─ KeywordRuleParser.cs   规则解析
│  │  └─ WordBook.cs / TypingSession.cs   词库（拼音映射用，UI 已不提供练习）
│  ├─ UI/MainWindow.cs          自动 / 日志 / 探测 / 设置
│  └─ data/words.json           1210 条词库（总表转换版，含带空格拼音）
├─ data/source/
│  ├─ FF14 魔光键影 词库.scel     原始搜狗词库
│  └─ 输入法词库/               光之键盘侠词库合集（12 种输入法格式 + 词条总表）
├─ tools/（tsv/scel 转换、安装、注册、selfcheck）
└─ docs/（调研、实测、架构）
```

## 9. 合规

- 只读取游戏 UI 文本（AtkAddon），不修改游戏数据。
- 自动输入只在**你自己的游戏窗口位于前台**时发送，等价于你自己敲键盘。
- 不注入游戏内存、不修改游戏文件。

## 10. 版本记录

| 版本 | 说明 |
|---|---|
| 0.2.0 | 实测 KTG 面板接入：自动读词 + 自动输入 + 自动弹窗；移除本地练习界面 |
| 0.1.0 | 占位：词库、本地练习、三路数据采集 |
