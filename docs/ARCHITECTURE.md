# 架构与后续开发路线

## 1. 分层

```
Plugin.cs            插件入口：服务注入、命令、窗口注册、Framework.Update 分发
PluginConfiguration  配置（pluginConfigs\KeyboundBrawler\Configuration.json）

Core/
├─ WordBook.cs           词库模型 + 读取 + 按词长抽题（纯逻辑，无 Dalamud 依赖）
├─ TypingSession.cs      判定 / 计时 / 统计（纯逻辑，无 Dalamud 依赖，可自检）
├─ ChatLogRecorder.cs    IChatGui.ChatMessage → logs\chat-*.log（含 LineCaptured 钩子）
├─ DalamudLogTailer.cs   增量 tail dalamud.log → logs\dalamud-*.log
└─ AddonProbe.cs         AtkAddon PostShow 记录 + 按名字读 AtkValues（题目解析的起点）

UI/MainWindow.cs     练习 / 日志 / 探测 / 设置
```

设计约束：
- `Core` 里除 `ChatLogRecorder` / `DalamudLogTailer` / `AddonProbe` 外的纯逻辑不引用 Dalamud，
  这样 `tools/selfcheck` 能直接链接源码跑测试。
- 所有采集只读，写文件只写插件自己的配置/日志目录。

## 2. 运行时数据流

```
游戏聊天 ──IChatGui.ChatMessage──▶ ChatLogRecorder ──▶ logs\chat-yyyyMMdd.log
                                                └──▶ LineCaptured 事件（后续解析器）
dalamud.log ──文件增量读取──▶ DalamudLogTailer ──▶ logs\dalamud-yyyyMMdd.log
游戏 UI 面板 ──IAddonLifecycle.PostShow──▶ AddonProbe ──▶ logs\addons-yyyyMMdd.log
                                                     └──▶ UI 探测 Tab / AtkValues 快照
```

`Framework.Update` 每帧调用：`ChatLog.Flush()`（批量落盘）、`LogTailer.Poll()`（内部节流 ~1s）。

## 3. 词库数据格式

`data/words.json`：

```json
{
  "source": "FF14 魔光键影 词库.scel",
  "count": 1210,
  "words": [
    { "word": "阿勃祖", "pinyin": "abozu", "freq": 1092 }
  ]
}
```

- `freq` 是原词库里的排序号（越大越靠前），可当难度/权重用。
- 用户目录版本优先于插件内置版本；`/kbb reload` 热重载。
- 由 `tools/scel_to_json.py` 从搜狗词库生成（scel 格式解析：拼音表 + 词条表，见脚本注释）。

## 4. 下一阶段：接入游戏内题目

前置：实机确认「魔光键影」的 AtkAddon 面板名（方法见 `docs/LOGS.md` §3）。

计划：
1. 新建 `Core/KeyboundParser.cs`：
   - 订阅目标面板的 `PostShow` / `PreUpdate`（`IAddonLifecycle`）。
   - 读 `AtkUnitBasePtr.AtkValues`（或 Atk 文本节点），抽出题目词。
   - 输出统一事件 `KeyboundStateChanged(CurrentWord, Wave, Score, Combo, ...)`。
2. `TypingSession` 增加「跟打模式」：目标词来自解析器而不是词库，判定用游戏同样的规则（整串提交、忽略大小写、标点容错）。
3. UI 练习 Tab 增加「实时跟打」来源切换：本地词库 / 游戏面板。
4. 成绩记录：把每局结果写 `logs\scores.csv`（或用户数据目录），可回看。

## 5. 注意

- 面板/节点名未公开，不要在设计里写死；用配置项 + 探测工具确认。
- 读 AtkValues 只能在面板有效期内（PostShow/PreUpdate 回调内）读，避免持有过期的 `AtkUnitBasePtr`。
- 不代打：只提示「该打哪个词」，输入永远由玩家完成。
- 版本升级 Dalamud API Level 时：`Dalamud.NET.Sdk` 版本号、`DalamudApiLevel`、`KeyboundBrawler.json` 三处一起改。

## 6. 验证方式

```powershell
# 核心逻辑
dotnet run --project tools/selfcheck -- dalamud-plugin/KeyboundBrawler/data/words.json
# 插件编译
dotnet build ff14-keybound-brawler/dalamud-plugin/KeyboundBrawler/KeyboundBrawler.csproj -c Release
```
