using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;

namespace KeyboundBrawler.Core;

/// <summary>一个最近出现过（PostShow）的游戏 UI 面板。</summary>
public sealed record AddonSnapshot(string Name, DateTime Time);

/// <summary>
/// 游戏 UI 探测（占位）：监听所有 AtkAddon 的 PostShow，记录面板名；
/// 对可疑面板自动抓取其 AtkValues 文本（写 logs\addon-text-*.log），
/// 并支持按名字手动读取 AtkValues。
///
/// 用途：实机确认「魔光键影」用的面板名和文本节点，后续据此解析敌人头顶的词。
/// 只读取 UI 数据，不修改任何游戏内容。
/// </summary>
public sealed class AddonProbe : IDisposable
{
    private readonly IAddonLifecycle lifecycle;
    private readonly IGameGui gameGui;
    private readonly IPluginLog pluginLog;
    private readonly string logDirectory;
    private readonly List<AddonSnapshot> recent = new();
    private readonly HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> lastSnapshotAt = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> lastSnapshotText = new(StringComparer.OrdinalIgnoreCase);
    private readonly object gate = new();
    private bool disposed;

    public AddonProbe(IAddonLifecycle lifecycle, IGameGui gameGui, IPluginLog pluginLog, string logDirectory)
    {
        this.lifecycle = lifecycle;
        this.gameGui = gameGui;
        this.pluginLog = pluginLog;
        this.logDirectory = logDirectory;

        Directory.CreateDirectory(logDirectory);
        lifecycle.RegisterListener(AddonEvent.PostShow, OnAddonPostShow);
    }

    /// <summary>是否记录面板出现（关掉后不再写入新发现）。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>是否自动抓取可疑面板的 AtkValues 文本。</summary>
    public bool AutoSnapshot { get; set; } = true;

    /// <summary>需要自动抓取文本的面板名关键词（逗号分隔）。</summary>
    public string SnapshotKeywords { get; set; } = "Keybound,Brawler,Keyward,魔光,键影";

    public long SeenCount
    {
        get
        {
            lock (gate)
            {
                return seen.Count;
            }
        }
    }

    public long SnapshotCount { get; private set; }

    public string? LastError { get; private set; }

    public string LogDirectory => logDirectory;

    /// <summary>任意面板显示时触发（参数：面板名）。用来做自动弹窗判断。</summary>
    public event Action<string>? AddonShown;

    /// <summary>抓到面板文本时触发（参数：面板名、非空文本列表）。用来喂关键字引擎。</summary>
    public event Action<string, IReadOnlyList<string>>? TextSnapshotted;

    private void OnAddonPostShow(AddonEvent type, AddonArgs args)
    {
        if (!Enabled || disposed)
            return;

        try
        {
            var name = args.AddonName;
            if (string.IsNullOrWhiteSpace(name))
                return;

            var isNew = false;
            lock (gate)
            {
                isNew = seen.Add(name);
                recent.Insert(0, new AddonSnapshot(name, DateTime.Now));
                while (recent.Count > 120)
                    recent.RemoveAt(recent.Count - 1);
            }

            if (isNew)
                WriteAddonName(name);

            AddonShown?.Invoke(name);

            if (AutoSnapshot)
                TrySnapshot(name);
        }
        catch (Exception ex)
        {
            LastError = $"addon 记录失败：{ex.Message}";
        }
    }

    /// <summary>对面板做一次文本快照（限流 + 内容变化才写）。</summary>
    private void TrySnapshot(string name)
    {
        var now = DateTime.Now;

        lock (gate)
        {
            if (lastSnapshotAt.TryGetValue(name, out var last) && (now - last).TotalMilliseconds < 2500)
                return;
            lastSnapshotAt[name] = now;
        }

        List<(int Index, string Value)> values;
        try
        {
            values = ReadAddonValues(name);
        }
        catch
        {
            return;
        }

        if (values.Count == 0)
            return;

        var texts = values.Where(v => !string.IsNullOrWhiteSpace(v.Value)).Select(v => v.Value).ToList();
        if (texts.Count == 0)
            return;

        // 只对可疑面板（名字命中关键词）或文本较多的面板写完整快照，避免刷屏
        var suspicious = MatchesKeywords(name, SnapshotKeywords);
        if (!suspicious && texts.Count < 3)
            return;

        var payload = $"[{name}] {string.Join(" | ", texts.Select(Tidy))}";

        lock (gate)
        {
            if (lastSnapshotText.TryGetValue(name, out var previous) && previous == payload)
                return;
            lastSnapshotText[name] = payload;
        }

        SnapshotCount++;
        WriteSnapshot(payload);
        TextSnapshotted?.Invoke(name, texts);
    }

    private void WriteAddonName(string name)
    {
        try
        {
            var path = Path.Combine(logDirectory, $"addons-{DateTime.Now:yyyyMMdd}.log");
            File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss} {name}{Environment.NewLine}", new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            LastError = $"写面板名日志失败：{ex.Message}";
        }
    }

    private void WriteSnapshot(string payload)
    {
        try
        {
            var path = Path.Combine(logDirectory, $"addon-text-{DateTime.Now:yyyyMMdd}.log");
            File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss} {payload}{Environment.NewLine}", new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            LastError = $"写面板文本日志失败：{ex.Message}";
        }
    }

    private static string Tidy(string text) => text.Replace('\r', ' ').Replace('\n', ' ').Trim();

    private static bool MatchesKeywords(string name, string keywords)
    {
        if (string.IsNullOrWhiteSpace(keywords))
            return false;

        foreach (var keyword in keywords.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (name.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>最近出现过的面板（新的在前）。</summary>
    public IReadOnlyList<AddonSnapshot> GetRecent()
    {
        lock (gate)
        {
            return recent.ToArray();
        }
    }

    public void ClearRecent()
    {
        lock (gate)
        {
            recent.Clear();
        }
    }

    /// <summary>读取指定面板当前所有 AtkValue 的值（文本 / 数字），供实机调试用。</summary>
    public List<(int Index, string Value)> ReadAddonValues(string addonName)
    {
        var result = new List<(int, string)>();

        if (string.IsNullOrWhiteSpace(addonName))
            throw new ArgumentException("addon 名不能为空", nameof(addonName));

        var addon = gameGui.GetAddonByName(addonName, 1);
        if (addon.IsNull)
            throw new InvalidOperationException($"面板不存在或未打开：{addonName}");
        if (!addon.IsReady)
            throw new InvalidOperationException($"面板尚未就绪：{addonName}");

        var index = 0;
        foreach (var value in addon.AtkValues)
        {
            try
            {
                var boxed = value.GetValue();
                if (boxed is not null)
                    result.Add((index, boxed.ToString() ?? string.Empty));
            }
            catch (Exception ex)
            {
                result.Add((index, $"<读取失败: {ex.Message}>"));
            }

            index++;
        }

        return result;
    }

    /// <summary>读取指定面板的可见文本（只保留非空字符串值）。</summary>
    public List<string> ReadAddonTexts(string addonName) =>
        ReadAddonValues(addonName).Select(pair => pair.Value)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .ToList();

    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;
        lifecycle.UnregisterListener(AddonEvent.PostShow, OnAddonPostShow);
    }
}
