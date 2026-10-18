using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace XpPenPad;

internal static class Log
{
    private static readonly object Gate = new();
    private static string _path;
    private static StreamWriter _w;

    public static void Init(string path)
    {
        _path = path;
        try
        {
            // 单例句柄 + 自动刷新：避免每次写日志都开关文件（原 AppendAllText 在高频日志时开销很大）
            // FileShare.ReadWrite：允许 --calib 等第二个进程同时追加日志。
            // 注意：不能用 using 包 fs —— using 一退出就会把流关掉；流随 _w 一起释放即可
            var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            _w = new StreamWriter(fs, Encoding.UTF8) { AutoFlush = true };
        }
        catch { _w = null; }
    }

    public static void Write(string msg)
    {
        try
        {
            if (_w == null) return;
            lock (Gate)
                _w.WriteLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + msg);
        }
        catch { /* 日志写不进去也不能影响主流程 */ }
    }
}

/// <summary>极简 INI 解析：只认 [section] 与 key=value，# / ; 为注释。与插件的解析口径一致。</summary>
internal static class Ini
{
    public static Dictionary<string, Dictionary<string, string>> Read(string path)
    {
        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path)) return result;

        string text = File.ReadAllText(path, Encoding.UTF8);
        if (text.Length > 0 && text[0] == '\uFEFF') text = text.Substring(1);

        Dictionary<string, string> cur = null;
        foreach (string raw in text.Split('\n'))
        {
            string s = raw.Trim('\r', ' ', '\t');
            if (s.Length == 0 || s[0] == '#' || s[0] == ';') continue;

            if (s[0] == '[' && s[^1] == ']')
            {
                string sec = s.Substring(1, s.Length - 2).Trim();
                cur = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                result[sec] = cur;
                continue;
            }

            int eq = s.IndexOf('=');
            if (eq > 0 && cur != null)
                cur[s.Substring(0, eq).Trim()] = s.Substring(eq + 1).Trim();
        }
        return result;
    }

    public static string Get(Dictionary<string, Dictionary<string, string>> ini,
                             string section, string key, string fallback)
    {
        if (ini.TryGetValue(section, out var s) && s.TryGetValue(key, out var v) && v.Length > 0)
            return v;
        return fallback;
    }

    /// <summary>定点修改某个 section 下的 key=value，保留注释、其余键值与顺序。
    /// 找不到 key 就在该 section 末尾追加；找不到 section 就在文件末尾新建。</summary>
    public static void UpdateKey(string path, string section, string key, string value)
    {
        var lines = File.Exists(path) ? File.ReadAllLines(path) : Array.Empty<string>();
        var outl = new List<string>();
        bool inSec = false, wrote = false, secSeen = false;

        foreach (var line in lines)
        {
            string s = line.Trim();
            if (s.Length > 0 && s[0] == '[' && s[^1] == ']')
            {
                if (inSec && !wrote) { outl.Add($"{key}={value}"); wrote = true; }
                bool isTarget = string.Equals(s.Substring(1, s.Length - 2).Trim(), section, StringComparison.OrdinalIgnoreCase);
                if (isTarget) { inSec = true; secSeen = true; }
                else inSec = false;
                outl.Add(line);
                continue;
            }
            if (inSec && !wrote && s.Length > 0 && s[0] != '#' && s[0] != ';' &&
                s.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase))
            {
                outl.Add($"{key}={value}");
                wrote = true;
                continue;
            }
            outl.Add(line);
        }

        // 目标段存在但 key 缺失，且它是文件最后一段：在段内（文件末尾）补写
        if (inSec && !wrote) outl.Add($"{key}={value}");

        // 目标段从未出现：在文件末尾新建（注意不能重复追加同名 section，否则 Ini.Read 覆盖时冲掉已写值）
        if (!secSeen)
        {
            if (outl.Count > 0 && outl[^1].Trim().Length > 0) outl.Add("");
            outl.Add($"[{section}]");
            outl.Add($"{key}={value}");
        }

        File.WriteAllLines(path, outl);
    }
}

/// <summary>圆盘的一个扇区。</summary>
internal sealed class Sector
{
    public int Index;          // 1-based
    public string Label = "";
    public string Tool = "pen";
    public string Size = "";
    public string Color = "";

    /// <summary>预览用颜色；拿不到就返回灰。</summary>
    public System.Windows.Media.Color Swatch
    {
        get
        {
            if (Color.Length == 6 &&
                uint.TryParse(Color, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint rgb))
                return System.Windows.Media.Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
            return System.Windows.Media.Color.FromRgb(0x88, 0x88, 0x88);
        }
    }
}

internal static class PresetStore
{
    public static List<Sector> Load(string path)
    {
        var list = new List<Sector>();
        var ini = Ini.Read(path);

        for (int i = 1; i <= 8; i++)
        {
            string sec = "sector" + i;
            if (!ini.TryGetValue(sec, out var kv)) continue;

            list.Add(new Sector
            {
                Index = i,
                Label = kv.TryGetValue("label", out var l) && l.Length > 0 ? l : ("第" + i + "格"),
                Tool  = kv.TryGetValue("tool", out var t) && t.Length > 0 ? t : "pen",
                Size  = kv.TryGetValue("size", out var sz) ? sz : "",
                Color = kv.TryGetValue("color", out var c) ? c.Replace("#", "") : "",
            });
        }
        return list;
    }
}

/// <summary>xpPenPad.ini：进来的两个快捷键、目标进程、触发阈值。</summary>
internal sealed class AppConfig
{
    public bool PanCtrl = true, PanAlt = true, PanShift = true, PanWin = false;
    public int PanVk = 0x70;     // F1

    public bool MenuCtrl = true, MenuAlt = true, MenuShift = true, MenuWin = false;
    public int MenuVk = 0x71;    // F2

    public string Target = "xournalpp.exe";
    public int Threshold = 26;
    public string PresetsPath = "";

    /// <summary>把收到的按键（带修饰键的、以及 F1~F24）写进日志，用于排查"按了没反应"。</summary>
    public bool DebugKeys = true;

    /// <summary>圆盘定位后截一张图（在锚点画红十字）到 menu_shot.png，用于排查位置问题。</summary>
    public bool DebugShot = false;

    /// <summary>
    /// 位置取哪个源：
    ///   auto   = 原始输入有新鲜数据就用它，否则退回系统光标（默认）
    ///   raw    = 只用原始输入
    ///   cursor = 只用系统光标（回到老行为）
    /// 三者都会在日志里同时打印另一个源，方便对照。
    /// </summary>
    public string PenSource = "auto";

    /// <summary>手工校准偏移（像素）。万一坐标系统性偏了，先靠它顶一下。</summary>
    public int PenOffsetX, PenOffsetY;

    /// <summary>原始输入设备名子串过滤（如 "VID_256C"）。空 = 接受任何绝对定位设备。</summary>
    public string RawDevice = "";

    /// <summary>把每个原始输入事件写进日志。很吵，只在排查设备类型时开。</summary>
    public bool DebugRaw = false;

    public static AppConfig Load(string path, string defaultPresets)
    {
        var cfg = new AppConfig();
        var ini = Ini.Read(path);

        cfg.PanCtrl  = Bool(ini, "hotkeys", "pan_ctrl", true);
        cfg.PanAlt   = Bool(ini, "hotkeys", "pan_alt", true);
        cfg.PanShift = Bool(ini, "hotkeys", "pan_shift", true);
        cfg.PanWin   = Bool(ini, "hotkeys", "pan_win", false);
        cfg.PanVk    = Vk(ini, "hotkeys", "pan_key", 0x70);

        cfg.MenuCtrl  = Bool(ini, "hotkeys", "menu_ctrl", true);
        cfg.MenuAlt   = Bool(ini, "hotkeys", "menu_alt", true);
        cfg.MenuShift = Bool(ini, "hotkeys", "menu_shift", true);
        cfg.MenuWin   = Bool(ini, "hotkeys", "menu_win", false);
        cfg.MenuVk    = Vk(ini, "hotkeys", "menu_key", 0x71);

        cfg.Target = Ini.Get(ini, "behavior", "target", cfg.Target);
        cfg.Threshold = int.TryParse(Ini.Get(ini, "behavior", "threshold", "26"), out int th) ? th : 26;
        cfg.DebugKeys = Bool(ini, "behavior", "debug_keys", true);
        cfg.DebugShot = Bool(ini, "behavior", "debug_shot", false);
        cfg.PresetsPath = Ini.Get(ini, "paths", "presets", defaultPresets);

        string src = Ini.Get(ini, "behavior", "pen_source", "auto").Trim().ToLowerInvariant();
        if (src != "raw" && src != "cursor") src = "auto";
        cfg.PenSource = src;
        cfg.PenOffsetX = int.TryParse(Ini.Get(ini, "behavior", "pen_offset_x", "0"), out int ox) ? ox : 0;
        cfg.PenOffsetY = int.TryParse(Ini.Get(ini, "behavior", "pen_offset_y", "0"), out int oy) ? oy : 0;
        cfg.RawDevice = Ini.Get(ini, "behavior", "raw_device", "");
        cfg.DebugRaw = Bool(ini, "behavior", "debug_raw", false);

        // 互换侧键1/2 的功能：只对调 pan/menu 两组（键 + 修饰键），物理按键不动。
        // 在 Load 一次性对调，之后 PadController 用到的 PanVk/MenuVk 全部是互换后的值，无运行时分支。
        if (Bool(ini, "behavior", "swap_buttons", false))
        {
            (cfg.PanCtrl,  cfg.MenuCtrl)  = (cfg.MenuCtrl,  cfg.PanCtrl);
            (cfg.PanAlt,   cfg.MenuAlt)   = (cfg.MenuAlt,   cfg.PanAlt);
            (cfg.PanShift, cfg.MenuShift) = (cfg.MenuShift, cfg.PanShift);
            (cfg.PanWin,   cfg.MenuWin)   = (cfg.MenuWin,   cfg.PanWin);
            (cfg.PanVk,    cfg.MenuVk)    = (cfg.MenuVk,    cfg.PanVk);
            Log.Write("swap_buttons=1 -> side-button functions swapped");
        }
        return cfg;
    }

    private static bool Bool(Dictionary<string, Dictionary<string, string>> ini,
                             string sec, string key, bool fallback)
    {
        string v = Ini.Get(ini, sec, key, "").ToLowerInvariant();
        if (v == "1" || v == "true" || v == "yes") return true;
        if (v == "0" || v == "false" || v == "no") return false;
        return fallback;
    }

    private static int Vk(Dictionary<string, Dictionary<string, string>> ini,
                          string sec, string key, int fallback)
    {
        string v = Ini.Get(ini, sec, key, "");
        if (v.Length == 0) return fallback;
        return NameToVk(v, fallback);
    }

    /// <summary>把 "F1" / "F13" / "A" / "5" 之类转成 VK 码。</summary>
    public static int NameToVk(string name, int fallback)
    {
        name = name.Trim().ToUpperInvariant();
        if (name.Length == 0) return fallback;

        if (name.Length >= 2 && name[0] == 'F' &&
            int.TryParse(name.Substring(1), out int fn) && fn >= 1 && fn <= 24)
            return 0x70 + (fn - 1);

        if (name.Length == 1)
        {
            char c = name[0];
            if (c >= 'A' && c <= 'Z') return c;
            if (c >= '0' && c <= '9') return c;
        }

        return name switch
        {
            "SPACE" => 0x20,
            "TAB" => 0x09,
            "ESC" or "ESCAPE" => 0x1B,
            "INSERT" or "INS" => 0x2D,
            "DELETE" or "DEL" => 0x2E,
            "HOME" => 0x24,
            "END" => 0x23,
            "PAGEUP" or "PGUP" => 0x21,
            "PAGEDOWN" or "PGDN" => 0x22,
            "BACKSPACE" or "BACK" => 0x08,
            "ENTER" or "RETURN" => 0x0D,
            "PAUSE" => 0x13,
            "SCROLLLOCK" => 0x91,
            _ => fallback,
        };
    }

    public static string VkToName(int vk)
    {
        if (vk >= 0x70 && vk <= 0x87) return "F" + (vk - 0x70 + 1);
        if (vk >= 'A' && vk <= 'Z') return ((char)vk).ToString();
        if (vk >= '0' && vk <= '9') return ((char)vk).ToString();
        return vk switch
        {
            0x2D => "INSERT",
            0x2E => "DELETE",
            0x24 => "HOME",
            0x23 => "END",
            0x21 => "PAGEUP",
            0x22 => "PAGEDOWN",
            0x20 => "SPACE",
            0x09 => "TAB",
            0x0D => "ENTER",
            0x13 => "PAUSE",
            0x91 => "SCROLLLOCK",
            _ => "0x" + vk.ToString("X2"),
        };
    }
}
