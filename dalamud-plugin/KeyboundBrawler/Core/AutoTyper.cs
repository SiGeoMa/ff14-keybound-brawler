using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Dalamud.Plugin.Services;

namespace KeyboundBrawler.Core;

public enum AutoInputMode
{
    /// <summary>不自动输入，只监控。</summary>
    Disabled = 0,

    /// <summary>SendInput Unicode（直接送字符，绕过输入法；实测游戏输入框可能不接收）。</summary>
    Unicode = 1,

    /// <summary>逐字符虚拟键（仅 ASCII / 字母数字）。</summary>
    Keystrokes = 2,

    /// <summary>写系统剪贴板 + Ctrl+V 粘贴 + 回车（中文最可靠）。</summary>
    Paste = 3,

    /// <summary>逐字粘贴：一个字符一个字符 Ctrl+V，像真人打字一样慢慢出来。</summary>
    PasteChar = 4,
}

/// <summary>
/// 模拟键盘输入（SendInput）。只在游戏窗口位于前台时发送，避免误输入到别处。
/// 中文用 KEYEVENTF_UNICODE 直接送字符，不需要切换输入法。
/// </summary>
public sealed class AutoTyper
{
    private const uint InputKeyboard = 1;
    private const uint KeyeventfKeyup = 0x0002;
    private const uint KeyeventfUnicode = 0x0004;
    private const ushort VkReturn = 0x0D;
    private const ushort VkControl = 0x11;
    private const ushort VkV = 0x56;

    private readonly IPluginLog pluginLog;

    public AutoTyper(IPluginLog pluginLog)
    {
        this.pluginLog = pluginLog;
    }

    /// <summary>每个字符的基准间隔（毫秒）。</summary>
    public int CharDelayMs { get; set; } = 60;

    /// <summary>拟人化开关：开启后字符间隔随机抖动，并额外加随机卡顿。</summary>
    public bool Humanize { get; set; } = true;

    /// <summary>字符间隔抖动百分比（0-80）。</summary>
    public int JitterPercent { get; set; } = 45;

    /// <summary>看到新词后的反应延迟下限（毫秒）。</summary>
    public int StartDelayMinMs { get; set; } = 350;

    /// <summary>看到新词后的反应延迟上限（毫秒）。</summary>
    public int StartDelayMaxMs { get; set; } = 900;

    /// <summary>打完字符到按回车之间的停顿下限（毫秒）。</summary>
    public int SubmitDelayMinMs { get; set; } = 150;

    /// <summary>打完字符到按回车之间的停顿上限（毫秒）。</summary>
    public int SubmitDelayMaxMs { get; set; } = 400;

    /// <summary>提交后等待（非拟人化模式用）。</summary>
    public int AfterSubmitMs { get; set; } = 120;

    public string? LastError { get; private set; }

    /// <summary>演都不演：关闭所有拟人化延迟，全速输入。</summary>
    public bool BlatantMode { get; set; }

    /// <summary>下一个字符的间隔（拟人化时随机抖动，偶尔停顿一下）。</summary>
    public int NextCharDelay()
    {
        if (BlatantMode)
            return 0;

        if (!Humanize)
            return Math.Max(5, CharDelayMs);

        var spread = CharDelayMs * Math.Clamp(JitterPercent, 0, 80) / 100.0;
        var delay = CharDelayMs + (Random.Shared.NextDouble() * 2 - 1) * spread;

        // 5% 概率"手滑停顿"一下，更像真人
        if (Random.Shared.NextDouble() < 0.05)
            delay += Random.Shared.Next(80, 260);

        return Math.Max(8, (int)Math.Round(delay));
    }

    /// <summary>看到新词后的反应延迟。</summary>
    public int NextStartDelay()
    {
        if (BlatantMode)
            return 0;

        if (!Humanize)
            return 0;

        var min = Math.Max(0, StartDelayMinMs);
        var max = Math.Max(min, StartDelayMaxMs);
        return Random.Shared.Next(min, max + 1);
    }

    /// <summary>提交前的停顿。</summary>
    public int NextSubmitDelay()
    {
        if (BlatantMode)
            return 15;

        if (!Humanize)
            return Math.Max(1, AfterSubmitMs);

        var min = Math.Max(0, SubmitDelayMinMs);
        var max = Math.Max(min, SubmitDelayMaxMs);
        return Random.Shared.Next(min, max + 1);
    }

    /// <summary>尝试把游戏窗口切回前台（游戏失焦时防止挂机/失败）。</summary>
    public bool TryFocusGame()
    {
        if (IsGameForeground())
            return true;

        try
        {
            var hwnd = Process.GetCurrentProcess().MainWindowHandle;
            if (hwnd == IntPtr.Zero)
                return false;

            return SetForegroundWindow(hwnd);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>游戏窗口是否在前台（插件在游戏进程内，比较前台窗口 PID 即可）。</summary>
    public static bool IsGameForeground()
    {
        try
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero)
                return false;
            _ = GetWindowThreadProcessId(hwnd, out var pid);
            return pid == (uint)Environment.ProcessId;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>输入文本并回车提交。返回是否真的发送了。</summary>
    public bool TypeAndEnter(string text, AutoInputMode mode)
    {
        if (string.IsNullOrEmpty(text) || mode == AutoInputMode.Disabled)
            return false;

        if (!IsGameForeground())
        {
            LastError = "游戏不在前台，跳过自动输入";
            return false;
        }

        if (mode == AutoInputMode.Paste)
            return PasteAndEnter(text);

        if (mode == AutoInputMode.PasteChar)
            return PasteCharByChar(text);

        try
        {
            if (mode == AutoInputMode.Unicode)
            {
                foreach (var ch in text)
                {
                    SendUnicodeChar(ch);
                    Thread.Sleep(NextCharDelay());
                }
            }
            else
            {
                foreach (var ch in text)
                {
                    SendVirtualKey((ushort)char.ToUpperInvariant(ch), NextCharDelay());
                }
            }

            Thread.Sleep(NextSubmitDelay());
            SendVirtualKey(VkReturn, 30);
            LastError = null;
            return true;
        }
        catch (Exception ex)
        {
            LastError = $"模拟输入失败：{ex.Message}";
            pluginLog.Warning(ex, "模拟输入失败");
            return false;
        }
    }

    /// <summary>只按回车（调试用）。</summary>
    public bool SendEnter()
    {
        if (!IsGameForeground())
            return false;
        SendVirtualKey(VkReturn, 30);
        return true;
    }

    /// <summary>粘贴前是否先清空输入框（Ctrl+A + Delete），避免上一次的内容残留叠加。</summary>
    public bool ClearInputBeforePaste { get; set; } = true;

    /// <summary>粘贴：先清空输入框，再写剪贴板、Ctrl+V、回车。</summary>
    public bool PasteAndEnter(string text)
    {
        if (string.IsNullOrEmpty(text) || !IsGameForeground())
            return false;

        try
        {
            if (ClearInputBeforePaste)
                SendClearInput();

            if (!TrySetClipboardText(text))
            {
                LastError = "写剪贴板失败";
                return false;
            }

            Thread.Sleep(40);
            SendKeyCombo(VkControl, VkV);
            Thread.Sleep(NextSubmitDelay());
            SendVirtualKey(VkReturn, 30);
            LastError = null;
            return true;
        }
        catch (Exception ex)
        {
            LastError = $"粘贴输入失败：{ex.Message}";
            return false;
        }
    }

    /// <summary>逐字粘贴：一个字符一次 Ctrl+V，间隔随机，像真人打字一样一个字一个字出来。</summary>
    public bool PasteCharByChar(string text)
    {
        if (string.IsNullOrEmpty(text) || !IsGameForeground())
            return false;

        try
        {
            if (ClearInputBeforePaste)
                SendClearInput();

            foreach (var ch in text)
            {
                if (!TrySetClipboardText(ch.ToString()))
                {
                    LastError = "写剪贴板失败";
                    return false;
                }

                Thread.Sleep(12);
                SendKeyCombo(VkControl, VkV);
                Thread.Sleep(NextCharDelay());
            }

            Thread.Sleep(NextSubmitDelay());
            SendVirtualKey(VkReturn, 30);
            LastError = null;
            return true;
        }
        catch (Exception ex)
        {
            LastError = $"逐字粘贴失败：{ex.Message}";
            return false;
        }
    }

    /// <summary>清空当前输入框：Ctrl+A + Delete。</summary>
    public void SendClearInput()
    {
        if (!IsGameForeground())
            return;

        SendKeyCombo(VkControl, 0x41 /* A */);
        Thread.Sleep(25);
        SendVirtualKey(0x2E /* Delete */, 25);
        Thread.Sleep(20);
    }

    private static bool TrySetClipboardText(string text)
    {
        if (!OpenClipboard(IntPtr.Zero))
            return false;

        try
        {
            EmptyClipboard();
            var handle = Marshal.StringToHGlobalUni(text);
            try
            {
                if (SetClipboardData(13 /* CF_UNICODETEXT */, handle) == IntPtr.Zero)
                    return false;

                handle = IntPtr.Zero; // 所有权转移给剪贴板
                return true;
            }
            finally
            {
                if (handle != IntPtr.Zero)
                    Marshal.FreeHGlobal(handle);
            }
        }
        finally
        {
            CloseClipboard();
        }
    }

    private static void SendUnicodeChar(char ch)
    {
        var inputs = new INPUT[2];

        inputs[0].type = InputKeyboard;
        inputs[0].union.keyboard.wVk = 0;
        inputs[0].union.keyboard.wScan = ch;
        inputs[0].union.keyboard.dwFlags = KeyeventfUnicode;

        inputs[1].type = InputKeyboard;
        inputs[1].union.keyboard.wVk = 0;
        inputs[1].union.keyboard.wScan = ch;
        inputs[1].union.keyboard.dwFlags = KeyeventfUnicode | KeyeventfKeyup;

        SendInput(2, inputs, Marshal.SizeOf<INPUT>());
    }

    private static void SendVirtualKey(ushort vk, int delayMs)
    {
        var inputs = new INPUT[2];

        inputs[0].type = InputKeyboard;
        inputs[0].union.keyboard.wVk = vk;
        inputs[0].union.keyboard.dwFlags = 0;

        inputs[1].type = InputKeyboard;
        inputs[1].union.keyboard.wVk = vk;
        inputs[1].union.keyboard.dwFlags = KeyeventfKeyup;

        SendInput(2, inputs, Marshal.SizeOf<INPUT>());
        Thread.Sleep(Math.Max(1, delayMs));
    }

    private static void SendKeyCombo(ushort modifier, ushort key)
    {
        var inputs = new INPUT[4];

        inputs[0].type = InputKeyboard;
        inputs[0].union.keyboard.wVk = modifier;
        inputs[0].union.keyboard.dwFlags = 0;

        inputs[1].type = InputKeyboard;
        inputs[1].union.keyboard.wVk = key;
        inputs[1].union.keyboard.dwFlags = 0;

        inputs[2].type = InputKeyboard;
        inputs[2].union.keyboard.wVk = key;
        inputs[2].union.keyboard.dwFlags = KeyeventfKeyup;

        inputs[3].type = InputKeyboard;
        inputs[3].union.keyboard.wVk = modifier;
        inputs[3].union.keyboard.dwFlags = KeyeventfKeyup;

        SendInput(4, inputs, Marshal.SizeOf<INPUT>());
    }

    #region Win32

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion union;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public KEYBDINPUT keyboard;
        [FieldOffset(0)] public MOUSEINPUT mouse;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    #endregion
}
