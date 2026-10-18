using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Forms;
using Application = System.Windows.Application;

namespace XpPenPad;

internal static class Program
{
    private const string MutexName = @"Local\XpPenPad.SingleInstance";
    private static Mutex _appMutex;   // 持有到进程退出，保证单实例

    [STAThread]
    private static int Main(string[] args)
    {
        string baseDir = AppContext.BaseDirectory;
        Log.Init(Path.Combine(baseDir, "xpPenPad.log"));

        if (args.Contains("--test", StringComparer.OrdinalIgnoreCase))
            return SelfTest(baseDir);

        if (args.Contains("--preview", StringComparer.OrdinalIgnoreCase))
        {
            int i = Array.FindIndex(args, a => a.Equals("--preview", StringComparison.OrdinalIgnoreCase));
            string outPng = (i >= 0 && i + 1 < args.Length && !args[i + 1].StartsWith("--"))
                ? args[i + 1]
                : Path.Combine(baseDir, "preview.png");
            return Preview(baseDir, outPng);
        }

        // --devices ：列出所有原始输入设备。用来判断数位板的笔到底是「鼠标」还是「HID」。
        // 这条不需要笔有动作，插着就能列出来。
        if (args.Contains("--devices", StringComparer.OrdinalIgnoreCase))
        {
            RawPenTracker.EnumerateDevices();
            return 0;
        }

        // --calib [秒数] ：连续采集笔的原始 HID 坐标到 raw_calib.csv（不限行、不节流），
        // 用于标定 pen_raw_min/max。运行期间把笔扫过整个数位板，到点自动退出。
        if (args.Contains("--calib", StringComparer.OrdinalIgnoreCase))
        {
            int dur = 15;
            int ci = Array.FindIndex(args, a => a.Equals("--calib", StringComparison.OrdinalIgnoreCase));
            if (ci >= 0 && ci + 1 < args.Length && int.TryParse(args[ci + 1], out int dd) && dd > 0 && dd <= 120)
                dur = dd;

            string csv = Path.Combine(baseDir, "raw_calib.csv");
            using var csw = new StreamWriter(csv);
            Log.Write($"=== CALIB === duration={dur}s csv={csv} filter=tablethid");

            var tr = new RawPenTracker { DeviceFilter = "tablethid", Verbose = false };
            tr.CalibSample = (x, y) => { try { csw.WriteLine($"{x},{y}"); } catch { } };
            Log.Write("calib: move the pen across the WHOLE tablet now (hover is fine)");

            var calibApp = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var ct = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(dur),
            };
            ct.Tick += (_, _) =>
            {
                try { csw.Flush(); } catch { }
                Log.Write("calib timer fired");
                calibApp.Shutdown();
            };
            ct.Start();
            calibApp.Run();
            tr.Dispose();
            Log.Write("=== CALIB DONE ===");
            return 0;
        }

        // --sendalt 3 --to xournalpp
        // 把 Alt+<键> 发给指定进程（内置置前）。用于在没有数位板时验证整条链路。
        if (args.Contains("--sendalt", StringComparer.OrdinalIgnoreCase))
        {
            int si = Array.FindIndex(args, a => a.Equals("--sendalt", StringComparison.OrdinalIgnoreCase));
            if (si < 0 || si + 1 >= args.Length)
            {
                Log.Write("--sendalt 需要一个参数，例如 --sendalt 3");
                return 2;
            }
            string keys = args[si + 1];

            string target = "xournalpp";
            int ti = Array.FindIndex(args, a => a.Equals("--to", StringComparison.OrdinalIgnoreCase));
            if (ti >= 0 && ti + 1 < args.Length) target = args[ti + 1];

            IntPtr hwnd = Interop.FindMainWindow(target);
            bool fg = Interop.ForceForeground(hwnd);
            System.Threading.Thread.Sleep(400);

            foreach (char c in keys)
            {
                Interop.SendAlt(c);
                System.Threading.Thread.Sleep(350);
            }

            Log.Write($"sendalt '{keys}' -> {target}  hwnd=0x{hwnd.ToInt64():X}  foreground={fg}");
            return 0;
        }

        // --postkey F13 --to xournalpp ：用 PostMessage 把按键直接投给窗口，不依赖前台焦点
        if (args.Contains("--postkey", StringComparer.OrdinalIgnoreCase))
        {
            int si = Array.FindIndex(args, a => a.Equals("--postkey", StringComparison.OrdinalIgnoreCase));
            if (si < 0 || si + 1 >= args.Length) { Log.Write("--postkey 需要一个参数"); return 2; }
            int vk = AppConfig.NameToVk(args[si + 1], 0);
            if (vk == 0) { Log.Write("--postkey 无法识别按键: " + args[si + 1]); return 2; }

            string target = "xournalpp";
            int ti = Array.FindIndex(args, a => a.Equals("--to", StringComparison.OrdinalIgnoreCase));
            if (ti >= 0 && ti + 1 < args.Length) target = args[ti + 1];

            IntPtr hwnd = Interop.FindMainWindow(target);
            bool ok = Interop.PostKey(hwnd, vk);
            Log.Write($"postkey vk=0x{vk:X} -> {target} hwnd=0x{hwnd.ToInt64():X} posted={ok}");
            return 0;
        }

        // --shot out.png ：整屏截图（本机 Add-Type 被禁，系统 PowerShell 截不了图，只能靠这里）
        if (args.Contains("--shot", StringComparer.OrdinalIgnoreCase))
        {
            int si = Array.FindIndex(args, a => a.Equals("--shot", StringComparison.OrdinalIgnoreCase));
            string outPng = (si >= 0 && si + 1 < args.Length && !args[si + 1].StartsWith("--"))
                ? args[si + 1] : Path.Combine(baseDir, "shot.png");
            try
            {
                var bounds = System.Windows.Forms.Screen.PrimaryScreen.Bounds;
                using var bmp = new Bitmap(bounds.Width, bounds.Height);
                using (var g = Graphics.FromImage(bmp))
                    g.CopyFromScreen(bounds.X, bounds.Y, 0, 0, new System.Drawing.Size(bounds.Width, bounds.Height));
                bmp.Save(outPng, System.Drawing.Imaging.ImageFormat.Png);
                Log.Write($"shot saved -> {outPng}  {bounds.Width}x{bounds.Height}");
                return 0;
            }
            catch (Exception ex) { Log.Write("shot failed: " + ex.Message); return 1; }
        }

        // --sendkey ctrl+alt+f12 --to xournalpp ：发任意组合键
        if (args.Contains("--sendkey", StringComparer.OrdinalIgnoreCase))
        {
            int si = Array.FindIndex(args, a => a.Equals("--sendkey", StringComparison.OrdinalIgnoreCase));
            if (si < 0 || si + 1 >= args.Length) { Log.Write("--sendkey 需要一个参数"); return 2; }
            string spec = args[si + 1];

            string target = "xournalpp";
            int ti = Array.FindIndex(args, a => a.Equals("--to", StringComparison.OrdinalIgnoreCase));
            if (ti >= 0 && ti + 1 < args.Length) target = args[ti + 1];

            bool c = false, a2 = false, sh = false, w = false;
            int vk = 0;
            foreach (string part in spec.Split('+'))
            {
                string t = part.Trim().ToLowerInvariant();
                if (t == "ctrl" || t == "control") c = true;
                else if (t == "alt") a2 = true;
                else if (t == "shift") sh = true;
                else if (t == "win") w = true;
                else vk = AppConfig.NameToVk(t, 0);
            }
            if (vk == 0) { Log.Write("--sendkey 无法识别按键: " + spec); return 2; }

            IntPtr hwnd = Interop.FindMainWindow(target);
            bool fg = Interop.ForceForeground(hwnd);
            System.Threading.Thread.Sleep(400);

            IntPtr actual = Interop.ForegroundWindow();
            uint inserted = Interop.SendCombo(c, a2, sh, w, vk);

            Log.Write($"sendkey '{spec}' (vk=0x{vk:X}) -> {target}  " +
                      $"targetHwnd=0x{hwnd.ToInt64():X}  setOk={fg}  " +
                      $"actualFg=0x{actual.ToInt64():X}  focusStuck={(actual == hwnd)}  " +
                      $"sendInput_inserted={inserted}  lastErr={Interop.LastWin32Error}");
            return 0;
        }

        // 单实例互斥：用重试兜住"重启"场景——旧实例在 Restart() 里先起新进程再 Shutdown，
        // 新进程此刻还拿不到互斥体，稍等它退出即可（最多约 5s）。
        Mutex appMutex = null;
        bool owned = false;
        for (int attempt = 0; attempt < 25 && !owned; attempt++)
        {
            var m = new Mutex(true, MutexName, out bool created);
            if (created) { appMutex = m; owned = true; break; }
            m.Close();   // 没拿到所有权：只关闭本引用的句柄，不会释放别人的锁、也不抛异常
            Thread.Sleep(200);
        }
        if (!owned)
        {
            Log.Write("another instance is already running; exit");
            return 0;
        }
        _appMutex = appMutex;   // 持有到进程退出，保证单实例（进程退出时 OS 自动释放）

        var cfg = AppConfig.Load(Path.Combine(baseDir, "xpPenPad.ini"), "");
        cfg.PresetsPath = ResolvePresets(cfg.PresetsPath, baseDir);
        Log.Write($"--- start --- target={cfg.Target} presets={cfg.PresetsPath}");
        Log.Write($"hotkeys  pan  = {ComboText(cfg.PanCtrl, cfg.PanAlt, cfg.PanShift, cfg.PanWin, cfg.PanVk)}");
        Log.Write($"hotkeys  menu = {ComboText(cfg.MenuCtrl, cfg.MenuAlt, cfg.MenuShift, cfg.MenuWin, cfg.MenuVk)}");
        RawPenTracker.LogMonitors();

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };

        var radial = new RadialWindow();
        var controller = new PadController(cfg, radial);

        // 原始输入：直接向驱动要笔的位置，不依赖系统光标。
        RawPenTracker tracker = null;
        try
        {
            tracker = new RawPenTracker { DeviceFilter = cfg.RawDevice, Verbose = cfg.DebugRaw };

            // 笔的原始坐标量程。留 0 = 还没标定，只把原始值记进日志，不做换算。
            var rawIni = Ini.Read(Path.Combine(baseDir, "xpPenPad.ini"));
            tracker.RawMaxX = int.TryParse(Ini.Get(rawIni, "behavior", "pen_raw_max_x", "0"), out int rmx) ? rmx : 0;
            tracker.RawMaxY = int.TryParse(Ini.Get(rawIni, "behavior", "pen_raw_max_y", "0"), out int rmy) ? rmy : 0;
            tracker.RawMinX = int.TryParse(Ini.Get(rawIni, "behavior", "pen_raw_min_x", "0"), out int rnx) ? rnx : 0;
            tracker.RawMinY = int.TryParse(Ini.Get(rawIni, "behavior", "pen_raw_min_y", "0"), out int rny) ? rny : 0;
            if (tracker.RawMaxX > 0 && tracker.RawMaxY > 0)
                Log.Write($"raw mapping -> X[{tracker.RawMinX}..{tracker.RawMaxX}] " +
                          $"Y[{tracker.RawMinY}..{tracker.RawMaxY}]");

            PenSource.Tracker = tracker;
            PenSource.Mode = cfg.PenSource;
            PenSource.OffsetX = cfg.PenOffsetX;
            PenSource.OffsetY = cfg.PenOffsetY;
            controller.CursorProvider = PenSource.Get;
            Log.Write($"pen source = {cfg.PenSource} offset=({cfg.PenOffsetX},{cfg.PenOffsetY}) " +
                      $"filter='{cfg.RawDevice}'");
        }
        catch (Exception ex)
        {
            Log.Write("!! rawinput 没能注册，退回系统光标: " + ex.Message);
        }

        // --sniff：把每个原始输入事件写进日志，用来看清笔到底是哪种设备
        if (args.Contains("--sniff", StringComparer.OrdinalIgnoreCase) && tracker != null)
        {
            tracker.Verbose = true;
            Log.Write("SNIFF mode: raw input events will be logged");
        }

        // --dry：正常驻留但只记录、不真的发键，用于验证钩子链路
        if (args.Contains("--dry", StringComparer.OrdinalIgnoreCase))
        {
            controller.DryRun = true;
            Log.Write("DRY RUN mode: keys will be logged, not sent");
        }

        controller.ReloadPresets();

        if (cfg.DebugShot)
        {
            string shotPath = Path.Combine(baseDir, "menu_shot.png");
            controller.OnMenuPositioned = (mx, my) =>
            {
                try
                {
                    var vs = System.Windows.Forms.SystemInformation.VirtualScreen;
                    using var bmp = new Bitmap(vs.Width, vs.Height);
                    using (var g = Graphics.FromImage(bmp))
                    {
                        g.CopyFromScreen(vs.X, vs.Y, 0, 0, new System.Drawing.Size(vs.Width, vs.Height));
                        int cx = mx - vs.X, cy = my - vs.Y;
                        using var pen = new System.Drawing.Pen(System.Drawing.Color.Red, 3);
                        g.DrawLine(pen, cx - 24, cy, cx + 24, cy);
                        g.DrawLine(pen, cx, cy - 24, cx, cy + 24);
                        using var f = new System.Drawing.Font("Consolas", 16);
                        g.DrawString($"{mx},{my}", f, System.Drawing.Brushes.Red, cx + 16, cy + 14);

                        // 另一个坐标源：红色十字 = 圆盘实际所在，蓝色方块 = 另一个源认为的笔尖。
                        // 两个重合 = 位置没问题；差出距离 = 说明到底该信哪一个。
                        var (_, _, ox, oy, rawOk) = PenSource.Both();
                        if (rawOk && (ox != mx || oy != my))
                        {
                            int bx = ox - vs.X, by = oy - vs.Y;
                            using var bp = new System.Drawing.Pen(System.Drawing.Color.DodgerBlue, 3);
                            g.DrawRectangle(bp, bx - 14, by - 14, 28, 28);
                            g.DrawLine(pen, cx, cy, bx, by);
                            g.DrawString($"other {ox},{oy}", f, System.Drawing.Brushes.DodgerBlue, bx + 18, by + 14);
                        }
                    }
                    bmp.Save(shotPath, System.Drawing.Imaging.ImageFormat.Png);
                    Log.Write("debug shot -> " + shotPath);
                }
                catch (Exception ex) { Log.Write("debug shot failed: " + ex.Message); }
            };
            Log.Write("debug_shot enabled -> " + shotPath);
        }

        if (controller.Sectors.Count == 0)
            Log.Write("!! 没读到任何扇区，先检查 presets.ini 路径");

        Interop.OnKey = controller.OnKey;
        if (!Interop.Install())
            Log.Write("!! 键盘钩子安装失败，可能是权限或安全软件拦截");
        else
            Log.Write("keyboard hook installed");

        controller.Start();

        using var tray = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Visible = true,
            Text = "XpPenPad · 数位板笔控",
        };

        var menu = new ContextMenuStrip();
        var miStatus = new ToolStripMenuItem("…") { Enabled = false };
        var miRotate = new ToolStripMenuItem("当前预设");
        var miReload = new ToolStripMenuItem("重载预设");
        var miOpen = new ToolStripMenuItem("打开配置文件夹");
        var miConfig = new ToolStripMenuItem("设置…");
        var miPause = new ToolStripMenuItem("暂停");
        var miExit = new ToolStripMenuItem("退出");

        miRotate.Click += (_, _) =>
        {
            if (controller.Sectors.Count == 0) return;
            var items = controller.Sectors
                .Select(s => new ToolStripMenuItem($"Alt+{s.Index}  {s.Label}  [{s.Tool} {s.Size} {(s.Color.Length > 0 ? "#" + s.Color : "")}]"))
                .ToArray();
            new ContextMenuStrip().Items.AddRange(items);
            var dlg = new Form
            {
                Text = "当前预设",
                Width = 380,
                Height = 60 + items.Length * 24,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                StartPosition = FormStartPosition.CenterScreen,
                MaximizeBox = false,
                MinimizeBox = false,
            };
            var lb = new ListBox { Dock = DockStyle.Fill };
            foreach (var s in controller.Sectors)
                lb.Items.Add($"Alt+{s.Index}   {s.Label}   {s.Tool} {s.Size} {(s.Color.Length > 0 ? "#" + s.Color : "")}");
            dlg.Controls.Add(lb);
            dlg.ShowDialog();
        };

        miReload.Click += (_, _) => { controller.ReloadPresets(); miStatus.Text = controller.StatusText(); };

        miOpen.Click += (_, _) =>
        {
            try
            {
                string dir = Path.GetDirectoryName(cfg.PresetsPath);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                    System.Diagnostics.Process.Start("explorer.exe", dir);
            }
            catch (Exception ex) { Log.Write("open folder failed: " + ex.Message); }
        };

        miConfig.Click += (_, _) =>
        {
            try
            {
                string iniPath = Path.Combine(baseDir, "xpPenPad.ini");
                new ConfigForm(iniPath) { PresetsSaved = () => controller.ReloadPresets() }.ShowDialog();
            }
            catch (Exception ex) { Log.Write("config window failed: " + ex.Message); }
        };

        miPause.Click += (_, _) =>
        {
            controller.Paused = !controller.Paused;
            miPause.Text = controller.Paused ? "继续" : "暂停";
            miPause.Checked = controller.Paused;
            miStatus.Text = controller.Paused ? "已暂停" : controller.StatusText();
            Log.Write(controller.Paused ? "paused" : "resumed");
        };

        miExit.Click += (_, _) => { Log.Write("exit by user"); app.Shutdown(); };

        menu.Items.AddRange(new ToolStripItem[]
        {
            miStatus, new ToolStripSeparator(), miRotate, miReload, miOpen, miConfig,
            new ToolStripSeparator(), miPause, miExit,
        });

        tray.ContextMenuStrip = menu;
        miStatus.Text = controller.StatusText();

        try
        {
            app.Run();
        }
        finally
        {
            controller.Dispose();
            Interop.Uninstall();
            tray.Visible = false;
            Log.Write("--- stopped ---");
        }

        return 0;
    }

    private static string ComboText(bool ctrl, bool alt, bool shift, bool win, int vk)
    {
        var sb = new System.Text.StringBuilder();
        if (ctrl) sb.Append("Ctrl+");
        if (alt) sb.Append("Alt+");
        if (shift) sb.Append("Shift+");
        if (win) sb.Append("Win+");
        sb.Append(AppConfig.VkToName(vk));
        return sb.ToString();
    }

    /// <summary>
    /// 把圆盘画出来并截图，用于在没有数位板/没装 Xournal++ 的情况下先看外观。
    /// </summary>
    private static int Preview(string baseDir, string outPng)
    {
        var cfg = new AppConfig { PresetsPath = ResolvePresets("", baseDir) };
        var sectors = PresetStore.Load(cfg.PresetsPath);
        if (sectors.Count == 0) { Log.Write("preview: no sectors"); return 1; }

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };

        int cx = 700, cy = 500;
        const int Half = 250;

        // 先铺一张"白色文档页"当背景，截图才有参考意义
        var page = new System.Windows.Window
        {
            WindowStyle = System.Windows.WindowStyle.None,
            Background = System.Windows.Media.Brushes.White,
            Topmost = true,
            ShowInTaskbar = false,
            ShowActivated = false,
            ResizeMode = System.Windows.ResizeMode.NoResize,
            Width = Half * 2,
            Height = Half * 2,
            Left = cx - Half,
            Top = cy - Half,
        };
        var pageCanvas = new System.Windows.Controls.Canvas();
        page.Content = pageCanvas;
        for (int i = 0; i < 14; i++)
        {
            var line = new System.Windows.Shapes.Rectangle
            {
                Width = (i % 4 == 3) ? 180 : 380,
                Height = 7,
                RadiusX = 3,
                RadiusY = 3,
                Fill = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0xDE, 0xE3, 0xEA)),
            };
            System.Windows.Controls.Canvas.SetLeft(line, 48);
            System.Windows.Controls.Canvas.SetTop(line, 34 + i * 32);
            pageCanvas.Children.Add(line);
        }
        page.Show();

        var radial = new RadialWindow();
        radial.ShowAt(cx, cy, sectors);
        radial.Highlight(3);   // 高亮一格，截图里能看出选中态

        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(900),
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            try
            {
                using var bmp = new Bitmap(Half * 2, Half * 2);
                using (var g = Graphics.FromImage(bmp))
                    g.CopyFromScreen(cx - Half, cy - Half, 0, 0, new System.Drawing.Size(Half * 2, Half * 2));
                bmp.Save(outPng, System.Drawing.Imaging.ImageFormat.Png);
                Log.Write("preview saved -> " + outPng);
            }
            catch (Exception ex) { Log.Write("preview capture failed: " + ex.Message); }
            app.Shutdown();
        };
        timer.Start();
        app.Run();
        return 0;
    }

    /// <summary>
    /// presets.ini 的位置，优先级：
    ///   1) xpPenPad.ini 里显式写的
    ///   2) %APPDATA%\xournalpp\plugins\drawboard-pen\presets.ini （插件真正读取的那份）
    ///   3) 本工具目录旁边的 ..\plugins\drawboard-pen\presets.ini
    /// </summary>
    private static string ResolvePresets(string configured, string baseDir)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(configured)) candidates.Add(configured);

        // 实测：Xournal++ 的 Windows 版把配置放在 %LOCALAPPDATA%\xournalpp（不是 Roaming）
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        candidates.Add(Path.Combine(localAppData, "xournalpp", "plugins", "drawboard-pen", "presets.ini"));
        candidates.Add(Path.Combine(roaming, "xournalpp", "plugins", "drawboard-pen", "presets.ini"));
        candidates.Add(Path.GetFullPath(Path.Combine(baseDir, "..", "plugins", "drawboard-pen", "presets.ini")));
        candidates.Add(Path.GetFullPath(Path.Combine(baseDir, "..", "..", "plugins", "drawboard-pen", "presets.ini")));

        foreach (string c in candidates)
        {
            if (File.Exists(c))
            {
                Log.Write("presets resolved -> " + c);
                return c;
            }
        }

        Log.Write("!! presets.ini 未找到，将使用第一个候选路径（可能是空的）");
        return candidates[^1];
    }

    // ------------------------------------------------------------ 自检

    /// <summary>
    /// 不装驱动、不连数位板也能验证状态机与几何：
    ///   只跑状态迁移，把"本该发给 Xournal++ 的键"写进日志。
    /// </summary>
    private static int SelfTest(string baseDir)
    {
        Log.Write("=== SELF TEST ===");

        var cfg = new AppConfig { Target = "*", Threshold = 26, PanVk = 0x70, MenuVk = 0x71 };
        cfg.PresetsPath = ResolvePresets("", baseDir);

        var controller = new PadController(cfg, null) { DryRun = true };

        int cx = 1000, cy = 700;
        controller.CursorProvider = () => (cx, cy);
        controller.ReloadPresets();

        Log.Write($"sectors = {controller.Sectors.Count}");
        foreach (var s in controller.Sectors)
            Log.Write($"  [{s.Index}] {s.Label}  tool={s.Tool} size={s.Size} color={s.Color}");

        void Step(string name) => Log.Write("-- " + name + " --");

        Step("1. 按一下侧键1 -> 切换式平移，应发 Ctrl+Alt+F13（dry）切手型");
        cx = 1000; cy = 700;
        controller.TestKey(cfg.PanVk, true);
        controller.TestKey(cfg.PanVk, false);
        controller.TestTick();

        Step("2. 点一下侧键2 -> 打开圆盘，此时不该发键");
        cx = 1000; cy = 700;
        controller.TestKey(cfg.MenuVk, true);

        Step("3. 不动，再点一下 -> 同一位置连点 = 切橡皮（应发该橡皮扇区）");
        controller.TestTick();
        controller.TestKey(cfg.MenuVk, true);

        Step("4. 打开后向右移动 150px，再点一下 -> 应命中第3格");
        cx = 1000; cy = 700;
        controller.TestKey(cfg.MenuVk, true);
        cx = 1150;
        controller.TestTick();
        controller.TestKey(cfg.MenuVk, true);

        Step("5. 向下拖 -> 第5格");
        cx = 1000; cy = 700;
        controller.TestKey(cfg.MenuVk, true);
        cy = 850;
        controller.TestTick();
        controller.TestKey(cfg.MenuVk, true);

        Step("6. 向左拖 -> 第7格");
        cx = 1000; cy = 700;
        controller.TestKey(cfg.MenuVk, true);
        cx = 850;
        controller.TestTick();
        controller.TestKey(cfg.MenuVk, true);

        Step("7. 只移动到内圆死区 35px，再点一下 -> 回到切橡皮，不该选格");
        cx = 1000; cy = 700;
        controller.TestKey(cfg.MenuVk, true);
        cx = 1035;
        controller.TestTick();
        controller.TestKey(cfg.MenuVk, true);

        Step("8. 打开后移动，按 Esc -> 关闭且不发键");
        cx = 1000; cy = 700;
        controller.TestKey(cfg.MenuVk, true);
        cx = 1150;
        controller.TestTick();
        controller.TestKey(Interop.VK_ESCAPE, true);

        Step("9. 向上拖 -> 第1格");
        cx = 1000; cy = 700;
        controller.TestKey(cfg.MenuVk, true);
        cy = 550;
        controller.TestTick();
        controller.TestKey(cfg.MenuVk, true);

        Step("10. 配置模型 ini 往返一致性（不影响真实配置）");
        string tmp = Path.Combine(Path.GetTempPath(), "xp_test_cfg_" + Guid.NewGuid().ToString("N") + ".ini");
        File.Copy(Path.Combine(baseDir, "xpPenPad.ini"), tmp, true);
        var cm = ConfigModel.FromIni(tmp);
        cm.PanKey = "F13"; cm.Threshold = 99; cm.PenSource = "raw";
        cm.RawMaxX = 12345; cm.Target = "notepad.exe"; cm.DebugRaw = !cm.DebugRaw;
        cm.SwapButtons = true;
        ConfigModel.ApplyToIni(tmp, cm);
        var cm2 = ConfigModel.FromIni(tmp);
        bool cfgOk = cm2.PanKey == "F13" && cm2.Threshold == 99 && cm2.PenSource == "raw"
                     && cm2.RawMaxX == 12345 && cm2.Target == "notepad.exe" && cm2.DebugRaw == cm.DebugRaw
                     && cm2.SwapButtons;
        Log.Write("  cfg roundtrip " + (cfgOk ? "PASS" : "FAIL")
                  + "  PanKey=" + cm2.PanKey + " thr=" + cm2.Threshold + " src=" + cm2.PenSource
                  + " rmaxx=" + cm2.RawMaxX + " dbgRaw=" + cm2.DebugRaw);
        File.Delete(tmp);
        if (!cfgOk) { Log.Write("!! config roundtrip FAILED"); return 1; }

        Step("11. 配置窗口可构造（不弹窗，仅验证 UI 代码不抛异常）");
        try
        {
            var f = new ConfigForm(Path.Combine(baseDir, "xpPenPad.ini"));
            Log.Write("  config form constructed OK (title=" + f.Text + ")");
        }
        catch (Exception ex)
        {
            Log.Write("!! config form construction FAILED: " + ex.Message);
            return 1;
        }

        Step("12. 预设 ini 往返（扇区 label/tool/size/color 写入后可被 PresetStore 读回）");
        string preSrc = cfg.PresetsPath;
        if (!File.Exists(preSrc))
        {
            Log.Write("  presets.ini not found at " + preSrc + " -> SKIP");
        }
        else
        {
            string preTmp = Path.Combine(Path.GetTempPath(), "xp_test_pre_" + Guid.NewGuid().ToString("N") + ".ini");
            File.Copy(preSrc, preTmp, true);
            Ini.UpdateKey(preTmp, "sector1", "label", "测试格");
            Ini.UpdateKey(preTmp, "sector1", "tool", "highlighter");
            Ini.UpdateKey(preTmp, "sector1", "size", "veryThick");
            Ini.UpdateKey(preTmp, "sector1", "color", "abcdef");
            var secs = PresetStore.Load(preTmp);
            var s1 = secs.FirstOrDefault(s => s.Index == 1);
            bool preOk = s1 != null && s1.Label == "测试格" && s1.Tool == "highlighter"
                         && s1.Size == "veryThick" && s1.Color == "abcdef";
            Log.Write("  preset roundtrip " + (preOk ? "PASS" : "FAIL")
                      + "  label=" + (s1?.Label ?? "?") + " tool=" + (s1?.Tool ?? "?")
                      + " size=" + (s1?.Size ?? "?") + " color=" + (s1?.Color ?? "?"));
            File.Delete(preTmp);
            if (!preOk) { Log.Write("!! preset roundtrip FAILED"); return 1; }
        }

        Log.Write("=== SELF TEST DONE ===");
        return 0;
    }
}
