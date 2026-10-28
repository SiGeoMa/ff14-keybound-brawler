using System;
using System.IO;
using System.Linq;
using KeyboundBrawler.Core;

// 词库逻辑自检（不依赖 Dalamud）。
var jsonPath = args.Length > 0 ? args[0] : Path.Combine("..", "..", "..", "KeyboundBrawler", "data", "words.json");
var book = new WordBook();
book.LoadFile(jsonPath);
Console.WriteLine($"词库: {book.Count} 条, source={book.Source}");

if (book.Count != 1210)
    throw new Exception($"期望 1210 条，实际 {book.Count}");

var bad = book.Words.Where(w => w.Word.Length == 0 || w.Pinyin.Length == 0).ToList();
if (bad.Count > 0)
    throw new Exception($"有 {bad.Count} 条空词/空拼音");

var filtered = book.FilteredCount(2, 8);
Console.WriteLine($"筛选 2~8 字: {filtered} 条");
if (filtered < 1000)
    throw new Exception($"筛选结果异常: {filtered}");

var pick = book.Pick(2, 8) ?? throw new Exception("抽不到词");
Console.WriteLine($"随机抽词: {pick.Word} / {pick.Pinyin}");

// 词表校正
var cases = new (string Input, string Expected)[]
{
    ("  克劳德  ", "克劳德"),
    ("100克劳德", "克劳德"),
    ("克劳得", "克劳德"),
    ("克劳德", "克劳德"),
};

foreach (var (input, expected) in cases)
{
    var resolved = book.ResolveWord(input);
    if (resolved != expected)
        throw new Exception($"校正失败: [{input}] → [{resolved}]，期望 [{expected}]");
    Console.WriteLine($"校正 OK: [{input}] → [{resolved}]");
}

var pinyin = book.GetPinyin("克劳德");
if (pinyin != "kelaode")
    throw new Exception($"拼音映射失败: [{pinyin}]");
Console.WriteLine($"拼音映射 OK: 克劳德 → {pinyin}");

Console.WriteLine("ALL CHECKS PASSED");
