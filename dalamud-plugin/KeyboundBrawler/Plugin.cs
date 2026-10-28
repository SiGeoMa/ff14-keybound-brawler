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

public sealed class Plugin : IDalamudPlugin
{
    public const string CommandName = "/kbb";

    /// <summary>内置词库文件名（拼音映射用，可选）。</summary>
    public const string WordsFileName = "words.json";

    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IAddonLifecycle AddonLifecycle { get; private set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    private readonly WindowSystem windowSystem = new("KeyboundBrawler");
    private readonly MainWindow mainWindow;
    private readonly SemaphoreSlim autoInputLock = new(1, 1);

    private uint lastTerritoryId = uint.MaxValue;
    private DateTime lastAutoOpenAt = DateTime.MinValue;
    private string lastObservedWord = string.Empty;
    private DateTime lastAutoAt = DateTime.MinValue;
    private DateTime lastAutoTick = DateTime.MinValue;
    private DateTime nextAutoAllowedAt = DateTime.MinValue;
    private readonly Dictionary<string, int> wordAttempts = new(StringComparer.Ordinal);
    private DateTime lastSafetyLogAt = DateTime.MinValue;
    private DateTime lastFocusLogAt = DateTime.MinValue;
    private DateTime lastNoWordLogAt = DateTime.MinValue;

    /// <summary>这些确认类对话框出现时，绝不能发按键（否则回车会确认默认选项，可能退本/退队）。</summary>
    private static readonly string[] BlockingAddons =
    {
        "SelectYesno", "SelectString", "SelectIconString", "SelectOk",
        "ContentsFinderConfirm", "ContentsFinder", "ContextIconMenu",
        "CharaCard", "GuildLeve", "JournalAccept",
        "SystemMenu", "EscapeMenu", "CharacterMenu",
    };

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as PluginConfiguration ?? new PluginConfiguration();

        // v1/v2 -> v3：旧配置迁移。v2 调快拟人化参数；v3 默认改用剪贴板粘贴（实测 Unicode 送不进游戏输入框）
        if (Configuration.Version < 3)
        {
            if (Configuration.Version < 2)
            {
                Configuration.AutoCharDelayMs = 50;
                Configuration.AutoStartDelayMinMs = 250;
                Configuration.AutoStartDelayMaxMs = 600;
                Configuration.AutoSubmitDelayMinMs = 80;
                Configuration.AutoSubmitDelayMaxMs = 250;
            }

            Configuration.AutoInputMode = "paste";
            Configuration.Version = 3;
            try
            {
                Configuration.Save();
            }
            catch
            {
                // 忽略：保存失败不影响运行
            }
        }

        // v4：选词顺序改为按屏幕位置（近→远），旧配置自动迁移
        if (Configuration.Version < 4)
        {
            Configuration.AutoTargetOrder = "screen_desc";
            Configuration.Version = 4;
            try
            {
                Configuration.Save();
            }
            catch
            {
                // 忽略
            }
        }

        // v5：逐字粘贴 + 延迟区间调慢（老配置自动迁移）
        if (Configuration.Version < 5)
        {
            Configuration.AutoInputMode = "paste_char";
            Configuration.AutoCharDelayMs = 90;
            Configuration.AutoJitterPercent = 40;
            Configuration.AutoStartDelayMinMs = 400;
            Configuration.AutoStartDelayMaxMs = 1000;
            Configuration.AutoSubmitDelayMinMs = 150;
            Configuration.AutoSubmitDelayMaxMs = 400;
            Configuration.Version = 5;
            try
            {
                Configuration.Save();
            }
            catch
            {
                // 忽略
            }
        }

        var assemblyDirectory = PluginInterface.AssemblyLocation.Directory?.FullName ?? AppContext.BaseDirectory;
        BundledDataDirectory = Path.Combine(assemblyDirectory, "data");

        var configDirectory = PluginInterface.GetPluginConfigDirectory();
        UserDataDirectory = Path.Combine(configDirectory, "data");
        LogDirectory = Path.Combine(configDirectory, "logs");

        Book = new WordBook();
        EnsureWordsFile();
        LoadWords();

        KeywordEngine = new KeywordEngine(Log, LogDirectory)
        {
            Enabled = Configuration.EnableKeywordEngine,
        };
        KeywordEngine.LoadRules(Configuration.KeywordRules);
        KeywordEngine.Matched += OnKeywordMatched;

        ChatLog = new ChatLogRecorder(ChatGui, Log, LogDirectory)
        {
            Enabled = Configuration.RecordChatLog,
            KeywordFilter = Configuration.LogKeywords,
            RecentLimit = Configuration.RecentLogLines,
        };
        ChatLog.LineCaptured += line => KeywordEngine.Evaluate("聊天", line);

        LogTailer = new DalamudLogTailer(Log, LogDirectory)
        {
            Enabled = Configuration.TailDalamudLog,
            ConfiguredPath = Configuration.DalamudLogPath,
            KeywordFilter = Configuration.LogKeywords,
            RecentLimit = Configuration.RecentLogLines,
        };
        LogTailer.LineCaptured += line => KeywordEngine.Evaluate("dalamud", line);

        if (Configuration.TailDalamudLog)
            LogTailer.Attach();

        AddonProbe = new AddonProbe(AddonLifecycle, GameGui, Log, LogDirectory)
        {
            Enabled = Configuration.EnableAddonProbe,
            AutoSnapshot = Configuration.EnableAutoSnapshot,
            SnapshotKeywords = Configuration.AddonKeywords,
        };
        AddonProbe.TextSnapshotted += OnAddonTextSnapshotted;
        AddonProbe.AddonShown += OnAddonShown;

        Ktg = new KtgMonitor(GameGui, Log);
        Ktg.StateChanged += OnKtgStateChanged;
        Ktg.DiagnosticDump = dump =>
        {
            try
            {
                var path = Path.Combine(LogDirectory, $"ktg-nodes-{DateTime.Now:yyyyMMdd}.log");
                File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss} ====={Environment.NewLine}{dump}{Environment.NewLine}",
                    new System.Text.UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "写 KTG 节点树日志失败");
            }
        };

        AutoTyper = new AutoTyper(Log)
        {
            CharDelayMs = Configuration.AutoCharDelayMs,
        };
        ApplyAutoTyperSettings();

        mainWindow = new MainWindow(this);
        windowSystem.AddWindow(mainWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "魔光键影自动打字：不带参数打开窗口；auto on/off / type <文本> / ktg / dump <面板> / log",
        });

        PluginInterface.UiBuilder.Draw += Draw;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;
        Framework.Update += OnFrameworkUpdate;

        if (Configuration.ShowWindowOnLogin)
            mainWindow.IsOpen = true;

        Log.Information("KeyboundBrawler v0.2 已加载：自动状态 {Auto}，输入方式 {Mode}，日志 {Dir}",
            Configuration.AutoPlay ? "开" : "关", Configuration.AutoInputMode, LogDirectory);
    }

    public PluginConfiguration Configuration { get; }

    public WordBook Book { get; }

    public ChatLogRecorder ChatLog { get; }

    public DalamudLogTailer LogTailer { get; }

    public AddonProbe AddonProbe { get; }

    public KeywordEngine KeywordEngine { get; }

    public KtgMonitor Ktg { get; }

    public AutoTyper AutoTyper { get; }

    public string BundledDataDirectory { get; }

    public string UserDataDirectory { get; }

    public string LogDirectory { get; }

    /// <summary>最近一次自动输入记录（窗口显示用）。</summary>
    public string LastAutoMessage { get; private set; } = string.Empty;

    public string? DataError { get; private set; }

    public void Dispose()
    {
        Framework.Update -= OnFrameworkUpdate;
        PluginInterface.UiBuilder.Draw -= Draw;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;
        CommandManager.RemoveHandler(CommandName);
        windowSystem.RemoveAllWindows();

        Ktg.StateChanged -= OnKtgStateChanged;
        KeywordEngine.Matched -= OnKeywordMatched;
        ChatLog.Dispose();
        LogTailer.Dispose();
        AddonProbe.Dispose();
        KeywordEngine.Dispose();
        autoInputLock.Dispose();
    }

    private void Draw() => windowSystem.Draw();

    private void OnFrameworkUpdate(IFramework framework)
    {
        ChatLog.Flush();
        LogTailer.Poll();
        CheckTerritoryAutoOpen();
        Ktg.Poll();
        UpdateAutoLoop();
    }

    #region 魔光键影自动

    /// <summary>KTG 面板状态变化：记录 + 触发自动打字。</summary>
    private void OnKtgStateChanged(KtgState state)
    {
        try
        {
            if (state.HasWord && state.Word != lastObservedWord)
            {
                lastObservedWord = state.Word;
                WriteAutoLog($"[观察] 词「{state.Word}」 输入「{state.Input}」 波次「{state.Wave}」 分数「{state.Score}」 HP「{state.GaugeHp}」 目标「{state.Todo}」");
            }

            if (state.GameOver)
            {
                WriteAutoLog("[状态] 结算/结束面板出现");
                wordAttempts.Clear();
            }

            TryAutoType(state);
        }
        catch (Exception ex)
        {
            LastAutoMessage = $"自动处理异常：{ex.Message}";
            Log.Error(ex, "自动处理异常");
        }
    }

    /// <summary>定时兜底：状态没变化时也持续尝试打词（列表里还有没打过的敌人词）。</summary>
    private void UpdateAutoLoop()
    {
        if (!Configuration.AutoPlay)
            return;

        if ((DateTime.Now - lastAutoTick).TotalMilliseconds < 1000)
            return;

        lastAutoTick = DateTime.Now;
        TryAutoType(Ktg.State);
    }

    /// <summary>
    /// 从 KTGEWord 的拼接文本里拆出所有敌人词（实测形如 "地狱屋 血祭 季托夫 希瓦 弗雷沙"），
    /// 优先打还没打过 / 打得少 / 较短的词，避免反复打同一个（可能已死）的词。
    /// </summary>
    private void TryAutoType(KtgState state)
    {
        if (!Configuration.AutoPlay || !state.InGame || state.GameOver)
            return;

        // 安全门：任何确认对话框（退出副本 / 离开队伍 / 排本确认）出现时不发按键
        if (IsBlockingDialogVisible())
        {
            if ((DateTime.Now - lastSafetyLogAt).TotalSeconds > 5)
            {
                lastSafetyLogAt = DateTime.Now;
                WriteAutoLog("[安全] 检测到确认对话框，暂停自动输入");
            }

            return;
        }

        // 输入框必须还在（结算后输入框消失，此时发按键可能确认对话框）
        if (!Ktg.IsAddonVisible("KTGInput"))
            return;

        // 游戏失焦：暂停输入（否则会误输入到别的窗口），可选自动切回前台
        if (!AutoTyper.IsGameForeground())
        {
            if ((DateTime.Now - lastFocusLogAt).TotalSeconds > 8)
            {
                lastFocusLogAt = DateTime.Now;
                WriteAutoLog("[前台] 游戏不在前台，暂停自动输入");
                ChatGui.Print($"[KeyboundBrawler] 游戏不在前台，自动输入已暂停{(Configuration.AutoRefocusGame ? "（正在尝试切回）" : string.Empty)}");
            }

            if (Configuration.AutoRefocusGame)
                AutoTyper.TryFocusGame();

            return;
        }

        // 数据太旧（面板可能已经关了/卡住）就不要输入
        if ((DateTime.Now - Ktg.StateUpdatedAt).TotalSeconds > 1.5)
            return;

        var candidates = ExtractCandidates(state.Word);
        if (candidates.Count > 0 && Ktg.WordNodes.Count > 0)
        {
            // 有节点坐标时按位置/顺序重新排列，实现"先集火前面的"
            var ordered = OrderNodes(Ktg.WordNodes);
            var byNode = ExtractCandidatesFromNodes(ordered);
            if (byNode.Count > 0)
                candidates = byNode;
        }

        if (candidates.Count == 0)
        {
            // Fever 等状态会把敌人词覆盖成提示文字，此时打不了，记录一下
            if ((DateTime.Now - lastNoWordLogAt).TotalSeconds > 15)
            {
                lastNoWordLogAt = DateTime.Now;
                var preview = state.Word.Length > 60 ? state.Word[..60] + "…" : state.Word;
                WriteAutoLog($"[观察] 当前没有可打的词（面板：{preview}）");
            }

            return;
        }

        // 清掉已经不在列表里的词（说明那个敌人已经死了）
        foreach (var key in wordAttempts.Keys.Where(k => !candidates.Contains(k)).ToList())
            wordAttempts.Remove(key);

        // 集火：一直打排第一的（最近的）词，直到它从列表消失（死亡）再打下一个
        var target = candidates[0];
        if (wordAttempts.GetValueOrDefault(target, 0) >= 3)
        {
            target = candidates.FirstOrDefault(c => wordAttempts.GetValueOrDefault(c, 0) < 3)!;
            if (target is null)
                return;
        }

        if (target is null)
            return;

        // 节流：每次随机区间，避免固定节拍（正常 900~1500ms，演都不演 120~260ms）
        if (DateTime.Now < nextAutoAllowedAt)
            return;

        var minIntervalMs = Configuration.AutoBlatantMode ? 120 : 900;
        var maxIntervalMs = Configuration.AutoBlatantMode ? 260 : 1500;
        nextAutoAllowedAt = DateTime.Now.AddMilliseconds(Random.Shared.Next(minIntervalMs, maxIntervalMs));
        lastAutoAt = DateTime.Now;
        wordAttempts[target] = wordAttempts.GetValueOrDefault(target, 0) + 1;

        var text = ResolveInputText(target);
        var mode = ParseMode(Configuration.AutoInputMode);

        _ = Task.Run(() =>
        {
            if (!autoInputLock.Wait(2500))
                return;

            try
            {
                // 拟人化：看到词之后先"反应"一下，别一出现就秒打
                var reaction = AutoTyper.NextStartDelay();
                if (reaction > 0)
                    Thread.Sleep(reaction);

                // 反应期间游戏可能已经结束 / 目标已消失，重新确认
                var currentState = Ktg.State;
                if (currentState.GameOver || !currentState.InGame)
                {
                    WriteAutoLog("[跳过] 输入前检测到游戏已结束");
                    return;
                }

                if (!ExtractCandidates(currentState.Word).Contains(target))
                {
                    WriteAutoLog($"[跳过] 反应期间目标已消失：{target}");
                    return;
                }

                var ok = AutoTyper.TypeAndEnter(text, mode);
                var message = ok ? $"已自动输入「{text}」" : $"自动输入失败：{AutoTyper.LastError}";
                WriteAutoLog($"[自动] {(ok ? "OK" : "FAIL")} 词「{target}」 输入「{text}」 反应{reaction}ms {AutoTyper.LastError}");
                LastAutoMessage = message;
            }
            finally
            {
                autoInputLock.Release();
            }
        });
    }

    /// <summary>按配置给词节点排序（默认按屏幕 Y 从下到上 = 最近的敌人优先）。</summary>
    private List<WordNode> OrderNodes(IReadOnlyList<WordNode> nodes) => Configuration.AutoTargetOrder switch
    {
        "order_reverse" => Enumerable.Reverse(nodes).ToList(),
        "screen_asc" => nodes.OrderBy(n => n.Y).ThenBy(n => n.X).ToList(),
        "screen_desc" => nodes.OrderByDescending(n => n.Y).ThenBy(n => n.X).ToList(),
        "shortest" => nodes.OrderBy(n => n.Text.Length).ToList(),
        "longest" => nodes.OrderByDescending(n => n.Text.Length).ToList(),
        _ => nodes.ToList(),
    };

    /// <summary>从词节点提取候选词（校正到词表，保序）。</summary>
    private List<string> ExtractCandidatesFromNodes(List<WordNode> nodes)
    {
        var result = new List<string>();
        foreach (var node in nodes)
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

    /// <summary>是否有确认类对话框（此时按回车会确认默认选项）。</summary>
    private bool IsBlockingDialogVisible()
    {
        foreach (var addon in BlockingAddons)
        {
            if (Ktg.IsAddonVisible(addon))
                return true;
        }

        return false;
    }

    /// <summary>从拼接文本拆出候选词并校正到词表。</summary>
    private List<string> ExtractCandidates(string wordList)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(wordList))
            return result;

        foreach (var segment in wordList.Split(new[] { ' ', '\u3000' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var resolved = Book.ResolveWord(segment);
            if (resolved.Length < 2 || resolved.Length > 24)
                continue;

            // 只保留词表里存在的词，避免把 UI 提示文字当词
            if (Book.FindExact(resolved) is null)
                continue;

            if (!result.Contains(resolved, StringComparer.Ordinal))
                result.Add(resolved);
        }

        // 兜底：没拆出候选但文本本身像词
        if (result.Count == 0)
        {
            var fallback = Book.ResolveWord(wordList);
            if (fallback.Length >= 2 && Book.FindExact(fallback) is not null)
                result.Add(fallback);
        }

        return result;
    }

    /// <summary>过滤明显不是词的内容（纯数字、空、过长），避免把面板里的数字当词输入。</summary>
    private static bool IsValidWord(string word)
    {
        var trimmed = word.Trim();
        if (trimmed.Length == 0 || trimmed.Length > 24)
            return false;

        // 至少要有一个字母/汉字
        if (!trimmed.Any(char.IsLetter))
            return false;

        return true;
    }

    /// <summary>决定实际要输入的文本：中文原词，或从词库查拼音。</summary>
    private string ResolveInputText(string word)
    {
        if (!string.Equals(Configuration.AutoInputContent, "pinyin", StringComparison.OrdinalIgnoreCase))
            return word;

        var entry = Book.Words.FirstOrDefault(w => string.Equals(w.Word, word, StringComparison.Ordinal));
        return entry?.Pinyin ?? word;
    }

    private static AutoInputMode ParseMode(string mode) => mode switch
    {
        "keystrokes" => AutoInputMode.Keystrokes,
        "unicode" => AutoInputMode.Unicode,
        "disabled" => AutoInputMode.Disabled,
        "paste" => AutoInputMode.Paste,
        "paste_char" => AutoInputMode.PasteChar,
        _ => AutoInputMode.PasteChar,
    };

    /// <summary>把配置同步到 AutoTyper（UI 改设置后调用）。</summary>
    public void ApplyAutoTyperSettings()
    {
        AutoTyper.CharDelayMs = Configuration.AutoCharDelayMs;
        AutoTyper.Humanize = Configuration.AutoHumanize;
        AutoTyper.BlatantMode = Configuration.AutoBlatantMode;
        AutoTyper.JitterPercent = Configuration.AutoJitterPercent;
        AutoTyper.StartDelayMinMs = Configuration.AutoStartDelayMinMs;
        AutoTyper.StartDelayMaxMs = Configuration.AutoStartDelayMaxMs;
        AutoTyper.SubmitDelayMinMs = Configuration.AutoSubmitDelayMinMs;
        AutoTyper.SubmitDelayMaxMs = Configuration.AutoSubmitDelayMaxMs;
    }

    /// <summary>手动测试：输入一段文本并回车。</summary>
    public bool TryManualInput(string text)
    {
        var ok = AutoTyper.TypeAndEnter(text, ParseMode(Configuration.AutoInputMode));
        LastAutoMessage = ok ? $"手动测试输入「{text}」已发送" : $"手动测试失败：{AutoTyper.LastError}";
        WriteAutoLog($"[手动] {(ok ? "OK" : "FAIL")} 输入「{text}」 {AutoTyper.LastError}");
        return ok;
    }

    private void WriteAutoLog(string line)
    {
        try
        {
            var path = Path.Combine(LogDirectory, $"auto-{DateTime.Now:yyyyMMdd}.log");
            File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss.fff} {line}{Environment.NewLine}", new System.Text.UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "写自动日志失败");
        }
    }

    #endregion

    #region 自动弹窗 / 关键字事件

    private void CheckTerritoryAutoOpen()
    {
        var territoryId = ClientState.TerritoryType;
        if (territoryId == lastTerritoryId)
            return;

        lastTerritoryId = territoryId;
        if (territoryId == 0 || !ClientState.IsLoggedIn)
            return;

        var name = GetTerritoryName(territoryId);
        if (string.IsNullOrEmpty(name) || !MatchesKeywords(name, Configuration.TerritoryKeywords))
            return;

        TryAutoOpen($"检测到地图「{name}」");
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

    private void OnAddonShown(string addonName)
    {
        if (!Configuration.AutoOpenOnAddon)
            return;

        if (!MatchesKeywords(addonName, Configuration.AddonKeywords))
            return;

        TryAutoOpen($"检测到面板「{addonName}」");
    }

    private void TryAutoOpen(string reason)
    {
        if (!Configuration.AutoOpenOnTerritory)
            return;

        if ((DateTime.Now - lastAutoOpenAt).TotalSeconds < 15)
            return;

        lastAutoOpenAt = DateTime.Now;
        mainWindow.IsOpen = true;
        ChatGui.Print($"[KeyboundBrawler] {reason}，已自动打开窗口。");
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

    private void OnAddonTextSnapshotted(string addonName, System.Collections.Generic.IReadOnlyList<string> texts)
    {
        KeywordEngine.Evaluate($"addon:{addonName}", string.Join(" | ", texts));
    }

    private void OnKeywordMatched(KeywordMatch match)
    {
        if (Configuration.NotifyKeywordMatchesInChat)
            ChatGui.Print($"[KeyboundBrawler] 事件：{match.EventName}（关键词「{match.Keyword}」，来源 {match.Source}）");
    }

    #endregion

    #region 命令

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

            case "type":
                if (parts.Length < 2)
                {
                    ChatGui.Print("[KeyboundBrawler] 用法：/kbb type <文本>（在游戏前台时模拟输入并回车）");
                    break;
                }

                ChatGui.Print(TryManualInput(string.Join(" ", parts.Skip(1))) ? "[KeyboundBrawler] 已发送输入" : $"[KeyboundBrawler] 发送失败：{AutoTyper.LastError}");
                break;

            case "ktg":
            {
                Ktg.Poll(true);
                var state = Ktg.State;
                ChatGui.Print($"[KeyboundBrawler] KTG：游戏内 {state.InGame}，结束 {state.GameOver}；词「{state.Word}」 输入「{state.Input}」 波次「{state.Wave}」 分数「{state.Score}」 HP「{state.GaugeHp}」 目标「{state.Todo}」");
                ChatGui.Print($"[KeyboundBrawler] 词节点（{Ktg.WordNodes.Count}）：{string.Join("  ", Ktg.WordNodes.Select(n => n.ToString()))}");
                break;
            }

            case "nodes":
            {
                try
                {
                    var lines = parts.Length > 1 ? Ktg.DumpNodeTree(parts[1]) : Ktg.DumpAllProbeAddons();
                    if (lines.Count == 0)
                    {
                        ChatGui.Print("[KeyboundBrawler] 没有可见的 KTG 面板（先进本）。");
                        break;
                    }

                    var path = Path.Combine(LogDirectory, $"ktg-nodes-manual-{DateTime.Now:HHmmss}.log");
                    Directory.CreateDirectory(LogDirectory);
                    File.WriteAllLines(path, lines, new System.Text.UTF8Encoding(false));
                    ChatGui.Print($"[KeyboundBrawler] 节点树 {lines.Count} 行已写入 {path}");
                }
                catch (Exception ex)
                {
                    ChatGui.PrintError($"[KeyboundBrawler] dump 失败：{ex.Message}");
                }

                break;
            }

            case "log":
                OpenLogFolder();
                break;

            case "dump":
                if (parts.Length < 2)
                {
                    ChatGui.Print("[KeyboundBrawler] 用法：/kbb dump <面板名>，例如 /kbb dump KTGEWord");
                    break;
                }

                try
                {
                    var values = Ktg.DumpValues(parts[1]);
                    ChatGui.Print($"[KeyboundBrawler] {parts[1]}：{string.Join(" | ", values.Select(v => $"[{v.Index}]{v.Value}"))}");
                }
                catch (Exception ex)
                {
                    ChatGui.PrintError($"[KeyboundBrawler] 读取失败：{ex.Message}");
                }

                break;

            case "text":
                if (parts.Length < 2)
                {
                    ChatGui.Print("[KeyboundBrawler] 用法：/kbb text <面板名>，读取该面板所有文本节点");
                    break;
                }

                ChatGui.Print($"[KeyboundBrawler] {parts[1]} 文本节点：{Ktg.ReadTextNodes(parts[1])}");
                break;

            case "words":
                ChatGui.Print($"[KeyboundBrawler] 词库 {Book.Count} 条（{Book.Source}），可作拼音映射。");
                break;

            case "reload":
                LoadWords();
                KeywordEngine.LoadRules(Configuration.KeywordRules);
                ChatGui.Print($"[KeyboundBrawler] 已重载：词库 {Book.Count} 条，规则 {KeywordEngine.Rules.Count} 条。");
                break;

            case "addons":
            {
                var recent = AddonProbe.GetRecent();
                ChatGui.Print($"[KeyboundBrawler] 已记录 {AddonProbe.SeenCount} 个面板名；最近：{string.Join(", ", recent.Take(8).Select(s => s.Name))}");
                break;
            }

            case "events":
            {
                var events = KeywordEngine.GetRecent();
                ChatGui.Print(events.Count == 0
                    ? $"[KeyboundBrawler] 还没有关键字事件（规则 {KeywordEngine.Rules.Count} 条）。"
                    : $"[KeyboundBrawler] 最近事件：{string.Join("；", events.Take(5).Select(e => $"{e.Time:HH:mm:ss} {e.EventName}({e.Keyword})"))}");
                break;
            }

            default:
                ChatGui.Print("[KeyboundBrawler] 子命令：auto on|off / type <文本> / ktg / text <面板> / dump <面板> / log / words / reload / addons / events；不带参数打开窗口。");
                break;
        }
    }

    #endregion

    #region 词库 / 文件

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
            Process.Start(new ProcessStartInfo
            {
                FileName = LogDirectory,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "打开日志目录失败");
        }
    }

    #endregion
}
