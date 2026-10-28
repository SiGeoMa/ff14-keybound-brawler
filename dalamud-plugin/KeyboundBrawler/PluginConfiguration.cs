using Dalamud.Configuration;

namespace KeyboundBrawler;

/// <summary>插件配置，保存在 %APPDATA%\XIVLauncherCN\pluginConfigs\KeyboundBrawler\Configuration.json。</summary>
public sealed class PluginConfiguration : IPluginConfiguration
{
    public int Version { get; set; } = 8;

    /// <summary>总开关。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>主窗口 UI 缩放。</summary>
    public float UiScale { get; set; } = 1.0f;

    /// <summary>输入模式：Pinyin（打拼音，不依赖输入法）/ Chinese（打汉字，需要中文输入法）。</summary>
    public string InputMode { get; set; } = "Pinyin";

    /// <summary>是否在目标词下方显示拼音提示。</summary>
    public bool ShowPinyinHint { get; set; } = true;

    /// <summary>参与练习的最小词长（汉字个数）。</summary>
    public int MinWordLength { get; set; } = 2;

    /// <summary>参与练习的最大词长（汉字个数）。</summary>
    public int MaxWordLength { get; set; } = 8;

    /// <summary>提交后自动进入下一题。</summary>
    public bool AutoNext { get; set; } = true;

    /// <summary>是否把游戏聊天消息记录到日志（后续解析玩法数据用）。</summary>
    public bool RecordChatLog { get; set; } = true;

    /// <summary>是否跟随读取 Dalamud 的 dalamud.log（外部日志文件）。</summary>
    public bool TailDalamudLog { get; set; } = true;

    /// <summary>是否记录游戏 UI 面板（AtkAddon）出现事件，用于后续解析玩法面板。</summary>
    public bool EnableAddonProbe { get; set; } = true;

    /// <summary>进入匹配地图时自动弹出插件窗口。</summary>
    public bool AutoOpenOnTerritory { get; set; } = true;

    /// <summary>自动弹窗的地图名关键词（逗号分隔）。</summary>
    public string TerritoryKeywords { get; set; } = "金碟,键斗,魔光,键影,游乐";

    /// <summary>检测到匹配的 UI 面板名时也自动弹窗（逗号分隔关键词）。</summary>
    public bool AutoOpenOnAddon { get; set; } = true;

    /// <summary>自动弹窗的面板名关键词。</summary>
    public string AddonKeywords { get; set; } = "Keybound,Brawler,Keyward,魔光,键影,KTG";

    /// <summary>自动打字总开关（进 KTG 面板后自动输入当前词并回车）。</summary>
    public bool AutoPlay { get; set; } = true;

    /// <summary>自动输入方式：direct（直接写输入框+游戏回调，默认）/ paste_char（逐字粘贴）/ paste（整词粘贴）/ disabled。</summary>
    public string AutoInputMode { get; set; } = "direct";

    /// <summary>固定频率：触发间隔不再随机波动，用 AutoFixedIntervalMs 固定值。</summary>
    public bool AutoFixedInterval { get; set; }

    /// <summary>固定频率的触发间隔（毫秒）。</summary>
    public int AutoFixedIntervalMs { get; set; } = 1500;

    /// <summary>失焦时自动把游戏窗口切回前台（防止挂机/副本失败被踢）。</summary>
    public bool AutoRefocusGame { get; set; } = true;

    /// <summary>防 AFK：定期发送无绑定按键，避免自动打字（无键盘事件）被判定挂机。</summary>
    public bool AutoAntiAfk { get; set; } = true;

    /// <summary>防 AFK 心跳间隔（秒）。</summary>
    public int AntiAfkIntervalSeconds { get; set; } = 20;

    /// <summary>输入内容：word（原词，中文）/ pinyin（用词库把词转拼音）。</summary>
    public string AutoInputContent { get; set; } = "word";

    /// <summary>选词顺序：distance（实体距离近→远，默认）/ screen_desc / screen_asc / order / shortest / longest。</summary>
    public string AutoTargetOrder { get; set; } = "distance";

    /// <summary>每个字符输入间隔基准（毫秒）。</summary>
    public int AutoCharDelayMs { get; set; } = 120;

    /// <summary>拟人化：字符间隔随机抖动 + 随机停顿，避免机械固定节奏。</summary>
    public bool AutoHumanize { get; set; } = true;

    /// <summary>演都不演：关掉所有拟人化延迟，全速连打（节流也缩短）。</summary>
    public bool AutoBlatantMode { get; set; }

    /// <summary>字符间隔抖动百分比（0-80）。</summary>
    public int AutoJitterPercent { get; set; } = 40;

    /// <summary>看到新词后的反应延迟范围（毫秒）。</summary>
    public int AutoStartDelayMinMs { get; set; } = 600;

    public int AutoStartDelayMaxMs { get; set; } = 1500;

    /// <summary>打完字到按回车之间的停顿范围（毫秒）。</summary>
    public int AutoSubmitDelayMinMs { get; set; } = 200;

    public int AutoSubmitDelayMaxMs { get; set; } = 500;

    /// <summary>自动抓取可疑面板（匹配 AddonKeywords 或首次出现）的 AtkValues 文本，写 logs\addon-text-*.log。</summary>
    public bool EnableAutoSnapshot { get; set; } = true;

    /// <summary>关键字事件引擎开关。</summary>
    public bool EnableKeywordEngine { get; set; } = true;

    /// <summary>关键字规则，每行「关键词|事件名」；留空使用内置默认规则。</summary>
    public string KeywordRules { get; set; } = string.Empty;

    /// <summary>关键字命中时在聊天框提示。</summary>
    public bool NotifyKeywordMatchesInChat { get; set; }

    /// <summary>运行时提示（自动弹窗、事件命中、失焦等）是否打印到聊天框；默认关，只写日志。</summary>
    public bool PrintRuntimeMessages { get; set; }

    /// <summary>dalamud.log 路径；留空时自动探测国服 / 国际服路径。</summary>
    public string DalamudLogPath { get; set; } = string.Empty;

    /// <summary>日志关键词过滤（逗号分隔）；留空表示不过滤、全部记录。</summary>
    public string LogKeywords { get; set; } = string.Empty;

    /// <summary>日志 Tab 里在内存中保留的最近行数。</summary>
    public int RecentLogLines { get; set; } = 300;

    /// <summary>登录后自动打开主窗口。</summary>
    public bool ShowWindowOnLogin { get; set; }

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
