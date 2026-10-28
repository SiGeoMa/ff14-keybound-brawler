using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Dalamud.Plugin.Services;

namespace KeyboundBrawler.Core;

/// <summary>
/// 跟随读取 Dalamud 自己的日志文件（<c>%APPDATA%\XIVLauncherCN\dalamud.log</c>），
/// 只取新增内容，过滤后追加到插件日志目录，方便后续分析「魔光键影」在游戏侧的行为。
///
/// 说明：dalamud.log 是 Dalamud 本体 / 各插件的日志，不等于游戏聊天；游戏聊天由
/// <see cref="ChatLogRecorder"/> 采集，两者互相补充。
/// </summary>
public sealed class DalamudLogTailer : IDisposable
{
    private const int InitialHistoryBytes = 256 * 1024;

    private readonly IPluginLog pluginLog;
    private readonly string outputDirectory;
    private readonly List<byte> partial = new();
    private readonly Queue<string> recent = new();
    private readonly object recentLock = new();

    private FileStream? stream;
    private DateTime lastPoll = DateTime.MinValue;
    private bool disposed;

    public DalamudLogTailer(IPluginLog pluginLog, string outputDirectory)
    {
        this.pluginLog = pluginLog;
        this.outputDirectory = outputDirectory;
        Directory.CreateDirectory(outputDirectory);
    }

    public bool Enabled { get; set; } = true;

    /// <summary>用户配置的路径；空 = 自动探测。</summary>
    public string ConfiguredPath { get; set; } = string.Empty;

    /// <summary>关键词过滤，逗号分隔；空表示全部记录。</summary>
    public string KeywordFilter { get; set; } = string.Empty;

    public int RecentLimit { get; set; } = 300;

    public string? ResolvedPath { get; private set; }

    public bool IsAttached => stream is not null;

    public long CapturedLines { get; private set; }

    public string? LastError { get; private set; }

    public long FilePosition => stream?.Position ?? 0;

    public string LogDirectory => outputDirectory;

    /// <summary>每读到一行新增日志时触发（后续解析 / 关键字引擎用）。</summary>
    public event Action<string>? LineCaptured;

    /// <summary>自动探测 dalamud.log 路径（国服优先）。</summary>
    public static string? DetectPath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var candidates = new[]
        {
            Path.Combine(appData, "XIVLauncherCN", "dalamud.log"),
            Path.Combine(appData, "XIVLauncher", "dalamud.log"),
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>按配置 / 自动探测挂上日志文件；返回是否成功。</summary>
    public bool Attach()
    {
        Detach();

        var path = string.IsNullOrWhiteSpace(ConfiguredPath) ? DetectPath() : ConfiguredPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            ResolvedPath = path;
            LastError = "找不到 dalamud.log（可手动指定路径）";
            return false;
        }

        try
        {
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            ResolvedPath = path;
            partial.Clear();

            // 首次挂载：把文件末尾一段历史读出来显示（不重复写入导出日志）
            var length = stream.Length;
            var start = Math.Max(0, length - InitialHistoryBytes);
            stream.Position = start;
            var buffer = new byte[length - start];
            var read = stream.Read(buffer, 0, buffer.Length);
            var text = Encoding.UTF8.GetString(buffer, 0, read);
            var lines = text.Split('\n');
            // 丢弃可能被截断的第一行（从文件中间开始读时）
            for (var i = start > 0 ? 1 : 0; i < lines.Length; i++)
            {
                var line = lines[i].TrimEnd('\r');
                if (line.Length == 0 || !MatchesFilter(line))
                    continue;
                AddRecent(line);
            }

            stream.Position = stream.Length;
            LastError = null;
            pluginLog.Information("已挂载 dalamud.log: {Path}", path);
            return true;
        }
        catch (Exception ex)
        {
            Detach();
            LastError = $"打开 dalamud.log 失败：{ex.Message}";
            pluginLog.Warning(ex, "打开 dalamud.log 失败");
            return false;
        }
    }

    /// <summary>由 Framework.Update 定期调用（内部节流为 ~1 秒一次）。</summary>
    public void Poll(bool force = false)
    {
        if (!Enabled || disposed || stream is null)
            return;

        var now = DateTime.UtcNow;
        if (!force && (now - lastPoll).TotalMilliseconds < 900)
            return;
        lastPoll = now;

        try
        {
            // 文件被轮转 / 截断：重新挂载
            if (stream.Length < stream.Position)
            {
                pluginLog.Information("dalamud.log 已轮转，重新挂载");
                Attach();
                return;
            }

            if (stream.Length == stream.Position)
                return;

            var available = stream.Length - stream.Position;
            // 单次读取上限，避免一次读太多卡帧
            var readSize = (int)Math.Min(available, 4 * 1024 * 1024);
            var buffer = new byte[readSize];
            var read = stream.Read(buffer, 0, readSize);
            if (read <= 0)
                return;

            partial.AddRange(buffer.AsSpan(0, read).ToArray());

            // 找出最后一个换行，整行解出，剩余半行留到下次
            var lastNewline = partial.LastIndexOf((byte)'\n');
            if (lastNewline < 0)
                return;

            var complete = Encoding.UTF8.GetString(partial.GetRange(0, lastNewline + 1).ToArray());
            partial.RemoveRange(0, lastNewline + 1);

            var newLines = new List<string>();
            foreach (var raw in complete.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                if (line.Length == 0 || !MatchesFilter(line))
                    continue;
                newLines.Add(line);
                AddRecent(line);
                CapturedLines++;

                try
                {
                    LineCaptured?.Invoke(line);
                }
                catch (Exception ex)
                {
                    LastError = $"日志解析钩子异常：{ex.Message}";
                }
            }

            if (newLines.Count > 0)
            {
                var path = Path.Combine(outputDirectory, $"dalamud-{DateTime.Now:yyyyMMdd}.log");
                File.AppendAllText(path, string.Join(Environment.NewLine, newLines) + Environment.NewLine, new UTF8Encoding(false));
            }

            LastError = null;
        }
        catch (Exception ex)
        {
            LastError = $"读取 dalamud.log 失败：{ex.Message}";
            pluginLog.Warning(ex, "读取 dalamud.log 失败");
        }
    }

    private bool MatchesFilter(string line)
    {
        if (string.IsNullOrWhiteSpace(KeywordFilter))
            return true;

        var keywords = KeywordFilter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return keywords.Any(k => line.Contains(k, StringComparison.OrdinalIgnoreCase));
    }

    private void AddRecent(string line)
    {
        lock (recentLock)
        {
            recent.Enqueue(line);
            while (recent.Count > Math.Max(20, RecentLimit))
                recent.Dequeue();
        }
    }

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

    public void Detach()
    {
        stream?.Dispose();
        stream = null;
    }

    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;
        Detach();
    }
}
