using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using KeyboundBrawler.Core;

namespace KeyboundBrawler.UI;

/// <summary>主窗口：自动 / 日志 / 探测 / 设置。</summary>
public sealed class MainWindow : Window
{
    private static readonly Vector4 Gold = new(1.0f, 0.84f, 0.36f, 1.0f);
    private static readonly Vector4 Green = new(0.42f, 0.92f, 0.55f, 1.0f);
    private static readonly Vector4 Red = new(1.0f, 0.42f, 0.42f, 1.0f);

    private static readonly string[] InputModes = { "逐字粘贴（一个字一个字打，推荐）", "整词粘贴（快）", "Unicode（直接送字符）", "虚拟键（ASCII）", "只监控不输入" };
    private static readonly string[] ContentModes = { "原词（中文）", "拼音（词库映射）" };
    private static readonly string[] TargetOrders = { "屏幕位置（近→远，推荐）", "屏幕位置（远→近）", "面板顺序", "面板反向", "短词优先", "长词优先" };

    private readonly Plugin plugin;

    private string keywordInput = string.Empty;
    private string logPathInput = string.Empty;
    private string probeAddonInput = string.Empty;
    private string keywordRulesInput = string.Empty;
    private string territoryKeywordsInput = string.Empty;
    private string addonKeywordsInput = string.Empty;
    private string manualInput = string.Empty;
    private List<(int Index, string Value)> probeValues = new();
    private string? probeError;
    private bool initialized;

    public MainWindow(Plugin plugin)
        : base("魔光键影 · 自动##kbb-main")
    {
        this.plugin = plugin;
        Size = new Vector2(640, 660);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(500, 420),
            MaximumSize = new Vector2(1600, 2000),
        };
    }

    public override void Draw()
    {
        var config = plugin.Configuration;

        if (!initialized)
        {
            keywordInput = config.LogKeywords;
            logPathInput = config.DalamudLogPath;
            keywordRulesInput = config.KeywordRules;
            territoryKeywordsInput = config.TerritoryKeywords;
            addonKeywordsInput = config.AddonKeywords;
            initialized = true;
        }

        ImGui.SetWindowFontScale(config.UiScale);

        ImGui.TextColored(Gold, "魔光键影 · 自动");
        ImGui.SameLine();
        ImGui.TextDisabled("KeyboundBrawler v0.2 · API 15");
        ImGui.Separator();

        if (ImGui.BeginTabBar("kbb-tabs"))
        {
            if (ImGui.BeginTabItem("自动"))
            {
                DrawAutoTab();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("日志"))
            {
                DrawLogTab();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("探测"))
            {
                DrawProbeTab();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("设置"))
            {
                DrawSettingsTab();
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }
    }

    // ─────────────────────────────────────────────────────────────
    // 自动
    // ─────────────────────────────────────────────────────────────

    private void DrawAutoTab()
    {
        var config = plugin.Configuration;
        var state = plugin.Ktg.State;

        ImGui.TextColored(Gold, "实时状态（KTG 面板）");
        if (!state.InGame && !state.GameOver)
        {
            ImGui.TextDisabled("（还没检测到「魔光键影」面板，进本后会自动显示）");
        }
        else if (state.GameOver)
        {
            ImGui.TextColored(Green, "本局结束（结算面板）");
        }

        ImGui.Text($"当前词：{state.Word}");
        ImGui.TextDisabled($"输入框：{state.Input}");
        ImGui.Text($"波次：{state.Wave}    分数：{state.Score}");
        ImGui.Text($"屏障 HP：{state.GaugeHp}    目标：{state.Todo}");

        ImGui.Separator();

        // ── 自动开关（大按钮） ────────────────────────────────
        if (config.AutoPlay)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.62f, 0.16f, 0.16f, 1.0f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.80f, 0.24f, 0.24f, 1.0f));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.90f, 0.30f, 0.30f, 1.0f));
            if (ImGui.Button("■ 停止自动", new Vector2(190, 44)))
            {
                config.AutoPlay = false;
                plugin.SaveConfig();
            }

            ImGui.PopStyleColor(3);
            ImGui.SameLine();
            ImGui.TextColored(Green, "● 自动运行中");
        }
        else
        {
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.13f, 0.45f, 0.22f, 1.0f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.18f, 0.60f, 0.30f, 1.0f));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.24f, 0.72f, 0.38f, 1.0f));
            if (ImGui.Button("▶ 开始自动", new Vector2(190, 44)))
            {
                config.AutoPlay = true;
                plugin.SaveConfig();
            }

            ImGui.PopStyleColor(3);
            ImGui.SameLine();
            ImGui.TextDisabled("○ 已停止");
        }

        ImGui.Separator();

        var modeIndex = config.AutoInputMode switch
        {
            "paste" => 1,
            "unicode" => 2,
            "keystrokes" => 3,
            "disabled" => 4,
            _ => 0,
        };
        if (ImGui.Combo("输入方式", ref modeIndex, InputModes, InputModes.Length))
        {
            config.AutoInputMode = modeIndex switch
            {
                1 => "paste",
                2 => "unicode",
                3 => "keystrokes",
                4 => "disabled",
                _ => "paste_char",
            };
            plugin.SaveConfig();
        }

        var contentIndex = config.AutoInputContent == "pinyin" ? 1 : 0;
        if (ImGui.Combo("输入内容", ref contentIndex, ContentModes, ContentModes.Length))
        {
            config.AutoInputContent = contentIndex == 1 ? "pinyin" : "word";
            plugin.SaveConfig();
        }

        var orderIndex = config.AutoTargetOrder switch
        {
            "screen_asc" => 1,
            "order" => 2,
            "order_reverse" => 3,
            "shortest" => 4,
            "longest" => 5,
            _ => 0,
        };
        if (ImGui.Combo("选词顺序", ref orderIndex, TargetOrders, TargetOrders.Length))
        {
            config.AutoTargetOrder = orderIndex switch
            {
                1 => "screen_asc",
                2 => "order",
                3 => "order_reverse",
                4 => "shortest",
                5 => "longest",
                _ => "screen_desc",
            };
            plugin.SaveConfig();
        }

        var refocus = config.AutoRefocusGame;
        if (ImGui.Checkbox("游戏失焦时自动切回前台（防止挂机 / 副本失败）", ref refocus))
        {
            config.AutoRefocusGame = refocus;
            plugin.SaveConfig();
        }

        // ── 拟人化 ───────────────────────────────────────────
        ImGui.Separator();
        ImGui.TextColored(Gold, "拟人化输入（避免固定机械节奏）");

        var blatant = config.AutoBlatantMode;
        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1.0f, 0.6f, 0.3f, 1.0f));
        if (ImGui.Checkbox("演都不演了（关闭所有延迟，全速连打）", ref blatant))
        {
            config.AutoBlatantMode = blatant;
            plugin.ApplyAutoTyperSettings();
            plugin.SaveConfig();
        }

        ImGui.PopStyleColor();

        var humanize = config.AutoHumanize;
        if (ImGui.Checkbox("启用拟人化（随机反应延迟 + 按键抖动 + 提交前停顿）", ref humanize))
        {
            config.AutoHumanize = humanize;
            plugin.ApplyAutoTyperSettings();
            plugin.SaveConfig();
        }

        var charDelay = config.AutoCharDelayMs;
        if (ImGui.SliderInt("字符间隔基准 (ms)", ref charDelay, 20, 200))
        {
            config.AutoCharDelayMs = charDelay;
            plugin.ApplyAutoTyperSettings();
        }

        var jitter = config.AutoJitterPercent;
        if (ImGui.SliderInt("按键抖动 ±%", ref jitter, 0, 80))
        {
            config.AutoJitterPercent = jitter;
            plugin.ApplyAutoTyperSettings();
        }

        var startMin = config.AutoStartDelayMinMs;
        if (ImGui.SliderInt("看到词后反应下限 (ms)", ref startMin, 0, 2000))
        {
            config.AutoStartDelayMinMs = startMin;
            plugin.ApplyAutoTyperSettings();
        }

        var startMax = config.AutoStartDelayMaxMs;
        if (ImGui.SliderInt("看到词后反应上限 (ms)", ref startMax, 0, 3000))
        {
            config.AutoStartDelayMaxMs = startMax;
            plugin.ApplyAutoTyperSettings();
        }

        var submitMin = config.AutoSubmitDelayMinMs;
        if (ImGui.SliderInt("提交前停顿下限 (ms)", ref submitMin, 0, 1500))
        {
            config.AutoSubmitDelayMinMs = submitMin;
            plugin.ApplyAutoTyperSettings();
        }

        var submitMax = config.AutoSubmitDelayMaxMs;
        if (ImGui.SliderInt("提交前停顿上限 (ms)", ref submitMax, 0, 2000))
        {
            config.AutoSubmitDelayMaxMs = submitMax;
            plugin.ApplyAutoTyperSettings();
        }

        if (ImGui.Button("保存设置"))
        {
            plugin.ApplyAutoTyperSettings();
            plugin.SaveConfig();
        }

        ImGui.SameLine();
        ImGui.TextDisabled($"当前：{config.AutoCharDelayMs}ms/字 ±{config.AutoJitterPercent}%，反应 {config.AutoStartDelayMinMs}~{config.AutoStartDelayMaxMs}ms，提交停顿 {config.AutoSubmitDelayMinMs}~{config.AutoSubmitDelayMaxMs}ms");

        ImGui.Separator();
        ImGui.TextColored(Gold, "手动测试输入");
        ImGui.SetNextItemWidth(-160f);
        ImGui.InputText("##kbb-manual", ref manualInput, 256, ImGuiInputTextFlags.EnterReturnsTrue);
        ImGui.SameLine();
        var send = ImGui.Button("发送并回车");
        if (send)
            plugin.TryManualInput(manualInput);

        ImGui.SameLine();
        if (ImGui.Button("按回车"))
            plugin.AutoTyper.SendEnter();

        if (plugin.LastAutoMessage.Length > 0)
            ImGui.TextWrapped(plugin.LastAutoMessage);

        if (plugin.AutoTyper.LastError is not null)
            ImGui.TextColored(Red, plugin.AutoTyper.LastError);

        if (plugin.Ktg.LastError is not null)
            ImGui.TextColored(Red, plugin.Ktg.LastError);

        ImGui.Separator();
        ImGui.TextDisabled("自动输入只在游戏窗口位于前台时发送；词读到变化才输入，同词不会重复。");
    }

    // ─────────────────────────────────────────────────────────────
    // 日志
    // ─────────────────────────────────────────────────────────────

    private void DrawLogTab()
    {
        var config = plugin.Configuration;
        var chat = plugin.ChatLog;
        var tailer = plugin.LogTailer;

        ImGui.TextColored(Gold, "数据采集");

        var recordChat = config.RecordChatLog;
        if (ImGui.Checkbox("记录游戏聊天消息", ref recordChat))
        {
            config.RecordChatLog = recordChat;
            chat.Enabled = recordChat;
            plugin.SaveConfig();
        }

        ImGui.TextDisabled($"已记录 {chat.CapturedLines} 行 → {chat.LogDirectory}");
        if (chat.LastError is not null)
            ImGui.TextColored(Red, chat.LastError);

        ImGui.Separator();

        var tailLog = config.TailDalamudLog;
        if (ImGui.Checkbox("跟随 dalamud.log", ref tailLog))
        {
            config.TailDalamudLog = tailLog;
            tailer.Enabled = tailLog;
            if (tailLog)
                tailer.Attach();
            plugin.SaveConfig();
        }

        ImGui.SetNextItemWidth(-110f);
        if (ImGui.InputText("##kbb-logpath", ref logPathInput, 512))
        {
            config.DalamudLogPath = logPathInput.Trim();
            plugin.SaveConfig();
        }

        ImGui.SameLine();
        if (ImGui.Button("重新挂载"))
            tailer.Attach();

        ImGui.TextDisabled(tailer.ResolvedPath is null ? "路径：自动探测中（未找到 dalamud.log）" : $"路径：{tailer.ResolvedPath}");
        ImGui.TextDisabled($"状态：{(tailer.IsAttached ? "已连接" : "未连接")}   抓取 {tailer.CapturedLines} 行");
        if (tailer.LastError is not null)
            ImGui.TextColored(Red, tailer.LastError);

        ImGui.Separator();

        ImGui.Text("关键词过滤（逗号分隔，留空 = 全部记录）");
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.InputText("##kbb-keywords", ref keywordInput, 512))
        {
            config.LogKeywords = keywordInput.Trim();
            chat.KeywordFilter = config.LogKeywords;
            tailer.KeywordFilter = config.LogKeywords;
            plugin.SaveConfig();
        }

        ImGui.Separator();
        if (ImGui.Button("打开日志目录"))
            plugin.OpenLogFolder();

        ImGui.SameLine();
        if (ImGui.Button("清空最近行"))
        {
            chat.ClearRecent();
            tailer.ClearRecent();
        }

        var chatLines = chat.GetRecent();
        if (ImGui.CollapsingHeader($"聊天最近行（{chatLines.Count}）"))
        {
            ImGui.BeginChild("kbb-chat-lines", new Vector2(0, 120), true);
            foreach (var line in chatLines)
                ImGui.TextUnformatted(line);
            ImGui.EndChild();
        }

        var logLines = tailer.GetRecent();
        if (ImGui.CollapsingHeader($"dalamud.log 最近行（{logLines.Count}）"))
        {
            ImGui.BeginChild("kbb-dalamud-lines", new Vector2(0, 160), true);
            foreach (var line in logLines)
                ImGui.TextUnformatted(line);
            ImGui.EndChild();
        }
    }

    // ─────────────────────────────────────────────────────────────
    // 探测
    // ─────────────────────────────────────────────────────────────

    private void DrawProbeTab()
    {
        var config = plugin.Configuration;
        var probe = plugin.AddonProbe;

        ImGui.TextColored(Gold, "游戏 UI 探测");
        ImGui.TextWrapped("记录 AtkAddon 面板名 / 文本；「魔光键影」面板是 KTG* 系列（KTGEWord = 敌人词，KTGInput = 输入框）。");

        var enabled = config.EnableAddonProbe;
        if (ImGui.Checkbox("记录游戏 UI 面板出现事件", ref enabled))
        {
            config.EnableAddonProbe = enabled;
            probe.Enabled = enabled;
            plugin.SaveConfig();
        }

        ImGui.SameLine();
        if (ImGui.Button("打开日志目录"))
            plugin.OpenLogFolder();

        ImGui.SameLine();
        if (ImGui.Button("清空列表"))
            probe.ClearRecent();

        ImGui.TextDisabled($"已见 {probe.SeenCount} 个面板名");
        if (probe.LastError is not null)
            ImGui.TextColored(Red, probe.LastError);

        ImGui.Separator();
        ImGui.Text("最近出现的面板（点击名字填入下方）");
        ImGui.BeginChild("kbb-addon-list", new Vector2(0, 100), true);
        var recent = probe.GetRecent();
        if (recent.Count == 0)
            ImGui.TextDisabled("（暂无记录）");
        foreach (var snapshot in recent)
        {
            if (ImGui.Selectable($"{snapshot.Time:HH:mm:ss}  {snapshot.Name}"))
                probeAddonInput = snapshot.Name;
        }

        ImGui.EndChild();

        ImGui.SetNextItemWidth(-120f);
        ImGui.InputText("##kbb-addon-name", ref probeAddonInput, 128);
        ImGui.SameLine();
        if (ImGui.Button("读取 AtkValues"))
        {
            try
            {
                probeValues = probe.ReadAddonValues(probeAddonInput);
                probeError = null;
            }
            catch (Exception ex)
            {
                probeValues = new List<(int, string)>();
                probeError = ex.Message;
            }
        }

        if (probeError is not null)
            ImGui.TextColored(Red, probeError);

        if (probeValues.Count > 0)
        {
            if (ImGui.Button("复制全部文本"))
                ImGui.SetClipboardText(string.Join("\n", probeValues.Select(v => v.Value)));

            ImGui.SameLine();
            ImGui.TextDisabled($"共 {probeValues.Count} 个值");

            ImGui.BeginChild("kbb-atkvalues", new Vector2(0, 140), true);
            foreach (var (index, value) in probeValues)
                ImGui.TextUnformatted($"[{index}] {value}");
            ImGui.EndChild();
        }

        ImGui.Separator();
        ImGui.TextColored(Gold, "关键字事件");
        var engineEnabled = config.EnableKeywordEngine;
        if (ImGui.Checkbox("启用关键字事件引擎", ref engineEnabled))
        {
            config.EnableKeywordEngine = engineEnabled;
            plugin.KeywordEngine.Enabled = engineEnabled;
            plugin.SaveConfig();
        }

        ImGui.SameLine();
        var notify = config.NotifyKeywordMatchesInChat;
        if (ImGui.Checkbox("命中时聊天提示", ref notify))
        {
            config.NotifyKeywordMatchesInChat = notify;
            plugin.SaveConfig();
        }

        ImGui.Text("规则（每行：关键词|事件名）");
        ImGui.InputTextMultiline("##kbb-rules", ref keywordRulesInput, 4096, new Vector2(-1, 70));
        if (ImGui.Button("保存并重载规则"))
        {
            config.KeywordRules = keywordRulesInput;
            plugin.KeywordEngine.LoadRules(keywordRulesInput);
            plugin.SaveConfig();
        }

        ImGui.SameLine();
        if (ImGui.Button("恢复内置默认"))
        {
            keywordRulesInput = string.Empty;
            config.KeywordRules = string.Empty;
            plugin.KeywordEngine.LoadRules(string.Empty);
            plugin.SaveConfig();
        }

        ImGui.SameLine();
        ImGui.TextDisabled($"{plugin.KeywordEngine.Rules.Count} 条规则，命中 {plugin.KeywordEngine.MatchCount} 次");

        var events = plugin.KeywordEngine.GetRecent();
        ImGui.BeginChild("kbb-events", new Vector2(0, 90), true);
        if (events.Count == 0)
            ImGui.TextDisabled("（还没有事件）");
        foreach (var match in events)
            ImGui.TextUnformatted($"{match.Time:HH:mm:ss}  [{match.Source}]  {match.EventName}（{match.Keyword}）");
        ImGui.EndChild();
    }

    // ─────────────────────────────────────────────────────────────
    // 设置
    // ─────────────────────────────────────────────────────────────

    private void DrawSettingsTab()
    {
        var config = plugin.Configuration;

        var scale = config.UiScale;
        if (ImGui.SliderFloat("界面缩放", ref scale, 0.8f, 1.6f, "%.2f"))
        {
            config.UiScale = scale;
        }

        var showOnLogin = config.ShowWindowOnLogin;
        if (ImGui.Checkbox("登录后自动打开窗口", ref showOnLogin))
        {
            config.ShowWindowOnLogin = showOnLogin;
            plugin.SaveConfig();
        }

        var recent = config.RecentLogLines;
        if (ImGui.DragInt("日志保留行数", ref recent, 10f, 50, 2000))
            config.RecentLogLines = Math.Clamp(recent, 50, 2000);

        ImGui.Separator();
        ImGui.TextColored(Gold, "自动弹窗");

        var autoTerritory = config.AutoOpenOnTerritory;
        if (ImGui.Checkbox("进入指定地图时自动弹窗", ref autoTerritory))
        {
            config.AutoOpenOnTerritory = autoTerritory;
            plugin.SaveConfig();
        }

        ImGui.TextDisabled("地图名关键词（逗号分隔）");
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.InputText("##kbb-territory-kw", ref territoryKeywordsInput, 256))
        {
            config.TerritoryKeywords = territoryKeywordsInput.Trim();
            plugin.SaveConfig();
        }

        var autoAddon = config.AutoOpenOnAddon;
        if (ImGui.Checkbox("检测到指定面板时自动弹窗", ref autoAddon))
        {
            config.AutoOpenOnAddon = autoAddon;
            plugin.SaveConfig();
        }

        ImGui.TextDisabled("面板名关键词（逗号分隔，同时用于自动抓取面板文本）");
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.InputText("##kbb-addon-kw", ref addonKeywordsInput, 256))
        {
            config.AddonKeywords = addonKeywordsInput.Trim();
            plugin.AddonProbe.SnapshotKeywords = config.AddonKeywords;
            plugin.SaveConfig();
        }

        var autoSnapshot = config.EnableAutoSnapshot;
        if (ImGui.Checkbox("自动抓取面板文本", ref autoSnapshot))
        {
            config.EnableAutoSnapshot = autoSnapshot;
            plugin.AddonProbe.AutoSnapshot = autoSnapshot;
            plugin.SaveConfig();
        }

        ImGui.Separator();
        if (ImGui.Button("保存设置"))
            plugin.SaveConfig();

        ImGui.SameLine();
        ImGui.TextDisabled("配置：pluginConfigs\\KeyboundBrawler\\Configuration.json");

        ImGui.Separator();
        ImGui.TextDisabled("合规：只读取游戏 UI 文本；自动输入仅在你自己的游戏窗口前台时发送，不修改游戏数据。");
    }
}
