using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Dalamud.Game.Chat;
using Dalamud.Plugin.Services;

namespace KeyboundBrawler.Core;

/// <summary>
/// 游戏聊天日志采集器：订阅 IChatGui 的聊天消息，按行写入 logs\chat-yyyyMMdd.log，
/// 并在内存里保留最近若干行给 UI 显示。
///
/// 后续开发钩子：订阅 <see cref="LineCaptured"/> 即可拿到实时文本，做「魔光键影」题目 / 得分 / 结算解析。
/// </summary>
public sealed class ChatLogRecorder : IDisposable
{
    private readonly IChatGui chatGui;
    private readonly IPluginLog pluginLog;
    private readonly string directory;
    private readonly ConcurrentQueue<string> pending = new();
    private readonly Queue<string> recent = new();
    private readonly object recentLock = new();
    private bool disposed;

    public ChatLogRecorder(IChatGui chatGui, IPluginLog pluginLog, string directory)
    {
        this.chatGui = chatGui;
        this.pluginLog = pluginLog;
        this.directory = directory;

        Directory.CreateDirectory(directory);
        chatGui.ChatMessage += OnChatMessage;
    }

    /// <summary>是否记录（关掉后事件仍在，但内容直接丢弃）。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>关键词过滤，逗号分隔；空表示全部记录。命中任意关键词才记录。</summary>
    public string KeywordFilter { get; set; } = string.Empty;

    /// <summary>内存里保留的最近行数上限。</summary>
    public int RecentLimit { get; set; } = 300;

    public long CapturedLines { get; private set; }

    public string? LastError { get; private set; }

    public string LogDirectory => directory;

    /// <summary>每采集到一行时触发（参数已带时间戳前缀）。后续解析器接这里。</summary>
    public event Action<string>? LineCaptured;

    private void OnChatMessage(IChatMessage message)
    {
        if (!Enabled || disposed)
            return;

        try
        {
            var text = message.Message?.TextValue ?? string.Empty;
            if (text.Length == 0)
                return;

            var sender = message.Sender?.TextValue ?? string.Empty;
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{(int)message.LogKind:00}] [{message.LogKind}] [{sender}] {text}";

            if (!MatchesFilter(line))
                return;

            pending.Enqueue(line);
        }
        catch (Exception ex)
        {
            LastError = $"聊天消息处理失败：{ex.Message}";
        }
    }

    private bool MatchesFilter(string line)
    {
        if (string.IsNullOrWhiteSpace(KeywordFilter))
            return true;

        var keywords = KeywordFilter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return keywords.Any(k => line.Contains(k, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>由 Framework.Update 定期调用：把缓冲写入文件、更新最近行。</summary>
    public void Flush()
    {
        if (pending.IsEmpty)
            return;

        try
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"chat-{DateTime.Now:yyyyMMdd}.log");
            var builder = new StringBuilder();
            while (pending.TryDequeue(out var line))
            {
                builder.AppendLine(line);
                CapturedLines++;

                lock (recentLock)
                {
                    recent.Enqueue(line);
                    while (recent.Count > Math.Max(20, RecentLimit))
                        recent.Dequeue();
                }

                try
                {
                    LineCaptured?.Invoke(line);
                }
                catch (Exception ex)
                {
                    LastError = $"解析钩子异常：{ex.Message}";
                }
            }

            File.AppendAllText(path, builder.ToString(), new UTF8Encoding(false));
            LastError = null;
        }
        catch (Exception ex)
        {
            LastError = $"写聊天日志失败：{ex.Message}";
            pluginLog.Warning(ex, "写聊天日志失败");
        }
    }

    /// <summary>取最近的日志行（副本，线程安全）。</summary>
    public IReadOnlyList<string> GetRecent()
    {
        lock (recentLock)
        {
            return recent.ToArray();
        }
    }

    public void ClearRecent()
    {
        lock (recentLock)
        {
            recent.Clear();
        }
    }

    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;
        chatGui.ChatMessage -= OnChatMessage;
        Flush();
    }
}
