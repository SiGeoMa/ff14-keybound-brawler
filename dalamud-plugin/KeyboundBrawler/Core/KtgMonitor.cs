using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace KeyboundBrawler.Core;

/// <summary>KTGEWord 里的一个敌人词节点：文本 + 屏幕位置（用于按远近排序）。</summary>
public sealed class WordNode
{
    public string Text { get; init; } = string.Empty;
    public short X { get; init; }
    public short Y { get; init; }

    public override string ToString() => $"{Text}({X},{Y})";
}

/// <summary>KTG（Keybound Brawler / 魔光键影）面板的一次快照。</summary>
public sealed class KtgState
{
    public string Word { get; init; } = string.Empty;
    public string Input { get; init; } = string.Empty;
    public string Wave { get; init; } = string.Empty;
    public string Score { get; init; } = string.Empty;
    public string GaugeHp { get; init; } = string.Empty;
    public string Todo { get; init; } = string.Empty;
    public bool InGame { get; init; }
    public bool GameOver { get; init; }

    public bool HasWord => !string.IsNullOrWhiteSpace(Word);
}

/// <summary>
/// 读取「魔光键影」的 KTG* 面板（实测面板名）。
///
/// 词文本不在 AtkValues 里（全数字，且是静态模板值），也不一定在 UldManager 的直接子节点，
/// 所以这里递归遍历节点树（含 AtkComponentNode 内部节点），并保留整树 dump 用于诊断。
/// 只读，不修改游戏数据。
/// </summary>
public sealed class KtgMonitor
{
    private const int PollIntervalMs = 100;

    private static readonly string[] ProbeAddons =
    {
        "KTGEWord", "KTGInput", "_KTGWave", "KTGHudScore", "KTGHudGaugeHp", "KTGHudTodo",
        "KTGHudLog", "KTGPlayGuide", "KTGEnemyCastBar", "KTGIGaugeHp", "KTGScreenTextFever",
        "_KTGLocationTitle", "_KTGCountDown",
    };

    private readonly IGameGui gameGui;
    private readonly IPluginLog pluginLog;
    private DateTime lastPoll = DateTime.MinValue;
    private DateTime lastDumpAt = DateTime.MinValue;

    public KtgMonitor(IGameGui gameGui, IPluginLog pluginLog)
    {
        this.gameGui = gameGui;
        this.pluginLog = pluginLog;
    }

    public KtgState State { get; private set; } = new();

    /// <summary>KTGEWord 里的每个敌人词节点（文本 + 屏幕坐标），每次 Poll 更新。</summary>
    public List<WordNode> WordNodes { get; private set; } = new();

    /// <summary>上一次成功读取面板的时间（判断数据是否过期）。</summary>
    public DateTime StateUpdatedAt { get; private set; } = DateTime.MinValue;

    public string? LastError { get; private set; }

    /// <summary>诊断用：节点树 dump 回调（由 Plugin 写日志）。</summary>
    public Action<string>? DiagnosticDump { get; set; }

    public event Action<KtgState>? StateChanged;

    /// <summary>由 Framework.Update 调用，内部节流。</summary>
    public void Poll(bool force = false)
    {
        var now = DateTime.Now;
        if (!force && (now - lastPoll).TotalMilliseconds < PollIntervalMs)
            return;
        lastPoll = now;

        try
        {
            var inGame = IsAddonVisible("KTGEWord") || IsAddonVisible("KTGHudScore");
            var gameOver = IsAddonVisible("_KTGGameover") || IsAddonVisible("KTGResult");
            if (!inGame && !gameOver)
            {
                if (State.InGame || State.GameOver)
                {
                    State = new KtgState();
                    StateChanged?.Invoke(State);
                }

                return;
            }

            var state = new KtgState
            {
                Word = ReadTextNodes("KTGEWord"),
                Input = ReadInput(),
                Wave = ReadTextNodes("_KTGWave"),
                Score = ReadTextNodes("KTGHudScore"),
                GaugeHp = ReadTextNodes("KTGHudGaugeHp"),
                Todo = ReadTextNodes("KTGHudTodo"),
                InGame = inGame,
                GameOver = gameOver,
            };

            var changed = !SameState(State, state);
            State = state;
            StateUpdatedAt = DateTime.Now;
            WordNodes = ReadWordNodes();
            if (changed)
                StateChanged?.Invoke(state);

            // 词读不到时自动 dump 节点树，帮助定位（每 6 秒最多一次）
            if (inGame && !state.HasWord && (now - lastDumpAt).TotalSeconds > 6)
            {
                lastDumpAt = now;
                var dumps = DumpAllProbeAddons();
                if (dumps.Count > 0)
                    DiagnosticDump?.Invoke(string.Join(Environment.NewLine, dumps));
            }
        }
        catch (Exception ex)
        {
            LastError = $"读取 KTG 面板失败：{ex.Message}";
            pluginLog.Debug(ex, "读取 KTG 面板失败");
        }
    }

    private static bool SameState(KtgState a, KtgState b) =>
        a.Word == b.Word && a.Input == b.Input && a.Wave == b.Wave && a.Score == b.Score &&
        a.GaugeHp == b.GaugeHp && a.Todo == b.Todo && a.InGame == b.InGame && a.GameOver == b.GameOver;

    /// <summary>面板是否存在且可见。</summary>
    public bool IsAddonVisible(string addonName)
    {
        try
        {
            var ptr = gameGui.GetAddonByName(addonName, 1);
            return !ptr.IsNull && ptr.IsVisible;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>读 KTGEWord 里每个敌人词节点（文本 + 屏幕坐标）。</summary>
    public unsafe List<WordNode> ReadWordNodes()
    {
        var result = new List<WordNode>();
        var ptr = gameGui.GetAddonByName("KTGEWord", 1);
        if (ptr.IsNull)
            return result;

        var unit = (AtkUnitBase*)ptr.Address;
        if (unit == null)
            return result;

        var uld = unit->UldManager;
        for (var i = 0; i < uld.NodeListCount; i++)
        {
            var node = uld.NodeList[i];
            if (node == null)
                continue;

            var builder = new StringBuilder();
            var visited = new HashSet<IntPtr>();
            CollectText(node, builder, 0, visited);
            var text = builder.ToString().Trim();
            if (text.Length == 0)
                continue;

            // 同一个词只保留第一个
            if (result.Any(w => w.Text == text))
                continue;

            result.Add(new WordNode
            {
                Text = text,
                X = (short)node->X,
                Y = (short)node->Y,
            });
        }

        return result;
    }

    /// <summary>读面板所有文本节点（递归组件/子节点），拼接返回。</summary>
    public unsafe string ReadTextNodes(string addonName)
    {
        var ptr = gameGui.GetAddonByName(addonName, 1);
        if (ptr.IsNull)
            return string.Empty;

        var unit = (AtkUnitBase*)ptr.Address;
        if (unit == null)
            return string.Empty;

        var builder = new StringBuilder();
        var visited = new HashSet<IntPtr>();
        var uld = unit->UldManager;
        for (var i = 0; i < uld.NodeListCount; i++)
            CollectText(uld.NodeList[i], builder, 0, visited);

        return builder.ToString();
    }

    private static unsafe void CollectText(AtkResNode* node, StringBuilder builder, int depth, HashSet<IntPtr> visited)
    {
        if (node == null || depth > 10)
            return;

        if (!visited.Add((IntPtr)node))
            return;

        // 文本节点：直接取 NodeText
        if (node->Type == NodeType.Text)
            AppendText((AtkTextNode*)node, builder);

        // 子节点链
        for (var child = node->ChildNode; child != null; child = child->NextSiblingNode)
            CollectText(child, builder, depth + 1, visited);

        // 组件节点：type >= 1000 才是 AtkComponentNode（0-9 是普通节点）
        if (IsComponentNode(node))
        {
            var componentNode = (AtkComponentNode*)node;
            var component = componentNode->Component;
            if (component != null)
            {
                var uld = component->UldManager;
                for (var i = 0; i < uld.NodeListCount; i++)
                    CollectText(uld.NodeList[i], builder, depth + 1, visited);
            }
        }
    }

    /// <summary>组件节点判断：实测 KTGEWord 的节点 type=1001/1002，不是 NodeType.Component。</summary>
    private static unsafe bool IsComponentNode(AtkResNode* node) => (uint)node->Type >= 1000;

    private static unsafe void AppendText(AtkTextNode* textNode, StringBuilder builder)
    {
        if (textNode == null)
            return;

        var text = ReadTextNodeText(textNode);
        if (string.IsNullOrWhiteSpace(text))
            return;

        if (builder.Length > 0)
            builder.Append(' ');

        builder.Append(text.Trim());
    }

    /// <summary>读文本节点：优先 NodeText，空则退回 OriginalTextPointer。</summary>
    private static unsafe string ReadTextNodeText(AtkTextNode* textNode)
    {
        try
        {
            var text = textNode->NodeText.ToString();
            if (!string.IsNullOrWhiteSpace(text))
                return text;
        }
        catch
        {
            // NodeText 读失败时继续尝试 OriginalTextPointer
        }

        try
        {
            return textNode->OriginalTextPointer.ToString();
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>dump 面板完整节点树（类型 / 节点 ID / 文本），诊断用。</summary>
    public unsafe List<string> DumpNodeTree(string addonName, int maxNodes = 200)
    {
        var result = new List<string>();
        var ptr = gameGui.GetAddonByName(addonName, 1);
        if (ptr.IsNull)
            return result;

        var unit = (AtkUnitBase*)ptr.Address;
        if (unit == null)
            return result;

        var visited = new HashSet<IntPtr>();
        var uld = unit->UldManager;
        for (var i = 0; i < uld.NodeListCount && result.Count < maxNodes; i++)
            DumpNode(uld.NodeList[i], 0, result, visited, maxNodes);

        return result;
    }

    private static unsafe void DumpNode(AtkResNode* node, int depth, List<string> output, HashSet<IntPtr> visited, int maxNodes)
    {
        if (node == null || depth > 10 || output.Count >= maxNodes)
            return;

        if (!visited.Add((IntPtr)node))
            return;

        var indent = new string(' ', depth * 2);
        var text = string.Empty;
        if (node->Type == NodeType.Text)
            text = ReadTextNodeText((AtkTextNode*)node).Trim();

        output.Add($"{indent}[{depth}] type={node->Type} id={node->NodeId} visible={node->IsVisible()} text=\"{text}\"");

        for (var child = node->ChildNode; child != null; child = child->NextSiblingNode)
            DumpNode(child, depth + 1, output, visited, maxNodes);

        if (IsComponentNode(node))
        {
            var componentNode = (AtkComponentNode*)node;
            var component = componentNode->Component;
            if (component != null)
            {
                var uld = component->UldManager;
                for (var i = 0; i < uld.NodeListCount && output.Count < maxNodes; i++)
                    DumpNode(uld.NodeList[i], depth + 1, output, visited, maxNodes);
            }
        }
    }

    /// <summary>dump 所有 KTG* 面板的节点树（诊断用）。</summary>
    public List<string> DumpAllProbeAddons()
    {
        var lines = new List<string>();
        foreach (var addon in ProbeAddons)
        {
            if (!IsAddonVisible(addon))
                continue;

            lines.Add($"=== {addon} ===");
            lines.AddRange(DumpNodeTree(addon));
        }

        return lines;
    }

    /// <summary>
    /// 读 KTGInput 输入框。
    /// 实测 AtkValues 形如 `6 |  | False | False | 0`，第 2 项（下标 1）是当前输入文本。
    /// 读不到时返回空串。
    /// </summary>
    public string ReadInput()
    {
        try
        {
            var ptr = gameGui.GetAddonByName("KTGInput", 1);
            if (ptr.IsNull || !ptr.IsReady)
                return string.Empty;

            var index = 0;
            foreach (var value in ptr.AtkValues)
            {
                if (index == 1)
                {
                    var boxed = value.GetValue();
                    return boxed?.ToString()?.Trim() ?? string.Empty;
                }

                index++;
            }
        }
        catch (Exception ex)
        {
            LastError = $"读取 KTGInput 失败：{ex.Message}";
        }

        return string.Empty;
    }

    /// <summary>读取某个面板的全部 AtkValue 文本，调试用。</summary>
    public List<(int Index, string Value)> DumpValues(string addonName)
    {
        var result = new List<(int, string)>();
        var ptr = gameGui.GetAddonByName(addonName, 1);
        if (ptr.IsNull)
            throw new InvalidOperationException($"面板不存在：{addonName}");

        var index = 0;
        foreach (var value in ptr.AtkValues)
        {
            try
            {
                var boxed = value.GetValue();
                result.Add((index, boxed?.ToString() ?? "<null>"));
            }
            catch (Exception ex)
            {
                result.Add((index, $"<读取失败: {ex.Message}>"));
            }

            index++;
        }

        return result;
    }
}
