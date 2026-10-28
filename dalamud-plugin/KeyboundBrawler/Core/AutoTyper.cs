using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Dalamud.Plugin.Services;

namespace KeyboundBrawler.Core;

/// <summary>
/// 键盘工具：防 AFK 心跳 + 拟人化延迟。
/// 输入本身不走键盘（由 KtgMonitor 直接写游戏输入框组件），这里只负责"让游戏认为有人在操作"。
/// </summary>
public sealed class AutoTyper
{
    private const uint InputKeyboard = 1;
    private const uint KeyeventfKeyup = 0x0002;
    private const ushort VkReturn = 0x0D;

    private readonly IPluginLog pluginLog;

    public AutoTyper(IPluginLog pluginLog)
    {
        this.pluginLog = pluginLog;
    }

    /// <summary>每个字符写入的基准间隔（毫秒）。</summary>
    public int CharDelayMs { get; set; } = 120;

    /// <summary>拟人化开关。</summary>
    public bool Humanize { get; set; } = true;

    /// <summary>演都不演：关掉拟人化延迟。</summary>
    public bool BlatantMode { get; set; }

    /// <summary>看到新词后的反应延迟下限（毫秒）。</summary>
    public int StartDelayMinMs { get; set; } = 600;

    /// <summary>看到新词后的反应延迟上限（毫秒）。</summary>
    public int StartDelayMaxMs { get; set; } = 1500;

    public string? LastError { get; private set; }

    /// <summary>下一个字符的间隔（拟人化时随机抖动）。</summary>
    public int NextCharDelay()
    {
        if (BlatantMode)
            return 0;

        if (!Humanize)
            return Math.Max(5, CharDelayMs);

        var delay = CharDelayMs * (0.75 + Random.Shared.NextDouble() * 0.6);

        // 5% 概率"手滑停顿"
        if (Random.Shared.NextDouble() < 0.05)
            delay += Random.Shared.Next(80, 240);

        return Math.Max(20, (int)Math.Round(delay));
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

    /// <summary>游戏窗口是否在前台（插件在游戏进程内）。</summary>
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

    /// <summary>尝试把游戏窗口切回前台。</summary>
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

    /// <summary>
    /// 防 AFK 心跳：F13/F14/左右 Shift（无绑定）+ 左右方向键短按（产生角色动作）。
    /// 让客户端输入检测和服务端动作检测都认为玩家在操作。
    /// </summary>
    public void SendAfkWiggle()
    {
        if (!IsGameForeground())
            return;

        try
        {
            SendVirtualKey(0x7C, 4);  // F13
            SendVirtualKey(0x7D, 4);  // F14
            SendVirtualKey(0xA0, 4);  // LShift
            SendVirtualKey(0xA1, 4);  // RShift
            SendVirtualKey(0x25, 30); // Left
            SendVirtualKey(0x27, 30); // Right
            LastError = null;
        }
        catch (Exception ex)
        {
            LastError = $"防 AFK 按键失败：{ex.Message}";
        }
    }

    /// <summary>按一次回车（手动测试用）。</summary>
    public bool SendEnter()
    {
        if (!IsGameForeground())
            return false;
        SendVirtualKey(VkReturn, 30);
        return true;
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
        ThreadSleep(Math.Max(1, delayMs));
    }

    private static void ThreadSleep(int ms) => System.Threading.Thread.Sleep(ms);

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

    #endregion
}
