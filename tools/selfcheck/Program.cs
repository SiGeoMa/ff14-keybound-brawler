using System;
using System.IO;
using System.Linq;
using KeyboundBrawler.Core;

// 不依赖 Dalamud 的核心逻辑自检：词库解析 + 抽题 + 判定 + 统计。
var jsonPath = args.Length > 0 ? args[0] : Path.Combine("..", "..", "..", "KeyboundBrawler", "data", "words.json");
var book = new WordBook();
book.LoadFile(jsonPath);
Console.WriteLine($"词库: {book.Count} 条, source={book.Source}");
if (book.Count != 1210)
    throw new Exception($"期望 1210 条，实际 {book.Count}");

var bad = book.Words.Where(w => w.Word.Length == 0 || w.Pinyin.Length == 0).ToList();
if (bad.Count > 0)
    throw new Exception($"有 {bad.Count} 条空词/空拼音");

Console.WriteLine($"筛选 2~8 字: {book.FilteredCount(2, 8)} 条");
var pick = book.Pick(2, 8) ?? throw new Exception("抽不到词");
Console.WriteLine($"随机抽题: {pick.Word} / {pick.Pinyin} / freq={pick.Frequency}");

var session = new TypingSession(book) { Mode = TypingMode.Pinyin };
session.Reset();
session.NextWord(2, 8);
var expected = session.ExpectedText;
Console.WriteLine($"当前题: {session.Current!.Word} -> {expected}");

// 正确提交
var ok = session.Submit(expected);
if (!ok.Correct)
    throw new Exception($"正确提交被判定为错: {ok}");
Console.WriteLine($"正确提交: {ok.Correct}, 用时 {ok.WordSeconds:0.000}s, 连击 {session.Combo}");

// 错误提交
var bad2 = session.Submit("zzzz");
if (bad2.Correct)
    throw new Exception("错误提交被判定为对");
if (session.Combo != 0)
    throw new Exception("错误后连击应清零");
Console.WriteLine($"错误提交: {bad2.Correct}, 连击 {session.Combo}, 正确率 {session.Accuracy:0.0}%");

// 空格/大小写归一化
session.NextWord(2, 8);
var py = session.ExpectedText;
var spaced = " " + string.Join(" ", py.Select(c => c)) + " ";
var spacedOutcome = session.Submit(spaced);
if (!spacedOutcome.Correct)
    throw new Exception($"带空格/大写输入应通过: [{spaced}] vs [{py}]");
Console.WriteLine($"归一化检查通过: [{spaced}] -> {spacedOutcome.Correct}");

// 中文模式
var cn = new TypingSession(book) { Mode = TypingMode.Chinese };
cn.Reset();
cn.NextWord(2, 8);
var cnOutcome = cn.Submit(cn.Current!.Word);
if (!cnOutcome.Correct)
    throw new Exception("中文模式正确提交失败");
Console.WriteLine($"中文模式通过: {cn.Current.Word}");

// 逐字状态
var states = cn.GetCharStates(cn.Current.Word.Substring(0, 1));
Console.WriteLine($"逐字状态: [{string.Join(",", states)}]");

// CPM 应为正
Console.WriteLine($"统计: 题数 {session.TotalWords}, 正确 {session.CorrectWords}, 错误 {session.WrongWords}, CPM {session.Cpm:0.0}");
if (session.Cpm <= 0)
    throw new Exception("CPM 计算异常");

// 关键字规则解析
var defaultRules = KeywordRuleParser.Parse(string.Empty);
if (defaultRules.Count == 0)
    throw new Exception("默认关键字规则为空");
Console.WriteLine($"默认关键字规则: {defaultRules.Count} 条");

var customRules = KeywordRuleParser.Parse("魔光键影|开始\n波次 -> 波次事件\n# 注释行\n通关\n得分=得分");
if (customRules.Count != 4)
    throw new Exception($"自定义规则解析数量不对: {customRules.Count}");
var ruleMap = customRules.ToDictionary(r => r.Keyword, r => r.EventName);
if (ruleMap["魔光键影"] != "开始" || ruleMap["波次"] != "波次事件" || ruleMap["得分"] != "得分" || ruleMap["通关"] != "事件")
    throw new Exception("自定义规则解析结果不对: " + string.Join("；", customRules.Select(r => $"{r.Keyword}->{r.EventName}")));
Console.WriteLine("关键字规则解析: " + string.Join("；", customRules.Select(r => $"{r.Keyword}->{r.EventName}")));

var roundTrip = KeywordRuleParser.Parse(KeywordRuleParser.ToText(customRules));
if (roundTrip.Count != customRules.Count)
    throw new Exception("规则文本往返不一致");
Console.WriteLine("关键字规则往返一致");

// 词表校正（游戏 UI 读到的文本 → 词表标准词）
var resolvedExact = book.ResolveWord("  克劳德  ");
if (resolvedExact != "克劳德")
    throw new Exception($"精确校正失败: [{resolvedExact}]");

var resolvedNoisy = book.ResolveWord("100克劳德");
if (resolvedNoisy != "克劳德")
    throw new Exception($"去噪校正失败: [{resolvedNoisy}]");

var resolvedTypo = book.ResolveWord("克劳得");
if (resolvedTypo != "克劳德")
    throw new Exception($"编辑距离校正失败: [{resolvedTypo}]");

var resolvedUnknown = book.ResolveWord("完全不存在的词组啊");
if (string.IsNullOrEmpty(resolvedUnknown))
    throw new Exception("未知词应返回清理后的原文");

var pinyin = book.GetPinyin("克劳德");
if (pinyin != "kelaode")
    throw new Exception($"拼音映射失败: [{pinyin}]");

Console.WriteLine($"词表校正: 精确/去噪/纠错/未知 全部通过；克劳德拼音 {pinyin}");

Console.WriteLine("ALL CHECKS PASSED");
