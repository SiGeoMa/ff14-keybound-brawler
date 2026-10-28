using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Dalamud.Plugin.Services;

namespace KeyboundBrawler.Core;

/// <summary>一次关键字命中。</summary>
public readonly record struct KeywordMatch(string Source, string Keyword, string EventName, string Text, DateTime Time);

/// <summary>
/// 关键字事件引擎（占位）：把聊天 / 日志 / UI 面板文本喂进来，按规则匹配关键词，
/// 命中后记录到 logs\events-*.log，并触发 <see cref="Matched"/>。
///
/// 用途：等实机确认「魔光键影」在游戏里用什么文字提示（开始 / 波次 / 得分 / 完成 / 失败），
/// 就用这里配置规则，插件即可判断事件进度。
/// </summary>
public sealed class KeywordEngine : IDisposable
{
    /// <summary>内置默认规则（可在设置里覆盖）。</summary>
    public static IReadOnlyList<KeywordRule> DefaultRules => KeywordRuleParser.DefaultRules;

    private readonly IPluginLog pluginLog;
    private readonly string logDirectory;
    private readonly List<KeywordRule> rules = new();
    private readonly Queue<KeywordMatch> recent = new();
    private readonly Dictionary<string, DateTime> lastMatchAt = new();
    private readonly object gate = new();
    private bool disposed;

    public KeywordEngine(IPluginLog pluginLog, string logDirectory)
    {
        this.pluginLog = pluginLog;
        this.logDirectory = logDirectory;
        Directory.CreateDirectory(logDirectory);
        LoadRules(string.Empty);
    }

    public bool Enabled { get; set; } = true;

    public long MatchCount { get; private set; }

    public string? LastError { get; private set; }

    /// <summary>最近事件（新的在前），UI 展示用。</summary>
    public IReadOnlyList<KeywordMatch> GetRecent()
    {
        lock (gate)
        {
            return recent.Reverse().ToArray();
        }
    }

    public void ClearRecent()
    {
        lock (gate)
        {
            recent.Clear();
        }
    }

    /// <summary>从多行文本加载规则；空文本 = 使用内置默认规则。</summary>
    public void LoadRules(string text)
    {
        rules.Clear();
        rules.AddRange(KeywordRuleParser.Parse(text));
    }

    public IReadOnlyList<KeywordRule> Rules => rules;

    /// <summary>当前规则的多行文本形式（给 UI 编辑用）。</summary>
    public string RulesToText() => KeywordRuleParser.ToText(rules);

    /// <summary>评估一段文本（来源用于标注：聊天 / dalamud / addon:名字）。</summary>
    public void Evaluate(string source, string text)
    {
        if (!Enabled || disposed || string.IsNullOrWhiteSpace(text))
            return;

        try
        {
            var now = DateTime.Now;
            foreach (var rule in rules)
            {
                if (!text.Contains(rule.Keyword, StringComparison.OrdinalIgnoreCase))
                    continue;

                // 同一来源同一关键词 1.5 秒内只记一次，避免 addon 快照刷屏
                var key = $"{source}|{rule.Keyword}";
                lock (gate)
                {
                    if (lastMatchAt.TryGetValue(key, out var last) && (now - last).TotalMilliseconds < 1500)
                        continue;
                    lastMatchAt[key] = now;
                }

                var match = new KeywordMatch(source, rule.Keyword, rule.EventName, Truncate(text, 300), now);
                MatchCount++;

                lock (gate)
                {
                    recent.Enqueue(match);
                    while (recent.Count > 200)
                        recent.Dequeue();
                }

                WriteEventLog(match);
                Matched?.Invoke(match);
            }
        }
        catch (Exception ex)
        {
            LastError = $"关键字匹配失败：{ex.Message}";
        }
    }

    private void WriteEventLog(KeywordMatch match)
    {
        try
        {
            var path = Path.Combine(logDirectory, $"events-{match.Time:yyyyMMdd}.log");
            var line = $"{match.Time:yyyy-MM-dd HH:mm:ss.fff} [{match.Source}] {match.Keyword} -> {match.EventName} | {match.Text}";
            File.AppendAllText(path, line + Environment.NewLine, new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            LastError = $"写事件日志失败：{ex.Message}";
            pluginLog.Warning(ex, "写事件日志失败");
        }
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "…";

    /// <summary>关键字命中事件（参数：来源、事件名、原文）。</summary>
    public event Action<KeywordMatch>? Matched;

    public void Dispose()
    {
        disposed = true;
    }
}
