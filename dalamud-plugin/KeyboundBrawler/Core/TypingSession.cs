using System;
using System.Diagnostics;
using System.Linq;

namespace KeyboundBrawler.Core;

/// <summary>输入模式。</summary>
public enum TypingMode
{
    /// <summary>打拼音：目标是无调拼音串（如 abozu），不依赖中文输入法。</summary>
    Pinyin,

    /// <summary>打汉字：目标是词本身，需要系统中文输入法。</summary>
    Chinese,
}

/// <summary>一次提交的判定结果。</summary>
public readonly record struct SubmitOutcome(bool Correct, string Expected, string Actual, double WordSeconds);

/// <summary>单次练习会话：抽题、判定、计时与统计。不依赖 ImGui，方便以后加单元测试。</summary>
public sealed class TypingSession
{
    private readonly WordBook book;
    private readonly Stopwatch stopwatch = new();

    private WordEntry? current;
    private double wordStartedAt;
    private int sessionChars;

    public TypingSession(WordBook book)
    {
        this.book = book;
    }

    public TypingMode Mode { get; set; } = TypingMode.Pinyin;

    public WordEntry? Current => current;

    public bool IsRunning => stopwatch.IsRunning;

    /// <summary>已经出现的题数（含未提交的当前题）。</summary>
    public int TotalWords { get; private set; }

    public int CorrectWords { get; private set; }

    public int WrongWords { get; private set; }

    public int Combo { get; private set; }

    public int MaxCombo { get; private set; }

    /// <summary>最近一次的判定结果，UI 用来显示对错。</summary>
    public SubmitOutcome? LastOutcome { get; private set; }

    public double ElapsedSeconds => stopwatch.Elapsed.TotalSeconds;

    /// <summary>每分钟正确字符数（拼音按字母、中文按汉字）。</summary>
    public double Cpm
    {
        get
        {
            var minutes = stopwatch.Elapsed.TotalMinutes;
            return minutes < 1e-6 ? 0 : sessionChars / minutes;
        }
    }

    /// <summary>正确率（按题）。</summary>
    public double Accuracy => TotalWords == 0 ? 0 : CorrectWords * 100.0 / TotalWords;

    /// <summary>当前目标串（按模式取拼音或汉字）。</summary>
    public string ExpectedText => current is null
        ? string.Empty
        : Mode == TypingMode.Pinyin ? current.Pinyin : current.Word;

    /// <summary>开始一次新会话。</summary>
    public void Reset()
    {
        stopwatch.Reset();
        stopwatch.Start();
        TotalWords = 0;
        CorrectWords = 0;
        WrongWords = 0;
        Combo = 0;
        MaxCombo = 0;
        sessionChars = 0;
        LastOutcome = null;
        current = null;
    }

    /// <summary>结束当前会话（停止计时，保留统计）。</summary>
    public void Stop() => stopwatch.Stop();

    /// <summary>出下一题。</summary>
    public WordEntry? NextWord(int minLength, int maxLength)
    {
        current = book.Pick(minLength, maxLength, current);
        wordStartedAt = stopwatch.Elapsed.TotalSeconds;
        return current;
    }

    /// <summary>判定一次输入；统计在这里更新，调用方决定是否出下一题。</summary>
    public SubmitOutcome Submit(string input)
    {
        var expected = ExpectedText;
        var actual = Normalize(input);
        var correct = actual.Length > 0 && actual == Normalize(expected);

        TotalWords++;
        if (correct)
        {
            CorrectWords++;
            Combo++;
            MaxCombo = Math.Max(MaxCombo, Combo);
            sessionChars += expected.Length;
        }
        else
        {
            WrongWords++;
            Combo = 0;
        }

        var outcome = new SubmitOutcome(correct, expected, input, stopwatch.Elapsed.TotalSeconds - wordStartedAt);
        LastOutcome = outcome;
        return outcome;
    }

    /// <summary>实时进度：对目标串逐字符给出 0=未输入 1=正确 2=错误。</summary>
    public int[] GetCharStates(string input)
    {
        var target = ExpectedText;
        var actual = Normalize(input);
        var states = new int[target.Length];
        for (var i = 0; i < target.Length; i++)
        {
            if (i >= actual.Length)
                states[i] = 0;
            else
                states[i] = char.ToLowerInvariant(target[i]) == actual[i] ? 1 : 2;
        }

        return states;
    }

    /// <summary>规范化输入：去空白、转小写（含全角空格）。</summary>
    public static string Normalize(string text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        return new string(text
            .Where(ch => !char.IsWhiteSpace(ch) && ch != '\u3000')
            .Select(char.ToLowerInvariant)
            .ToArray());
    }
}
