using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using KeyboundBrawler.Core;

namespace KeyboundBrawler.UI;

/// <summary>主窗口：自动 / 设置。</summary>
public sealed class MainWindow : Window
{
    private static readonly Vector4 Gold = new(1.0f, 0.84f, 0.36f, 1.0f);
    private static readonly Vector4 Green = new(0.42f, 0.92f, 0.55f, 1.0f);
    private static readonly Vector4 Red = new(1.0f, 0.42f, 0.42f, 1.0f);

    private static readonly string[] TargetOrders =
    {
        "实体距离（近→远，推荐）",
        "屏幕位置（近→远）",
        "屏幕位置（远→近）",
        "短词优先",
        "长词优先",
    };

    private readonly Plugin plugin;

    private string territoryKeywordsInput = string.Empty;
    private bool initialized;

    public MainWindow(Plugin plugin)
        : base("魔光键影 · 自动##kbb-main")
    {
        this.plugin = plugin;
        Size = new Vector2(560, 560);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(460, 380),
            MaximumSize = new Vector2(1600, 2000),
        };
    }

    public override void Draw()
    {
        var config = plugin.Configuration;

        if (!initialized)
        {
            territoryKeywordsInput = config.TerritoryKeywords;
            initialized = true;
        }

        ImGui.SetWindowFontScale(config.UiScale);

        ImGui.TextColored(Gold, "魔光键影 · 自动");
        ImGui.SameLine();
        ImGui.TextDisabled("v0.4.0");
        ImGui.Separator();

        if (ImGui.BeginTabBar("kbb-tabs"))
        {
            if (ImGui.BeginTabItem("自动"))
            {
                DrawAutoTab();
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

    private void DrawAutoTab()
    {
        var config = plugin.Configuration;
        var state = plugin.Ktg.State;

        // ── 状态 ──────────────────────────────────────────────
        if (!state.InGame && !state.GameOver)
        {
            ImGui.TextDisabled("（未进入魔光键影战斗区域）");
        }
        else if (state.GameOver)
        {
            ImGui.TextColored(Green, "本局结束");
        }
        else
        {
            ImGui.Text($"当前词：{string.Join("  ", System.Linq.Enumerable.Select(plugin.Ktg.WordNodes, n => n.Text))}");
            ImGui.TextDisabled($"输入框：{state.Input}");
            ImGui.Text($"目标：{state.Todo}    分数：{state.Score}");
        }

        ImGui.Separator();

        // ── 开始 / 停止 ───────────────────────────────────────
        if (config.AutoPlay)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.62f, 0.16f, 0.16f, 1.0f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.80f, 0.24f, 0.24f, 1.0f));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.90f, 0.30f, 0.30f, 1.0f));
            if (ImGui.Button("■ 停止自动", new Vector2(180, 40)))
            {
                config.AutoPlay = false;
                plugin.SaveConfig();
            }

            ImGui.PopStyleColor(3);
            ImGui.SameLine();
            ImGui.TextColored(Green, "● 运行中");
        }
        else
        {
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.13f, 0.45f, 0.22f, 1.0f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.18f, 0.60f, 0.30f, 1.0f));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.24f, 0.72f, 0.38f, 1.0f));
            if (ImGui.Button("▶ 开始自动", new Vector2(180, 40)))
            {
                config.AutoPlay = true;
                plugin.SaveConfig();
            }

            ImGui.PopStyleColor(3);
            ImGui.SameLine();
            ImGui.TextDisabled("○ 已停止");
        }

        if (plugin.LastAutoMessage.Length > 0)
            ImGui.TextWrapped(plugin.LastAutoMessage);

        ImGui.Separator();

        // ── 速度 / 拟人化 ─────────────────────────────────────
        ImGui.TextColored(Gold, "打字节奏");

        var blatant = config.AutoBlatantMode;
        if (ImGui.Checkbox("演都不演（关掉拟人化延迟）", ref blatant))
        {
            config.AutoBlatantMode = blatant;
            plugin.ApplyAutoTyperSettings();
            plugin.SaveConfig();
        }

        var humanize = config.AutoHumanize;
        if (ImGui.Checkbox("拟人化（逐字写入随机节奏）", ref humanize))
        {
            config.AutoHumanize = humanize;
            plugin.ApplyAutoTyperSettings();
            plugin.SaveConfig();
        }

        var charDelay = config.AutoCharDelayMs;
        if (ImGui.SliderInt("每字间隔 (ms)", ref charDelay, 40, 300))
        {
            config.AutoCharDelayMs = charDelay;
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

        ImGui.Separator();

        // ── 安全 / 防 AFK ─────────────────────────────────────
        ImGui.TextColored(Gold, "防踢保护");

        var antiAfk = config.AutoAntiAfk;
        if (ImGui.Checkbox("防 AFK 心跳（无绑定按键 + 方向键动作）", ref antiAfk))
        {
            config.AutoAntiAfk = antiAfk;
            plugin.SaveConfig();
        }

        if (config.AutoAntiAfk)
        {
            var afkSeconds = config.AntiAfkIntervalSeconds;
            if (ImGui.SliderInt("心跳间隔 (秒)", ref afkSeconds, 5, 120))
            {
                config.AntiAfkIntervalSeconds = afkSeconds;
                plugin.SaveConfig();
            }
        }

        var refocus = config.AutoRefocusGame;
        if (ImGui.Checkbox("游戏失焦时自动切回前台", ref refocus))
        {
            config.AutoRefocusGame = refocus;
            plugin.SaveConfig();
        }

        ImGui.Separator();

        // ── 选词顺序 ─────────────────────────────────────────
        ImGui.TextColored(Gold, "选词顺序");
        var orderIndex = config.AutoTargetOrder switch
        {
            "screen_desc" => 1,
            "screen_asc" => 2,
            "shortest" => 3,
            "longest" => 4,
            _ => 0,
        };
        if (ImGui.Combo("##kbb-order", ref orderIndex, TargetOrders, TargetOrders.Length))
        {
            config.AutoTargetOrder = orderIndex switch
            {
                1 => "screen_desc",
                2 => "screen_asc",
                3 => "shortest",
                4 => "longest",
                _ => "distance",
            };
            plugin.SaveConfig();
        }

        if (ImGui.Button("保存设置"))
            plugin.SaveConfig();

        ImGui.SameLine();
        if (ImGui.Button("打开日志目录"))
            plugin.OpenLogFolder();
    }

    private void DrawSettingsTab()
    {
        var config = plugin.Configuration;

        var scale = config.UiScale;
        if (ImGui.SliderFloat("界面缩放", ref scale, 0.8f, 1.6f, "%.2f"))
            config.UiScale = scale;

        var showOnLogin = config.ShowWindowOnLogin;
        if (ImGui.Checkbox("登录后自动打开窗口", ref showOnLogin))
        {
            config.ShowWindowOnLogin = showOnLogin;
            plugin.SaveConfig();
        }

        ImGui.Separator();
        ImGui.TextColored(Gold, "自动弹窗");

        var autoOpen = config.AutoOpenOnTerritory;
        if (ImGui.Checkbox("进入魔光键影相关地图时自动弹窗", ref autoOpen))
        {
            config.AutoOpenOnTerritory = autoOpen;
            plugin.SaveConfig();
        }

        ImGui.TextDisabled("地图名关键词（逗号分隔）");
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.InputText("##kbb-territory-kw", ref territoryKeywordsInput, 256))
        {
            config.TerritoryKeywords = territoryKeywordsInput.Trim();
            plugin.SaveConfig();
        }

        ImGui.Separator();
        if (ImGui.Button("保存设置"))
            plugin.SaveConfig();

        ImGui.SameLine();
        ImGui.TextDisabled($"词库 {plugin.Book.Count} 条");

        if (plugin.DataError is not null)
            ImGui.TextColored(Red, plugin.DataError);

        ImGui.Separator();
        ImGui.TextDisabled("只读取游戏 UI 文本，自动输入只在你自己的游戏窗口内生效。");
    }
}
