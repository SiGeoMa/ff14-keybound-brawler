# 数据源调研与实测计划（魔光键影 / Keybound Brawler）

> 调研时间：2026-10-28（玩法上线当天）。资料来自网络，外部内容仅作参考，以实机为准。

## 1. 玩法要点（影响插件设计的部分）

- 金碟「键斗广场」最多 4 人打字副本，怪物分 3 波推进；正确输入敌人头顶显示的词才能造成伤害，HP 打空才死。
- **整串提交判定**：输入完按 Enter，按整个字符串判定，不是逐字符实时判定；大小写不限。
- 标点规则：`.` `,` 空格必须输入；`?` `!` `-` `'` 可以省略（如 `Hi-potion` 与 `Hipotion` 都算对）。
- 只能用键盘，输入复用聊天输入系统，支持 IME；游玩期间强制 Direct chat 模式；↑ 可调出输入历史。
- 语言差异：日服显示汉字 + 假名 ruby（玩家输入假名）；英文服显示英文单词；**国服玩家输入汉字**（中文输入法）。
- 计分：连击倍率、波次结束按屏障剩余 HP 给分、击杀 magic pot 得「Impassioned Inputs」。
- 特殊机制：可打断法术、Letterlock（进度条满必须停手）、Cryptic Communications（文字部分隐藏）。

来源：[官方专题页](https://na.finalfantasyxiv.com/lodestone/special/keybound_brawler/)、
官方说明字符串（[XIV Rus Weblate](https://translate.xivrus.ru/zen/xivrus/descriptionstring/en/?offset=1557)）、
[灰机 wiki](https://ff14.huijiwiki.com/wiki/%E9%AD%94%E5%85%89%E9%94%AE%E5%BD%B1)。

## 2. 三条数据通道评估

| 通道 | 能否拿到题目 | 结论 |
|---|---|---|
| 游戏聊天（IChatGui.ChatMessage） | 大概率不能 | 官方 UI 说明里没有聊天消息；Enter 是提交攻击，不广播。**只能兜底**，用于验证是否有系统消息（得分/结算） |
| 游戏聊天日志文件 | 大概率不能 | 只记录聊天频道；若系统消息不存在，日志里同样没有 |
| 游戏 UI 面板（AtkAddon） | **最可能** | 官方明确有独立 Text Window / 词条显示 / 波次要求 / HP 条 / 倍率槽，属典型 AtkAddon；**面板名未公开，需实机确认** |
| dalamud.log | 间接 | 只能看到插件日志，看不到游戏玩法内容；用于排查插件自身 |

其他可能：
- `IGameGui.WorldToScreen` 把敌人头顶文字锚点换算屏幕坐标，可做 overlay 对齐 / 高亮。
- 输入框内容：输入组件复用聊天输入系统，可尝试读 ChatLog 相关 addon 的文本节点（推断，待实测）。
- 客户端 EXD：说明文本来自 `DescriptionString` sheet，词表可能也在客户端数据里（具体 sheet 名未查到）。

## 3. 实机实测结果（2026-10-28 首轮，已采集）

已确认的关键事实：

- **面板名全部是 `KTG*` 前缀**（KTG = Keybound Typing Game 之类）：
  `KTGEWord`（敌人词）、`KTGInput`（输入框）、`_KTGWave`（波次）、`KTGHudScore`（分数）、
  `KTGHudGaugeHp`（屏障 HP）、`KTGHudTodo`（目标）、`KTGHudLog`、`KTGEnemyCastBar`、`KTGIGaugeHp`、
  `KTGScreenTextFever`、`_KTGCountDown`、`KTGPlayGuide`、`KTGPlayerName`、`_KTGLocationTitle`、
  `_KTGGameover`（失败）、`KTGResult`（结算）等。
- **词文本不在 AtkValues 里**：`KTGEWord` 的 AtkValues 是 `100 | 0 | 1 | 199 | 100` 这种数字，
  词在 **AtkTextNode**（`UldManager.NodeList` 遍历取文本），插件已实现读取。
- `KTGInput` 的 AtkValues 形如 `6 |  | False | False | 0`，**下标 1 是当前输入文本**。
- 地图名 **键斗广场**；任务名「魔光键影」；聊天里只有任务开始消息，**没有题目/得分**（证实了之前的判断）。
- 进入方式：金碟 → 键斗广场，或排本（任务“魔光键影”出发准备完毕）。

自动输入结论：游戏输入框复用聊天输入系统、支持 IME，所以插件用
`SendInput + KEYEVENTF_UNICODE` 直接送中文字符（绕过输入法），再用 Enter 提交。

## 4. 下一阶段开发建议

1. **题目解析器**：`AddonProbe` 目前只记录面板名 + 手动读值。确认面板名后，改成订阅该面板的 `PostShow` / `PreUpdate`，
   读 AtkValues，抽出题目词并显示在插件窗口里（提示当前该打什么词）。
2. **词表对齐**：内置词库是中文词 + 拼音；如果游戏实际显示的词不在词库，按「屏幕文本 → 词库 → 人工校对」补充。
3. **成绩记录**：如果聊天/日志里确实没有得分，就从面板 AtkValues 抓（波次、连击、得分项），或手动记录。
4. **输入边界**：自动输入只在玩家自己的游戏窗口位于前台时发送（等价于自己敲键盘），不做后台挂机/多开。
5. **数据层抽象**：建议把「UI 文本源 / 聊天源 / 客户端数据源」做成 provider 接口，后续替换方便。

## 5. 词库资料

- 中文词库（输入法格式，NGA，需登录）：[词库](https://ngabbs.com/read.php?tid=47760780)、
  [导入版](https://bbs.nga.cn/read.php?tid=47762889)、[搜狗版](https://ngabbs.com/read.php?tid=47771404)
- 「光之键盘侠词库合集」（用户提供，已归档 `data/source/输入法词库/`）：
  同一份 1210 条词表的 12 种输入法成品（搜狗 scel / QQ / 百度 / 微软 / 谷歌 / 手心 / 讯飞 / Rime / Gboard / Mac），
  以及 `词条总表.tsv`（词语 / 全拼 / 词频，UTF-8 BOM，拼音经多音字上下文标注）。
- 插件词库已换成总表转换版（`tools/tsv_to_json.py`），拼音比原始 scel 更规范；
  并利用词表做「UI 读到的文本 → 标准词」的校正（精确 / 包含 / 编辑距离 ≤2）。
- 目前没有公开的 Dalamud 插件 / ACT 触发器 / overlay 实现（GitHub 搜索无相关仓库）。
