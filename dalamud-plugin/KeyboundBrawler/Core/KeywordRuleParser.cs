using System;
using System.Collections.Generic;
using System.Linq;

namespace KeyboundBrawler.Core;

/// <summary>一条关键字规则：命中关键词时触发的事件名。</summary>
public sealed record KeywordRule(string Keyword, string EventName);

/// <summary>
/// 关键字规则文本解析（纯逻辑，不依赖 Dalamud，方便自检）。
/// 格式：每行 `关键词|事件名`，`#` 开头为注释；分隔符也接受 `->` `=>` `=` `：` `:`。
/// 空文本 = 内置默认规则。
/// </summary>
public static class KeywordRuleParser
{
    private static readonly string[] Separators = { "->", "=>", "|", "=", "：", ":" };

    /// <summary>内置默认规则（未实机确认前的合理猜测，确认后可在设置里覆盖）。</summary>
    public static readonly IReadOnlyList<KeywordRule> DefaultRules = new KeywordRule[]
    {
        new("魔光键影", "进入玩法"),
        new("键斗广场", "进入场地"),
        new("Keybound", "进入玩法"),
        new("Keyward", "进入玩法"),
        new("波次", "波次事件"),
        new("Wave", "波次事件"),
        new("通关", "完成"),
        new("Clear", "完成"),
        new("得分", "得分事件"),
        new("Score", "得分事件"),
        new("连击", "连击事件"),
        new("Combo", "连击事件"),
    };

    public static List<KeywordRule> Parse(string? text)
    {
        var rules = new List<KeywordRule>();
        if (string.IsNullOrWhiteSpace(text))
        {
            rules.AddRange(DefaultRules);
            return rules;
        }

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            var separator = Separators.FirstOrDefault(s => line.Contains(s, StringComparison.Ordinal));
            string keyword;
            string eventName;

            if (separator is not null)
            {
                var parts = line.Split(separator, 2, StringSplitOptions.TrimEntries);
                keyword = parts[0];
                eventName = parts.Length > 1 ? parts[1] : "事件";
            }
            else
            {
                keyword = line;
                eventName = "事件";
            }

            if (string.IsNullOrWhiteSpace(keyword) || string.IsNullOrWhiteSpace(eventName))
                continue;

            // 关键字不能包含分隔符本身，避免解析出奇怪结果
            if (string.IsNullOrWhiteSpace(keyword))
                continue;

            rules.Add(new KeywordRule(keyword, eventName));
        }

        return rules.Count > 0 ? rules : DefaultRules.ToList();
    }

    public static string ToText(IEnumerable<KeywordRule> rules) =>
        string.Join(Environment.NewLine, rules.Select(r => $"{r.Keyword}|{r.EventName}"));
}
