using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace XpPenPad;

/// <summary>Win32 调用与小工具函数。全部集中在这里，方便核对。</summary>
internal static class Interop
{
    // ---------------- 前景窗口 / 进程 ----------------

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    /// <summary>当前真正的前台窗口句柄，用于校验 SetForegroundWindow 到底有没有生效。</summary>
    public static IntPtr ForegroundWindow() => GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    /// <summary>返回前台窗口所属进程名（小写、含 .exe），失败返回空串。</summary>
    public static string ForegroundProcessName()
    {
        try
        {
            IntPtr h = GetForegroundWindow();
            if (h == IntPtr.Zero) return "";
            GetWindowThreadProcessId(h, out uint pid);
            if (pid == 0) return "";
            using Process p = Process.GetProcessById((int)pid);
            return (p.ProcessName + ".exe").ToLowerInvariant();
        }
        catch { return ""; }
    }

    // ---------------- 光标 ----------------

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT p);

    public static (int x, int y) Cursor()
    {
        return GetCursorPos(out POINT p) ? (p.X, p.Y) : (0, 0);
    }

    // ---------------- 键盘状态 ----------------

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    public static bool IsDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    public const int VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_ESCAPE = 0x1B;

    // ---------------- SendInput ----------------

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    // 必须定义出来：x64 上 sizeof(INPUT)=40，因为 union 取最大成员 MOUSEINPUT(32)，
    // 而不是 KEYBDINPUT(24)。少了它 cbSize 会算成 32，SendInput 返回 0 + ERROR_INVALID_PARAMETER(87)。
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

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUT
    {
        [FieldOffset(0)] public uint type;
        [FieldOffset(8)] public MOUSEINPUT mi;   // 只为撑开 union 到 32 字节
        [FieldOffset(8)] public KEYBDINPUT ki;
    }

    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    // 给注入的事件打标记，自己的钩子看到这个就跳过，避免自激
    private static readonly IntPtr Tag = new(0x58505044); // "XPPD"

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    private static uint KeySend(ushort vk, bool up)
    {
        var inp = new INPUT[1];
        inp[0].type = INPUT_KEYBOARD;
        inp[0].ki.wVk = vk;
        inp[0].ki.dwFlags = up ? KEYEVENTF_KEYUP : 0;
        inp[0].ki.dwExtraInfo = Tag;
        return SendInput(1, inp, Marshal.SizeOf<INPUT>());
    }

    /// <summary>最近一次 SendInput 实际插入的事件数。0 表示被系统拒绝（多为 UIPI 权限不足）。</summary>
    public static uint LastInserted;

    public static int LastWin32Error => Marshal.GetLastWin32Error();

    /// <summary>发送 Alt + 单键（发给当前焦点窗口）。</summary>
    public static void SendAlt(char c)
    {
        ushort vk = char.ToUpperInvariant(c);
        KeySend(VK_MENU, false);
        KeySend(vk, false);
        KeySend(vk, true);
        KeySend(VK_MENU, true);
    }

    /// <summary>发送单个按键（不带修饰键），给 F13 这类虚拟键用。</summary>
    public static uint SendKeyPlain(int vk) => SendCombo(false, false, false, false, vk);

    public const int VK_LWIN = 0x5B;

    /// <summary>发送任意组合键，例如 Ctrl+Alt+F12。返回实际插入的事件数。</summary>
    public static uint SendCombo(bool ctrl, bool alt, bool shift, bool win, int vk)
    {
        uint n = 0;
        if (ctrl) n += KeySend(VK_CONTROL, false);
        if (alt) n += KeySend(VK_MENU, false);
        if (shift) n += KeySend(VK_SHIFT, false);
        if (win) n += KeySend(VK_LWIN, false);

        n += KeySend((ushort)vk, false);
        n += KeySend((ushort)vk, true);

        if (win) n += KeySend(VK_LWIN, true);
        if (shift) n += KeySend(VK_SHIFT, true);
        if (alt) n += KeySend(VK_MENU, true);
        if (ctrl) n += KeySend(VK_CONTROL, true);

        LastInserted = n;
        return n;
    }

    // ---------------- 窗口置前（给自检用） ----------------

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    private const int SW_RESTORE = 9;

    /// <summary>按进程名找主窗口句柄。</summary>
    public static IntPtr FindMainWindow(string processName)
    {
        string want = processName.ToLowerInvariant();
        if (!want.EndsWith(".exe")) want += ".exe";

        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (!string.Equals((p.ProcessName + ".exe").ToLowerInvariant(), want, StringComparison.Ordinal))
                    continue;
                if (p.MainWindowHandle != IntPtr.Zero) return p.MainWindowHandle;
            }
            catch { }
            finally { p.Dispose(); }
        }
        return IntPtr.Zero;
    }

    /// <summary>
    /// 强行把窗口拉到前台。窗口一旦被切走，SetForegroundWindow 会被前台锁拒绝，
    /// 所以先 AttachThreadInput 借用前台线程的输入状态。
    /// </summary>
    public static bool ForceForeground(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;

        IntPtr fg = GetForegroundWindow();
        if (fg == hwnd) return true;

        uint fgThread = GetWindowThreadProcessId(fg, out _);
        uint myThread = GetCurrentThreadId();

        bool attached = false;
        try
        {
            // 关键一步：先敲一下 Alt。系统会认为"最近的输入来自本进程"，
            // 从而解除前台锁，否则 SetForegroundWindow 只会改了个寂寞（返回 true 但焦点没动）。
            KeySend(VK_MENU, false);
            KeySend(VK_MENU, true);

            if (fgThread != 0 && fgThread != myThread)
                attached = AttachThreadInput(myThread, fgThread, true);

            ShowWindow(hwnd, SW_RESTORE);
            SetForegroundWindow(hwnd);

            if (GetForegroundWindow() == hwnd) return true;

            // 再试一次
            SetForegroundWindow(hwnd);
            return GetForegroundWindow() == hwnd;
        }
        catch { return false; }
        finally
        {
            if (attached) AttachThreadInput(myThread, fgThread, false);
        }
    }

    // ---------------- 直接把按键投递到指定窗口（不依赖前台焦点） ----------------

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint uCode, uint uMapType);

    public static bool PostKey(IntPtr hwnd, int vk)
    {
        if (hwnd == IntPtr.Zero) return false;
        uint scan = MapVirtualKey((uint)vk, 0);
        IntPtr down = (IntPtr)(1 | (int)(scan << 16));
        IntPtr up = (IntPtr)(1 | (int)(scan << 16) | (1 << 30) | unchecked((int)0x80000000));
        bool a = PostMessage(hwnd, (uint)WM_KEYDOWN, (IntPtr)vk, down);
        bool b = PostMessage(hwnd, (uint)WM_KEYUP, (IntPtr)vk, up);
        return a && b;
    }

    // ---------------- 低层键盘钩子 ----------------
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101,
                      WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;
    private const uint LLKHF_INJECTED = 0x10;

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);

    private static IntPtr _hook = IntPtr.Zero;
    private static LowLevelKeyboardProc _proc;      // 必须持有引用，否则会被 GC 回收

    /// <summary>钩子回调：返回 true 表示吞掉该按键，不传给目标程序。</summary>
    public static Func<int, bool, bool> OnKey;      // (vkCode, isDown) -> swallow?

    public static bool Install()
    {
        _proc = HookCallback;
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
        return _hook != IntPtr.Zero;
    }

    public static void Uninstall()
    {
        if (_hook != IntPtr.Zero) { UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero; }
    }

    private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);

            // 只忽略「我们自己 SendAlt 注入」的键（靠 dwExtraInfo 标记识别）。
            // 不能按 LLKHF_INJECTED 一概忽略 —— 数位板驱动发侧键用的就是注入式按键，
            // 那样会把驱动发来的按键全部静默丢弃，整套东西就会看起来完全没反应。
            bool ours = info.dwExtraInfo == Tag;

            if (!ours)
            {
                int msg = (int)wParam;
                bool isDown = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
                bool isUp = msg == WM_KEYUP || msg == WM_SYSKEYUP;

                if ((isDown || isUp) && OnKey != null)
                {
                    try
                    {
                        if (OnKey((int)info.vkCode, isDown)) return (IntPtr)1;
                    }
                    catch (Exception ex) { Log.Write("hook callback error: " + ex.Message); }
                }
            }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }
}
