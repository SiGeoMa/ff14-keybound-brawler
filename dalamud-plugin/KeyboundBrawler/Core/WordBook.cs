using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KeyboundBrawler.Core;

/// <summary>词库条目：词、无声调拼音（连写）、带空格的拼音（备用）、词频。</summary>
public sealed class WordEntry
{
    [JsonPropertyName("word")]
    public string Word { get; set; } = string.Empty;

    [JsonPropertyName("pinyin")]
    public string Pinyin { get; set; } = string.Empty;

    [JsonPropertyName("pinyinSpaced")]
    public string? PinyinSpaced { get; set; }

    [JsonPropertyName("freq")]
    public int Frequency { get; set; }

    [JsonIgnore]
    public int Length => Word.Length;
}

/// <summary>words.json 文件结构。</summary>
public sealed class WordBookFile
{
    [JsonPropertyName("source")]
    public string Source { get; set; } = string.Empty;

    [JsonPropertyName("count")]
    public int Count { get; set; }

    [JsonPropertyName("words")]
    public List<WordEntry> Words { get; set; } = new();
}

/// <summary>词库：从 words.json 读取，支持随机抽题与「读到的词 → 词表标准词」校正。</summary>
public sealed class WordBook
{
    private readonly List<WordEntry> words = new();
    private readonly Dictionary<string, WordEntry> exactIndex = new(StringComparer.Ordinal);
    private readonly Random random = new();

    public string Source { get; private set; } = string.Empty;

    public IReadOnlyList<WordEntry> Words => words;

    public int Count => words.Count;

    /// <summary>按词长筛选后的条目数。</summary>
    public int FilteredCount(int minLength, int maxLength) =>
        words.Count(w => w.Length >= minLength && w.Length <= maxLength);

    /// <summary>从文本加载；解析失败抛出异常（由调用方显示错误）。</summary>
    public void Load(string json)
    {
        var file = JsonSerializer.Deserialize<WordBookFile>(json)
                   ?? throw new InvalidDataException("words.json 解析结果为空");

        if (file.Words.Count == 0)
            throw new InvalidDataException("words.json 里没有词条");

        words.Clear();
        exactIndex.Clear();
        foreach (var entry in file.Words)
        {
            if (string.IsNullOrWhiteSpace(entry.Word) || string.IsNullOrWhiteSpace(entry.Pinyin))
                continue;

            words.Add(entry);
            exactIndex[entry.Word] = entry;
        }

        Source = file.Source;
    }

    /// <summary>从文件加载。</summary>
    public void LoadFile(string path) => Load(File.ReadAllText(path));

    /// <summary>精确查找。</summary>
    public WordEntry? FindExact(string word) =>
        exactIndex.TryGetValue(word.Trim(), out var entry) ? entry : null;

    /// <summary>按词长筛选后随机抽一条；没有符合条件的返回 null。</summary>
    public WordEntry? Pick(int minLength, int maxLength, WordEntry? avoid = null)
    {
        var candidates = words.Where(w => w.Length >= minLength && w.Length <= maxLength).ToList();
        if (candidates.Count == 0)
            return null;

        if (candidates.Count > 1 && avoid is not null)
            candidates.RemoveAll(w => ReferenceEquals(w, avoid));

        return candidates.Count == 0 ? null : candidates[random.Next(candidates.Count)];
    }

    /// <summary>
    /// 把从游戏 UI 读到的文本校正成词表里的标准词：
    /// 1) 清理成只有汉字/字母；2) 精确匹配；3) 包含匹配（最长的词条优先）；4) 编辑距离 ≤ maxDistance。
    /// 都不匹配时返回清理后的原文。
    /// </summary>
    public string ResolveWord(string observed, int maxDistance = 2)
    {
        var cleaned = CleanText(observed);
        if (cleaned.Length == 0)
            return string.Empty;

        if (exactIndex.ContainsKey(cleaned))
            return cleaned;

        // 包含匹配：优先选最长的词条（防止 "克劳德" 被更短的词条截走）
        WordEntry? contained = null;
        foreach (var entry in words)
        {
            if (entry.Word.Length < 2)
                continue;

            if (cleaned.Contains(entry.Word, StringComparison.Ordinal) &&
                (contained is null || entry.Word.Length > contained.Word.Length))
                contained = entry;
        }

        if (contained is not null)
            return contained.Word;

        // 编辑距离兜底
        WordEntry? best = null;
        var bestDistance = maxDistance + 1;
        foreach (var entry in words)
        {
            if (Math.Abs(entry.Word.Length - cleaned.Length) > maxDistance)
                continue;

            var distance = Levenshtein(entry.Word, cleaned);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = entry;
            }
        }

        return bestDistance <= maxDistance && best is not null ? best.Word : cleaned;
    }

    /// <summary>取词的拼音（无空格连写）。</summary>
    public string? GetPinyin(string word) =>
        exactIndex.TryGetValue(word.Trim(), out var entry) ? entry.Pinyin : null;

    /// <summary>只保留汉字和字母数字，去掉空白、标点、控制符。</summary>
    public static string CleanText(string text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        var builder = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch))
                continue;

            if (char.IsLetterOrDigit(ch) || ch >= 0x4E00 && ch <= 0x9FFF || ch >= 0x3400 && ch <= 0x4DBF)
                builder.Append(ch);
        }

        return builder.ToString();
    }

    private static int Levenshtein(string a, string b)
    {
        if (a == b)
            return 0;
        if (a.Length == 0)
            return b.Length;
        if (b.Length == 0)
            return a.Length;

        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];

        for (var j = 0; j <= b.Length; j++)
            previous[j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }
}
