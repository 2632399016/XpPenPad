using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Interop;

namespace XpPenPad;

/// <summary>
/// 用 Raw Input 直接读笔的坐标。
///
/// 存在理由：磁盘位置我们一直用的是 GetCursorPos()，那返回的是「系统光标」。
/// 数位板的笔未必驱动系统光标（这一点必须实测，不能假设），一旦不驱动，
/// 圆盘就会出现在错误的位置。Raw Input 是驱动直接喂上来的设备坐标，与光标无关。
///
/// 两类事件：
///   RIM_TYPEMOUSE + MOUSE_MOVE_ABSOLUTE -> lLastX/lLastY 是 0..65535 归一化坐标，
///                                          直接换算成虚拟屏幕像素，最好情况。
///   RIM_TYPEHID                         -> 原始 HID 报告，需要先看内容才能定解析。
///                                          所以这里先原样记录，不猜。
/// </summary>
internal sealed class RawPenTracker : IDisposable
{
    private const int WM_INPUT = 0x00FF;

    private const uint RID_INPUT = 0x10000003;
    private const uint RIDI_DEVICENAME = 0x20000007;
    private const uint RIDI_DEVICEINFO = 0x2000000B;
    private const uint RIDEV_INPUTSINK = 0x00000100;
    private const uint RIDEV_REMOVE = 0x00000001;

    private const uint RIM_TYPEMOUSE = 0;
    private const uint RIM_TYPEKEYBOARD = 1;
    private const uint RIM_TYPEHID = 2;

    private const ushort MOUSE_MOVE_ABSOLUTE = 0x8000;
    private const ushort MOUSE_VIRTUAL_DESKTOP = 0x4000;

    private const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;
    private const int SM_CXSCREEN = 0, SM_CYSCREEN = 1;

    // ------------------------------------------------------------ 结构

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICE
    {
        public ushort usUsagePage;
        public ushort usUsage;
        public uint dwFlags;
        public IntPtr hwndTarget;
    }

    // x64 上这个结构实际是 24 字节（两个指针要 8 字节对齐），
    // 所以数据区从 +24 开始，不是 +16。所有偏移都用 Marshal.SizeOf 算，别写死。
    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTHEADER
    {
        public uint dwType;
        public uint dwSize;
        public IntPtr hDevice;
        public IntPtr wParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RID_DEVICE_INFO
    {
        public uint cbSize;
        public uint dwType;
        public uint dwVendorId;
        public uint dwProductId;
        public uint dwVersionNumber;
        public ushort usUsagePage;
        public ushort usUsage;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICELIST
    {
        public IntPtr hDevice;
        public uint dwType;
    }

    // ------------------------------------------------------------ P/Invoke

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterRawInputDevices(
        RAWINPUTDEVICE[] pRawInputDevices, uint uiNumDevices, uint cbSize);

    [DllImport("user32.dll")]
    private static extern uint GetRawInputDeviceList(
        IntPtr pRawInputDeviceList, ref uint puiNumDevices, uint cbSize);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetRawInputDeviceInfo(
        IntPtr hDevice, uint uiCommand, IntPtr pData, ref uint pcbSize);

    [DllImport("user32.dll")]
    private static extern uint GetRawInputData(
        IntPtr hRawInput, uint uiCommand, IntPtr pData, ref uint pcbSize, uint cbSizeHeader);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    // ------------------------------------------------------------ 实例

    private readonly HwndSource _src;
    private readonly int _hdrSize;
    private IntPtr _buf;
    private int _bufLen;

    private readonly Dictionary<IntPtr, string> _names = new();
    private readonly Dictionary<IntPtr, DateTime> _perDevice = new();

    private int _x, _y;
    private DateTime _at = DateTime.MinValue;
    private string _dev = "";
    private int _hidReports;
    private DateTime _lastHidLog = DateTime.MinValue;
    private int _hidLogCount;

    /// <summary>
    /// 笔原始坐标的量程。全 0 = 还没标定，只把原始值记进日志、不做换算。
    /// 标定方法：把笔尖分别停在数位板工作区的四个角，从日志里读这四个点的值。
    /// </summary>
    public int RawMaxX, RawMaxY;
    public int RawMinX, RawMinY;

    /// <summary>设备名子串过滤；空串 = 接受任何「绝对定位」设备。</summary>
    public string DeviceFilter = "";

    /// <summary>把每个原始事件写进日志（会很吵，只在排查时开）。</summary>
    public bool Verbose;

    public bool HasPos { get; private set; }
    public (int x, int y) Last => (_x, _y);
    public DateTime LastAt => _at;
    public string LastDevice => _dev;

    /// <summary>收到的 HID 报告数（用于判断笔到底是 MOUSE 还是 HID）。</summary>
    public int HidReports => _hidReports;

    /// <summary>标定模式：每次成功解析出原始 X/Y 就回调一次（写到 CSV，不限行、不节流）。</summary>
    public Action<int, int> CalibSample;

    public RawPenTracker()
    {
        _hdrSize = Marshal.SizeOf<RAWINPUTHEADER>();

        var ps = new HwndSourceParameters("XpPenPadRawInput")
        {
            PositionX = 0,
            PositionY = 0,
            Width = 0,
            Height = 0,
            WindowStyle = 0,
            // HWND_MESSAGE：只拿消息队列，不产生任何可见窗口
            ParentWindow = new IntPtr(-3),
        };

        _src = new HwndSource(ps);
        _src.AddHook(WndProc);

        // 注意：绝不能加 RIDEV_NOLEGACY —— 那会让设备不再产生常规鼠标事件，
        // 笔就再也驱动不了 Xournal++ 的绘制了。INPUTSINK 只是"我不在前台也收得到"。
        var devs = new[]
        {
            // 通用桌面 · 鼠标（很多数位板把笔伪装成绝对定位鼠标，这是最好的情况）
            new RAWINPUTDEVICE { usUsagePage = 0x01, usUsage = 0x02, dwFlags = RIDEV_INPUTSINK, hwndTarget = _src.Handle },
            // 数字化仪 · 笔 / 数字化仪 / 触摸屏
            new RAWINPUTDEVICE { usUsagePage = 0x0D, usUsage = 0x01, dwFlags = RIDEV_INPUTSINK, hwndTarget = _src.Handle },
            new RAWINPUTDEVICE { usUsagePage = 0x0D, usUsage = 0x02, dwFlags = RIDEV_INPUTSINK, hwndTarget = _src.Handle },
            new RAWINPUTDEVICE { usUsagePage = 0x0D, usUsage = 0x04, dwFlags = RIDEV_INPUTSINK, hwndTarget = _src.Handle },
        };

        bool ok = RegisterRawInputDevices(devs, (uint)devs.Length, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
        Log.Write($"rawinput register -> {ok} (lastErr={Marshal.GetLastWin32Error()}) hwnd=0x{_src.Handle.ToInt64():X}");
    }

    public void Dispose()
    {
        try
        {
            if (_buf != IntPtr.Zero) { Marshal.FreeHGlobal(_buf); _buf = IntPtr.Zero; }
        }
        catch { }

        try
        {
            // 显式反注册，避免残留的 INPUTSINK 影响后续运行
            var off = new[]
            {
                new RAWINPUTDEVICE { usUsagePage = 0x01, usUsage = 0x02, dwFlags = RIDEV_REMOVE, hwndTarget = IntPtr.Zero },
                new RAWINPUTDEVICE { usUsagePage = 0x0D, usUsage = 0x01, dwFlags = RIDEV_REMOVE, hwndTarget = IntPtr.Zero },
                new RAWINPUTDEVICE { usUsagePage = 0x0D, usUsage = 0x02, dwFlags = RIDEV_REMOVE, hwndTarget = IntPtr.Zero },
                new RAWINPUTDEVICE { usUsagePage = 0x0D, usUsage = 0x04, dwFlags = RIDEV_REMOVE, hwndTarget = IntPtr.Zero },
            };
            RegisterRawInputDevices(off, (uint)off.Length, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
        }
        catch { }

        try { _src?.RemoveHook(WndProc); } catch { }
        try { _src?.Dispose(); } catch { }
    }

    // ------------------------------------------------------------ 枚举设备（--devices）

    /// <summary>
    /// 屏幕布局。必须看这一项：机器上挂了好几个虚拟显示器（向日葵/ToDesk/GlideX），
    /// 一旦虚拟屏幕比物理屏大，数位板又按"整个虚拟桌面"做映射，
    /// 光标就会被送到根本看不见的那块虚拟屏上，圆盘自然就不在笔尖。
    /// </summary>
    public static void LogMonitors()
    {
        var vs = System.Windows.Forms.SystemInformation.VirtualScreen;
        var prim = System.Windows.Forms.Screen.PrimaryScreen;
        Log.Write($"monitors: count={System.Windows.Forms.Screen.AllScreens.Length} " +
                  $"virtual=({vs.X},{vs.Y}) {vs.Width}x{vs.Height} " +
                  $"primary={prim.DeviceName} ({prim.Bounds.X},{prim.Bounds.Y}) {prim.Bounds.Width}x{prim.Bounds.Height}");

        foreach (var s in System.Windows.Forms.Screen.AllScreens)
            Log.Write($"  screen {s.DeviceName} ({s.Bounds.X},{s.Bounds.Y}) {s.Bounds.Width}x{s.Bounds.Height}" +
                      (s.Primary ? "  [primary]" : ""));
    }

    public static void EnumerateDevices()
    {
        LogMonitors();

        uint count = 0;
        GetRawInputDeviceList(IntPtr.Zero, ref count, (uint)Marshal.SizeOf<RAWINPUTDEVICELIST>());
        if (count == 0) { Log.Write("rawinput: 没有枚举到任何设备"); return; }

        int size = Marshal.SizeOf<RAWINPUTDEVICELIST>();
        IntPtr list = Marshal.AllocHGlobal(size * (int)count);
        try
        {
            uint n = count;
            GetRawInputDeviceList(list, ref n, (uint)size);

            Log.Write($"rawinput: {n} 个设备");
            for (int i = 0; i < (int)n; i++)
            {
                var e = Marshal.PtrToStructure<RAWINPUTDEVICELIST>(list + i * size);
                string name = DeviceName(e.hDevice);
                string kind = e.dwType switch
                {
                    RIM_TYPEMOUSE => "MOUSE",
                    RIM_TYPEKEYBOARD => "KEYBOARD",
                    RIM_TYPEHID => "HID",
                    _ => "?" + e.dwType,
                };

                string extra = "";
                if (e.dwType == RIM_TYPEHID)
                {
                    var info = new RID_DEVICE_INFO { cbSize = (uint)Marshal.SizeOf<RID_DEVICE_INFO>() };
                    IntPtr p = Marshal.AllocHGlobal(Marshal.SizeOf<RID_DEVICE_INFO>());
                    try
                    {
                        Marshal.StructureToPtr(info, p, false);
                        uint cb = (uint)Marshal.SizeOf<RID_DEVICE_INFO>();
                        if (GetRawInputDeviceInfo(e.hDevice, RIDI_DEVICEINFO, p, ref cb) != uint.MaxValue)
                        {
                            var got = Marshal.PtrToStructure<RID_DEVICE_INFO>(p);
                            extra = $" vid=0x{got.dwVendorId:X4} pid=0x{got.dwProductId:X4} page=0x{got.usUsagePage:X2} usage=0x{got.usUsage:X2}";
                        }
                    }
                    finally { Marshal.FreeHGlobal(p); }
                }
                Log.Write($"  {kind,-8} {name}{extra}");
            }
        }
        finally { Marshal.FreeHGlobal(list); }
    }

    private static string DeviceName(IntPtr h)
    {
        try
        {
            uint cb = 0;
            if (GetRawInputDeviceInfo(h, RIDI_DEVICENAME, IntPtr.Zero, ref cb) == uint.MaxValue || cb == 0)
                return "";
            IntPtr p = Marshal.AllocHGlobal((int)cb * 2);
            try
            {
                cb = (uint)((int)cb);
                if (GetRawInputDeviceInfo(h, RIDI_DEVICENAME, p, ref cb) == uint.MaxValue) return "";
                return Marshal.PtrToStringUni(p) ?? "";
            }
            finally { Marshal.FreeHGlobal(p); }
        }
        catch { return ""; }
    }

    // ------------------------------------------------------------ 消息处理

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_INPUT)
        {
            try { HandleInput(lParam); }
            catch (Exception ex) { Log.Write("rawinput error: " + ex.Message); }
            handled = true;
            return IntPtr.Zero;
        }
        return IntPtr.Zero;
    }

    private void HandleInput(IntPtr hRawInput)
    {
        uint cb = 0;
        if (GetRawInputData(hRawInput, RID_INPUT, IntPtr.Zero, ref cb, (uint)_hdrSize) == uint.MaxValue)
            return;
        if (cb == 0) return;

        if (_buf == IntPtr.Zero || _bufLen < (int)cb)
        {
            if (_buf != IntPtr.Zero) Marshal.FreeHGlobal(_buf);
            _bufLen = Math.Max((int)cb, 1024);
            _buf = Marshal.AllocHGlobal(_bufLen);
        }

        uint got = GetRawInputData(hRawInput, RID_INPUT, _buf, ref cb, (uint)_hdrSize);
        if (got == uint.MaxValue || got == 0) return;

        uint type = (uint)Marshal.ReadInt32(_buf, 0);
        IntPtr hDev = Marshal.ReadIntPtr(_buf, 8);   // x64: dwType(0..3) dwSize(4..7) hDevice(8..15)

        if (type == RIM_TYPEMOUSE)
        {
            // RAWMOUSE 布局：usFlags(2) pad(2) ulButtons(4) ulRawButtons(4) lLastX(4) lLastY(4) extra(4)
            int off = _hdrSize;
            ushort flags = (ushort)Marshal.ReadInt16(_buf, off + 0);
            int lx = Marshal.ReadInt32(_buf, off + 12);
            int ly = Marshal.ReadInt32(_buf, off + 16);

            bool abs = (flags & MOUSE_MOVE_ABSOLUTE) != 0;
            if (!abs) return;     // 相对移动是普通鼠标，忽略

            string name = Name(hDev);
            if (DeviceFilter.Length > 0 && !name.Contains(DeviceFilter, StringComparison.OrdinalIgnoreCase))
                return;

            bool vdesk = (flags & MOUSE_VIRTUAL_DESKTOP) != 0;
            int vx = vdesk ? GetSystemMetrics(SM_XVIRTUALSCREEN) : 0;
            int vy = vdesk ? GetSystemMetrics(SM_YVIRTUALSCREEN) : 0;
            int vw = vdesk ? GetSystemMetrics(SM_CXVIRTUALSCREEN) : GetSystemMetrics(SM_CXSCREEN);
            int vh = vdesk ? GetSystemMetrics(SM_CYVIRTUALSCREEN) : GetSystemMetrics(SM_CYSCREEN);

            int x = vx + (int)Math.Round(lx / 65535.0 * vw);
            int y = vy + (int)Math.Round(ly / 65535.0 * vh);

            _x = x; _y = y; _at = DateTime.UtcNow; _dev = name;
            HasPos = true;

            if (Verbose)
                Log.Write($"RAW mouse abs ({lx},{ly}) -> ({x},{y}) vdesk={vdesk} dev={Tail(name)}");
            return;
        }

        if (type == RIM_TYPEHID)
        {
            // 高漫的笔在本机不是鼠标，是 HID 数字化仪，坐标就藏在这份报告里。
            // 报告格式取决于设备描述符，这里先按两种最常见的数字化仪布局各猜一份，
            // 连同原始字节一起记进日志 —— 拿到真数据再定死解析，不靠猜。
            int off = _hdrSize;
            uint sizeHid = (uint)Marshal.ReadInt32(_buf, off + 0);
            uint cnt = (uint)Marshal.ReadInt32(_buf, off + 4);
            int dataOff = off + 8;
            if (cnt == 0) return;

            int len = (int)(sizeHid * cnt);
            if (len < 0 || len > 64) len = 0;

            _hidReports++;
            string name = Name(hDev);

            bool throttle = (_hidReports <= 8) || Verbose ||
                            (DateTime.UtcNow - _lastHidLog).TotalMilliseconds >= 100;
            // 标定（Verbose / debug_raw=1）时不限行，确保整段扫描都被记下；
            // 非标定模式仍留 5000 行安全上限，避免日志无限膨胀。
            if (throttle && (Verbose || _hidLogCount < 5000))
            {
                _lastHidLog = DateTime.UtcNow;
                _hidLogCount++;

                var sb = new StringBuilder();
                for (int i = 0; i < Math.Min(len, 12); i++)
                    sb.Append(Marshal.ReadByte(_buf, dataOff + i).ToString("X2")).Append(' ');

                Log.Write($"RAW hid dev={Tail(name)} sizeHid={sizeHid} cnt={cnt} " +
                          $"bytes=[{sb.ToString().TrimEnd()}]  " +
                          $"A(X@2,Y@4)=({Rd16(dataOff + 2)},{Rd16(dataOff + 4)}) " +
                          $"B(X@1,Y@3)=({Rd16(dataOff + 1)},{Rd16(dataOff + 3)})");
            }

            if (!FilterOk(name)) return;

            // 本机 tablethid 的报告布局（实测出来的，不是猜的）：
            //   [0]=报告ID(0x08) [1]=笔尖/按键 [2..3]=X(LE) [4..5]=Y(LE)
            //   [6..7]=压感(LE)  [8]=倾斜X [9]=倾斜Y
            int gx = Rd16(dataOff + 2), gy = Rd16(dataOff + 4);
            CalibSample?.Invoke(gx, gy);
            if (RawMaxX > 0 && RawMaxY > 0)
            {
                double spanX = RawMaxX - RawMinX, spanY = RawMaxY - RawMinY;
                int sx = spanX > 0 ? (int)Math.Round((gx - RawMinX) / spanX * ScrW()) : gx;
                int sy = spanY > 0 ? (int)Math.Round((gy - RawMinY) / spanY * ScrH()) : gy;
                _x = sx; _y = sy; _at = DateTime.UtcNow; _dev = name;
                HasPos = true;
            }
            return;
        }
    }

    private static int ScrW() => GetSystemMetrics(SM_CXVIRTUALSCREEN);
    private static int ScrH() => GetSystemMetrics(SM_CYVIRTUALSCREEN);

    /// <summary>读一个 16 位小端值。</summary>
    private int Rd16(int o)
    {
        if (o < 0 || o + 1 >= _bufLen) return 0;
        return Marshal.ReadByte(_buf, o) | (Marshal.ReadByte(_buf, o + 1) << 8);
    }

    private bool FilterOk(string name)
    {
        if (DeviceFilter.Length == 0) return true;
        return name.IndexOf(DeviceFilter, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private string Name(IntPtr h)
    {
        if (_names.TryGetValue(h, out string s)) return s;
        s = DeviceName(h);
        _names[h] = s;
        return s;
    }

    /// <summary>设备路径很长，只留最后两段，日志才看得懂。</summary>
    private static string Tail(string path)
    {
        if (string.IsNullOrEmpty(path)) return "(unknown)";
        var parts = path.Split('#');
        if (parts.Length >= 3) return string.Join("#", parts[^3..]);
        return path;
    }
}

/// <summary>
/// 把「系统光标」和「原始输入的笔坐标」合成一个位置源。
/// 两个都记账，日志里能看到差多少，这是判断"笔到到底驱动不驱动光标"的直接证据。
/// </summary>
internal static class PenSource
{
    public static RawPenTracker Tracker;
    public static string Mode = "auto";        // auto | raw | cursor
    public static int OffsetX, OffsetY;
    private const int FreshMs = 400;

    private static int _lastDx = -99999, _lastDy = -99999;

    /// <summary>曾经拿到过原始笔坐标吗。</summary>
    private static bool HaveRaw() => Tracker != null && Tracker.HasPos;

    /// <summary>原始坐标有多旧。</summary>
    private static long RawAge()
        => Tracker == null ? long.MaxValue : (long)(DateTime.UtcNow - Tracker.LastAt).TotalMilliseconds;

    public static (int x, int y) Get()
    {
        var cur = Interop.Cursor();

        // 笔不驱动光标的机器上，光标是个冻结的死值。
        // 哪怕原始坐标稍微旧一点，也远好过退回一个根本不跟着笔走的位置，
        // 所以只要曾经拿到过原始坐标就一直用它；只有从未拿到过才退回光标。
        if (Mode == "cursor" || !HaveRaw())
            return (cur.x + OffsetX, cur.y + OffsetY);

        return (Tracker.Last.x + OffsetX, Tracker.Last.y + OffsetY);
    }

    /// <summary>返回 (正在用的坐标, 另一个源的坐标, 原始坐标是否新鲜)。</summary>
    public static (int x, int y, int ox, int oy, bool rawOk) Both()
    {
        var cur = Interop.Cursor();
        bool rawOk = HaveRaw();

        int rx = cur.x, ry = cur.y;
        if (rawOk) { rx = Tracker.Last.x; ry = Tracker.Last.y; }

        if (Mode == "cursor" || !rawOk)
            return (cur.x + OffsetX, cur.y + OffsetY, rx + OffsetX, ry + OffsetY, rawOk);

        return (rx + OffsetX, ry + OffsetY, cur.x + OffsetX, cur.y + OffsetY, rawOk);
    }

    /// <summary>上一次记录的 光标-笔 偏差，给日志用；没有数据返回 (0,0)。</summary>
    public static (int dx, int dy) LastDelta => (_lastDx, _lastDy);

    public static void Snapshot(int cx, int cy, int rx, int ry)
    {
        _lastDx = rx - cx;
        _lastDy = ry - cy;
    }

    public static string Describe()
    {
        var cur = Interop.Cursor();
        string t = Tracker == null ? "no tracker" : (Tracker.HasPos ? $"raw=({Tracker.Last.x},{Tracker.Last.y})" : "raw=n/a");
        return $"cursor=({cur.x},{cur.y}) {t} mode={Mode}";
    }
}
