using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using KeyboundBrawler.Core;
using KeyboundBrawler.UI;
using Lumina.Excel.Sheets;

namespace KeyboundBrawler;

/// <summary>
/// 魔光键影自动打字插件。
/// 核心链路：读 KTGEWord 词条（含 EntityID）→ 按距离选最近的 → 逐字写入 KTGInput → 调游戏回调提交。
/// 附带：防 AFK 心跳、确认对话框防护、结算/失焦保护、进图自动弹窗。
/// </summary>
public sealed class Plugin : IDalamudPlugin
{
    public const string CommandName = "/kbb";

    public const string WordsFileName = "words.json";

    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    private readonly WindowSystem windowSystem = new("KeyboundBrawler");
    private readonly MainWindow mainWindow;
    private readonly SemaphoreSlim inputLock = new(1, 1);
    private readonly Dictionary<string, int> wordAttempts = new(StringComparer.Ordinal);
    private readonly object submitGate = new();

    private DateTime lastAutoAt = DateTime.MinValue;
    private DateTime nextAutoAllowedAt = DateTime.MinValue;
    private DateTime lastAfkWiggleAt = DateTime.MinValue;
    private DateTime lastAutoOpenAt = DateTime.MinValue;
    private uint lastTerritoryId = uint.MaxValue;

    // 逐字写入状态
    private bool directTyping;
    private string pendingDirectText = string.Empty;
    private int pendingDirectIndex;
    private DateTime nextDirectCharAt;

    // 待提交
    private bool pendingSubmit;
    private string pendingSubmitText = string.Empty;
    private DateTime pendingSubmitAt;

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as PluginConfiguration ?? new PluginConfiguration();
        MigrateConfig();

        var assemblyDirectory = PluginInterface.AssemblyLocation.Directory?.FullName ?? AppContext.BaseDirectory;
        BundledDataDirectory = Path.Combine(assemblyDirectory, "data");
        UserDataDirectory = Path.Combine(PluginInterface.GetPluginConfigDirectory(), "data");
        LogDirectory = Path.Combine(PluginInterface.GetPluginConfigDirectory(), "logs");

        Book = new WordBook();
        EnsureWordsFile();
        LoadWords();

        Ktg = new KtgMonitor(GameGui, ClientState, Log);
        Ktg.StateChanged += OnKtgStateChanged;

        AutoTyper = new AutoTyper(Log);
        ApplyAutoTyperSettings();

        mainWindow = new MainWindow(this);
        windowSystem.AddWindow(mainWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "魔光键影自动打字：不带参数打开窗口；auto on/off / ktg",
        });

        PluginInterface.UiBuilder.Draw += Draw;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;
        Framework.Update += OnFrameworkUpdate;

        if (Configuration.ShowWindowOnLogin)
            mainWindow.IsOpen = true;

        Log.Information("KeyboundBrawler 已加载：词库 {Count} 条，自动 {Auto}", Book.Count, Configuration.AutoPlay ? "开" : "关");
    }

    public PluginConfiguration Configuration { get; }

    public WordBook Book { get; }

    public KtgMonitor Ktg { get; }

    public AutoTyper AutoTyper { get; }

    public string BundledDataDirectory { get; }

    public string UserDataDirectory { get; }

    public string LogDirectory { get; }

    public string? DataError { get; private set; }

    public string LastAutoMessage { get; private set; } = string.Empty;

    public void Dispose()
    {
        Framework.Update -= OnFrameworkUpdate;
        PluginInterface.UiBuilder.Draw -= Draw;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;
        CommandManager.RemoveHandler(CommandName);
        windowSystem.RemoveAllWindows();

        Ktg.StateChanged -= OnKtgStateChanged;
        inputLock.Dispose();
    }

    private void Draw() => windowSystem.Draw();

    private void OnFrameworkUpdate(IFramework framework)
    {
        Ktg.Poll();
        UpdateAntiAfk();
        ProcessDirectTyping();
        ProcessPendingSubmit();
        CheckTerritoryAutoOpen();
        UpdateAutoLoop();
    }

    #region 自动流程

    private void OnKtgStateChanged(KtgState state)
    {
        if (state.GameOver)
            wordAttempts.Clear();

        TryAutoType(state);
    }

    private DateTime lastAutoTick = DateTime.MinValue;

    /// <summary>定时兜底 + 状态变化触发。</summary>
    private void UpdateAutoLoop()
    {
        if ((DateTime.Now - lastAutoTick).TotalMilliseconds < 800)
            return;

        lastAutoTick = DateTime.Now;
        TryAutoType(Ktg.State);
    }

    private void TryAutoType(KtgState state)
    {
        if (!Configuration.AutoPlay || !state.InGame || state.GameOver)
            return;

        // 只在魔光键影战斗区域（1359）里输入
        if (state.TerritoryId != KtgMonitor.ArenaTerritory)
            return;

        if (!AutoTyper.IsGameForeground())
        {
            if ((DateTime.Now - lastAfkWiggleAt).TotalSeconds > 8)
            {
                lastAfkWiggleAt = DateTime.Now;
                if (Configuration.AutoRefocusGame)
                    AutoTyper.TryFocusGame();
            }

            return;
        }

        if (IsBlockingDialogVisible())
            return;

        if (!Ktg.IsAddonVisible("KTGInput"))
            return;

        // 数据太旧不输入
        if ((DateTime.Now - Ktg.StateUpdatedAt).TotalSeconds > 1.5)
            return;

        var candidates = ExtractCandidates(state.Word);
        if (candidates.Count == 0)
            return;

        // 清理已消失的词
        foreach (var key in wordAttempts.Keys.Where(k => !candidates.Contains(k)).ToList())
            wordAttempts.Remove(key);

        // 集火：优先打最近的（列表第一个），打 3 次还在就换下一个
        var target = candidates.FirstOrDefault(c => wordAttempts.GetValueOrDefault(c, 0) < 3);
        if (target is null)
            return;

        if (DateTime.Now < nextAutoAllowedAt)
            return;

        var minIntervalMs = Configuration.AutoBlatantMode ? 120 : 800;
        var maxIntervalMs = Configuration.AutoBlatantMode ? 260 : 1300;
        nextAutoAllowedAt = DateTime.Now.AddMilliseconds(Random.Shared.Next(minIntervalMs, maxIntervalMs));
        lastAutoAt = DateTime.Now;
        wordAttempts[target] = wordAttempts.GetValueOrDefault(target, 0) + 1;

        var text = target;

        _ = Task.Run(() =>
        {
            if (!inputLock.Wait(3000))
                return;

            try
            {
                var reaction = AutoTyper.NextStartDelay();
                if (reaction > 0)
                    Thread.Sleep(reaction);

                var current = Ktg.State;
                if (current.GameOver || !current.InGame)
                {
                    WriteAutoLog($"[跳过] 输入前游戏已结束");
                    return;
                }

                if (!ExtractCandidates(current.Word).Contains(target))
                {
                    WriteAutoLog($"[跳过] 反应期间目标已消失：{target}");
                    return;
                }

                lock (submitGate)
                {
                    directTyping = true;
                    pendingDirectText = text;
                    pendingDirectIndex = 0;
                    nextDirectCharAt = DateTime.Now.AddMilliseconds(Random.Shared.Next(80, 200));
                }

                WriteAutoLog($"[打词] {target}（反应 {reaction}ms）");
                LastAutoMessage = $"开始打「{target}」";
            }
            finally
            {
                inputLock.Release();
            }
        });
    }

    /// <summary>逐字写入：主线程分帧把词一个字一个字写进 KTGInput。</summary>
    private void ProcessDirectTyping()
    {
        string text;
        int index;
        DateTime nextAt;
        lock (submitGate)
        {
            if (!directTyping)
                return;
            text = pendingDirectText;
            index = pendingDirectIndex;
            nextAt = nextDirectCharAt;
        }

        if (string.IsNullOrEmpty(text))
        {
            lock (submitGate) { directTyping = false; }
            return;
        }

        var state = Ktg.State;
        if (state.GameOver || !state.InGame || !Ktg.IsAddonVisible("KTGInput") ||
            !AutoTyper.IsGameForeground() || IsBlockingDialogVisible())
        {
            lock (submitGate) { directTyping = false; pendingDirectText = string.Empty; }
            WriteAutoLog("[逐字] 状态不安全，取消");
            return;
        }

        if (DateTime.Now < nextAt)
            return;

        index++;
        if (index > text.Length)
            index = text.Length;

        if (!Ktg.TrySetInputText(text[..index], out var error))
        {
            lock (submitGate) { directTyping = false; pendingDirectText = string.Empty; }
            WriteAutoLog($"[逐字] 写入失败：{error}");
            return;
        }

        lock (submitGate) { pendingDirectIndex = index; }

        if (index >= text.Length)
        {
            lock (submitGate)
            {
                directTyping = false;
                pendingSubmit = true;
                pendingSubmitText = text;
                pendingSubmitAt = DateTime.Now;
            }

            return;
        }

        var delay = Configuration.AutoBlatantMode ? Random.Shared.Next(8, 25) : AutoTyper.NextCharDelay();
        nextDirectCharAt = DateTime.Now.AddMilliseconds(Math.Max(15, delay));
    }

    /// <summary>提交：确认输入框有内容且安全后调游戏回调。</summary>
    private void ProcessPendingSubmit()
    {
        bool pending;
        string text;
        DateTime at;
        lock (submitGate)
        {
            pending = pendingSubmit;
            text = pendingSubmitText;
            at = pendingSubmitAt;
        }

        if (!pending)
            return;

        try
        {
            if ((DateTime.Now - at).TotalMilliseconds > 3000)
            {
                lock (submitGate) { pendingSubmit = false; }
                WriteAutoLog($"[提交] 超时，放弃「{text}」");
                return;
            }

            var state = Ktg.State;
            if (state.GameOver || !state.InGame || !Ktg.IsAddonVisible("KTGInput"))
            {
                lock (submitGate) { pendingSubmit = false; }
                return;
            }

            if (!AutoTyper.IsGameForeground() || IsBlockingDialogVisible())
            {
                lock (submitGate) { pendingSubmit = false; }
                return;
            }

            // 提交前补一次键盘动作心跳
            AutoTyper.SendAfkWiggle();

            var ok = Ktg.TrySubmitInput(out var error);
            lock (submitGate) { pendingSubmit = false; }
            WriteAutoLog($"[提交] {(ok ? "OK" : "FAIL")} 「{text}」 {error}");
            LastAutoMessage = ok ? $"已提交「{text}」" : $"提交失败：{error}";
        }
        catch (Exception ex)
        {
            lock (submitGate) { pendingSubmit = false; }
            WriteAutoLog($"[提交] 异常：{ex.Message}");
        }
    }

    /// <summary>从面板文本拆出候选词并校正到词表，按配置排序。</summary>
    private List<string> ExtractCandidates(string wordList)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(wordList))
            return result;

        // 优先用精确词条（20 槽位 + EntityID），失败退回文本遍历
        var nodes = Ktg.WordNodes;
        if (nodes.Count > 0)
        {
            foreach (var node in OrderNodes(nodes))
            {
                var resolved = Book.ResolveWord(node.Text);
                if (resolved.Length < 2 || resolved.Length > 24)
                    continue;
                if (Book.FindExact(resolved) is null)
                    continue;
                if (!result.Contains(resolved, StringComparer.Ordinal))
                    result.Add(resolved);
            }

            return result;
        }

        foreach (var segment in wordList.Split(new[] { ' ', '\u3000' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var resolved = Book.ResolveWord(segment);
            if (resolved.Length < 2 || resolved.Length > 24)
                continue;
            if (Book.FindExact(resolved) is null)
                continue;
            if (!result.Contains(resolved, StringComparer.Ordinal))
                result.Add(resolved);
        }

        return result;
    }

    private List<WordNode> OrderNodes(IReadOnlyList<WordNode> nodes) => Configuration.AutoTargetOrder switch
    {
        "distance" => nodes.OrderBy(n => GetEntityDistance(n.EntityID)).ThenBy(n => n.Text.Length).ToList(),
        "screen_desc" => nodes.OrderByDescending(n => n.Y).ThenBy(n => n.X).ToList(),
        "screen_asc" => nodes.OrderBy(n => n.Y).ThenBy(n => n.X).ToList(),
        "shortest" => nodes.OrderBy(n => n.Text.Length).ToList(),
        "longest" => nodes.OrderByDescending(n => n.Text.Length).ToList(),
        _ => nodes.OrderBy(n => GetEntityDistance(n.EntityID)).ThenBy(n => n.Text.Length).ToList(),
    };

    private float GetEntityDistance(int entityId)
    {
        if (entityId <= 0)
            return float.MaxValue;

        try
        {
            var player = ObjectTable.LocalPlayer;
            if (player == null)
                return float.MaxValue;

            var id = (uint)entityId;
            foreach (var obj in ObjectTable)
            {
                if (obj.EntityId == id)
                    return System.Numerics.Vector3.Distance(player.Position, obj.Position);
            }
        }
        catch
        {
            // 忽略
        }

        return float.MaxValue;
    }

    #endregion

    #region 防 AFK / 安全

    private void UpdateAntiAfk()
    {
        if (!Configuration.AutoAntiAfk)
            return;

        if ((DateTime.Now - lastAfkWiggleAt).TotalSeconds < Math.Max(5, Configuration.AntiAfkIntervalSeconds))
            return;

        var state = Ktg.State;
        if (!state.InGame || state.GameOver)
            return;

        if (!AutoTyper.IsGameForeground() || IsBlockingDialogVisible())
            return;

        lastAfkWiggleAt = DateTime.Now;
        AutoTyper.SendAfkWiggle();
    }

    /// <summary>确认类对话框（按回车会确认默认选项，可能退本/退队）。</summary>
    private static readonly string[] BlockingAddons =
    {
        "SelectYesno", "SelectString", "SelectIconString", "SelectOk",
        "ContentsFinderConfirm", "ContentsFinder", "ContextIconMenu",
        "SystemMenu", "EscapeMenu", "CharacterMenu",
    };

    private bool IsBlockingDialogVisible()
    {
        foreach (var addon in BlockingAddons)
        {
            if (Ktg.IsAddonVisible(addon))
                return true;
        }

        return false;
    }

    private void CheckTerritoryAutoOpen()
    {
        var territoryId = ClientState.TerritoryType;
        if (territoryId == lastTerritoryId)
            return;

        lastTerritoryId = territoryId;
        if (territoryId == 0 || !ClientState.IsLoggedIn)
            return;

        if (!Configuration.AutoOpenOnTerritory)
            return;

        if ((DateTime.Now - lastAutoOpenAt).TotalSeconds < 15)
            return;

        var name = GetTerritoryName(territoryId);
        if (string.IsNullOrEmpty(name) || !MatchesKeywords(name, Configuration.TerritoryKeywords))
            return;

        lastAutoOpenAt = DateTime.Now;
        mainWindow.IsOpen = true;
    }

    private string GetTerritoryName(uint territoryId)
    {
        try
        {
            var sheet = DataManager.GetExcelSheet<TerritoryType>();
            if (sheet.TryGetRow(territoryId, out var row))
                return row.PlaceName.Value.Name.ToString();
        }
        catch (Exception ex)
        {
            Log.Debug("读取 TerritoryType 失败: {Message}", ex.Message);
        }

        return string.Empty;
    }

    private static bool MatchesKeywords(string text, string keywords)
    {
        if (string.IsNullOrWhiteSpace(keywords))
            return false;

        foreach (var keyword in keywords.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (text.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    #endregion

    #region 命令 / 配置

    private void OnCommand(string command, string args)
    {
        var parts = (args ?? string.Empty).Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var sub = parts.Length > 0 ? parts[0].ToLowerInvariant() : string.Empty;

        switch (sub)
        {
            case "":
                ToggleMainUi();
                break;

            case "auto":
                if (parts.Length > 1)
                {
                    var on = parts[1].ToLowerInvariant() is "on" or "1" or "true";
                    Configuration.AutoPlay = on;
                    SaveConfig();
                    ChatGui.Print($"[KeyboundBrawler] 自动打字：{(on ? "开" : "关")}");
                }
                else
                {
                    ChatGui.Print($"[KeyboundBrawler] 自动打字当前：{(Configuration.AutoPlay ? "开" : "关")}；用法 /kbb auto on|off");
                }

                break;

            case "ktg":
            {
                Ktg.Poll(true);
                var state = Ktg.State;
                ChatGui.Print($"[KeyboundBrawler] 区域 {state.TerritoryId}，游戏内 {state.InGame}，结束 {state.GameOver}；词 {Ktg.WordNodes.Count} 个；目标「{state.Todo}」 分数「{state.Score}」");
                ChatGui.Print($"[KeyboundBrawler] 词：{string.Join("  ", Ktg.WordNodes.Select(n => n.Text))}");
                break;
            }

            case "reload":
                LoadWords();
                ChatGui.Print($"[KeyboundBrawler] 词库已重载：{Book.Count} 条");
                break;

            default:
                ChatGui.Print("[KeyboundBrawler] 子命令：auto on|off / ktg / reload；不带参数打开窗口。");
                break;
        }
    }

    public void ApplyAutoTyperSettings()
    {
        AutoTyper.CharDelayMs = Configuration.AutoCharDelayMs;
        AutoTyper.Humanize = Configuration.AutoHumanize;
        AutoTyper.BlatantMode = Configuration.AutoBlatantMode;
        AutoTyper.StartDelayMinMs = Configuration.AutoStartDelayMinMs;
        AutoTyper.StartDelayMaxMs = Configuration.AutoStartDelayMaxMs;
    }

    public void SaveConfig()
    {
        try
        {
            Configuration.Save();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "保存配置失败");
        }
    }

    public void ToggleMainUi() => mainWindow.Toggle();

    public void OpenLogFolder()
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            Process.Start(new ProcessStartInfo { FileName = LogDirectory, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "打开日志目录失败");
        }
    }

    public void WriteAutoLog(string line)
    {
        try
        {
            var path = Path.Combine(LogDirectory, $"auto-{DateTime.Now:yyyyMMdd}.log");
            Directory.CreateDirectory(LogDirectory);
            File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss.fff} {line}{Environment.NewLine}", new System.Text.UTF8Encoding(false));
        }
        catch
        {
            // 日志失败不影响功能
        }
    }

    private void MigrateConfig()
    {
        if (Configuration.Version >= 9)
            return;

        Configuration.Version = 9;
        Configuration.AutoPlay = Configuration.AutoPlay;
        Configuration.AutoInputMode = "direct";
        Configuration.AutoTargetOrder = "distance";
        Configuration.AutoAntiAfk = true;
        Configuration.AutoRefocusGame = true;
        Configuration.AutoHumanize = true;
        Configuration.AutoCharDelayMs = Math.Max(90, Configuration.AutoCharDelayMs);
        Configuration.AutoStartDelayMinMs = Math.Max(500, Configuration.AutoStartDelayMinMs);
        Configuration.AutoStartDelayMaxMs = Math.Max(1200, Configuration.AutoStartDelayMaxMs);

        try
        {
            Configuration.Save();
        }
        catch
        {
            // 忽略
        }
    }

    #endregion

    #region 词库

    private void EnsureWordsFile()
    {
        try
        {
            Directory.CreateDirectory(UserDataDirectory);
            var target = Path.Combine(UserDataDirectory, WordsFileName);
            if (File.Exists(target))
                return;

            var source = Path.Combine(BundledDataDirectory, WordsFileName);
            if (File.Exists(source))
                File.Copy(source, target, overwrite: false);
        }
        catch (Exception ex)
        {
            DataError = $"初始化词库失败：{ex.Message}";
        }
    }

    public void LoadWords()
    {
        try
        {
            var userPath = Path.Combine(UserDataDirectory, WordsFileName);
            var bundledPath = Path.Combine(BundledDataDirectory, WordsFileName);
            var path = File.Exists(userPath) ? userPath : bundledPath;
            if (!File.Exists(path))
                return;

            Book.LoadFile(path);
            DataError = null;
        }
        catch (Exception ex)
        {
            DataError = $"词库加载失败：{ex.Message}";
            Log.Error(ex, "词库加载失败");
        }
    }

    #endregion
}
