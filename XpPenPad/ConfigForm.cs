using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Windows.Forms;

namespace XpPenPad;

/// <summary>
/// 配置模型：与 xpPenPad.ini 的字段一一对应。
/// 纯数据 + 静态读写，方便 SelfTest 在不弹窗的情况下校验 ini 往返一致性。
/// </summary>
internal sealed class ConfigModel
{
    // 热键
    public bool PanCtrl = true, PanAlt = true, PanShift = true, PanWin = false;
    public string PanKey = "F3";
    public bool MenuCtrl = true, MenuAlt = true, MenuShift = true, MenuWin = false;
    public string MenuKey = "F4";

    // 行为
    public string Target = "xournalpp.exe";
    public int Threshold = 26;
    public string PenSource = "auto";          // auto | raw | cursor
    public int PenOffsetX = 0, PenOffsetY = 0;
    public string RawDevice = "";
    public bool DebugKeys = true, DebugShot = false, DebugRaw = false;
    public bool SwapButtons = false;           // 互换侧键1/2 的功能

    // 标定（笔原始坐标量程）
    public int RawMinX = 0, RawMinY = 0, RawMaxX = 32767, RawMaxY = 32767;

    // 路径
    public string Presets = "";

    public static ConfigModel FromIni(string path)
    {
        var m = new ConfigModel();
        var ini = Ini.Read(path);
        m.PanCtrl  = B(ini, "hotkeys", "pan_ctrl",  m.PanCtrl);
        m.PanAlt   = B(ini, "hotkeys", "pan_alt",   m.PanAlt);
        m.PanShift = B(ini, "hotkeys", "pan_shift", m.PanShift);
        m.PanWin   = B(ini, "hotkeys", "pan_win",   m.PanWin);
        m.PanKey   = Ini.Get(ini, "hotkeys", "pan_key", m.PanKey);
        m.MenuCtrl  = B(ini, "hotkeys", "menu_ctrl",  m.MenuCtrl);
        m.MenuAlt   = B(ini, "hotkeys", "menu_alt",   m.MenuAlt);
        m.MenuShift = B(ini, "hotkeys", "menu_shift", m.MenuShift);
        m.MenuWin   = B(ini, "hotkeys", "menu_win",   m.MenuWin);
        m.MenuKey   = Ini.Get(ini, "hotkeys", "menu_key", m.MenuKey);

        m.Target      = Ini.Get(ini, "behavior", "target", m.Target);
        m.Threshold   = I(ini, "behavior", "threshold", m.Threshold);
        m.PenSource   = Ini.Get(ini, "behavior", "pen_source", m.PenSource);
        m.PenOffsetX  = I(ini, "behavior", "pen_offset_x", m.PenOffsetX);
        m.PenOffsetY  = I(ini, "behavior", "pen_offset_y", m.PenOffsetY);
        m.RawDevice   = Ini.Get(ini, "behavior", "raw_device", m.RawDevice);
        m.DebugKeys   = B(ini, "behavior", "debug_keys", m.DebugKeys);
        m.DebugShot   = B(ini, "behavior", "debug_shot", m.DebugShot);
        m.DebugRaw    = B(ini, "behavior", "debug_raw", m.DebugRaw);
        m.SwapButtons = B(ini, "behavior", "swap_buttons", m.SwapButtons);
        m.RawMinX     = I(ini, "behavior", "pen_raw_min_x", m.RawMinX);
        m.RawMinY     = I(ini, "behavior", "pen_raw_min_y", m.RawMinY);
        m.RawMaxX     = I(ini, "behavior", "pen_raw_max_x", m.RawMaxX);
        m.RawMaxY     = I(ini, "behavior", "pen_raw_max_y", m.RawMaxY);

        m.Presets     = Ini.Get(ini, "paths", "presets", m.Presets);
        return m;
    }

    public static void ApplyToIni(string path, ConfigModel m)
    {
        Ini.UpdateKey(path, "hotkeys", "pan_ctrl",  m.PanCtrl  ? "1" : "0");
        Ini.UpdateKey(path, "hotkeys", "pan_alt",   m.PanAlt   ? "1" : "0");
        Ini.UpdateKey(path, "hotkeys", "pan_shift", m.PanShift ? "1" : "0");
        Ini.UpdateKey(path, "hotkeys", "pan_win",   m.PanWin   ? "1" : "0");
        Ini.UpdateKey(path, "hotkeys", "pan_key",   m.PanKey);
        Ini.UpdateKey(path, "hotkeys", "menu_ctrl",  m.MenuCtrl  ? "1" : "0");
        Ini.UpdateKey(path, "hotkeys", "menu_alt",   m.MenuAlt   ? "1" : "0");
        Ini.UpdateKey(path, "hotkeys", "menu_shift", m.MenuShift ? "1" : "0");
        Ini.UpdateKey(path, "hotkeys", "menu_win",   m.MenuWin   ? "1" : "0");
        Ini.UpdateKey(path, "hotkeys", "menu_key",   m.MenuKey);

        Ini.UpdateKey(path, "behavior", "target",      m.Target);
        Ini.UpdateKey(path, "behavior", "threshold",   m.Threshold.ToString());
        Ini.UpdateKey(path, "behavior", "pen_source",  m.PenSource);
        Ini.UpdateKey(path, "behavior", "pen_offset_x", m.PenOffsetX.ToString());
        Ini.UpdateKey(path, "behavior", "pen_offset_y", m.PenOffsetY.ToString());
        Ini.UpdateKey(path, "behavior", "raw_device",  m.RawDevice);
        Ini.UpdateKey(path, "behavior", "debug_keys",  m.DebugKeys ? "1" : "0");
        Ini.UpdateKey(path, "behavior", "debug_shot",  m.DebugShot ? "1" : "0");
        Ini.UpdateKey(path, "behavior", "debug_raw",   m.DebugRaw  ? "1" : "0");
        Ini.UpdateKey(path, "behavior", "swap_buttons", m.SwapButtons ? "1" : "0");
        Ini.UpdateKey(path, "behavior", "pen_raw_min_x", m.RawMinX.ToString());
        Ini.UpdateKey(path, "behavior", "pen_raw_min_y", m.RawMinY.ToString());
        Ini.UpdateKey(path, "behavior", "pen_raw_max_x", m.RawMaxX.ToString());
        Ini.UpdateKey(path, "behavior", "pen_raw_max_y", m.RawMaxY.ToString());

        Ini.UpdateKey(path, "paths", "presets", m.Presets);
    }

    private static bool B(Dictionary<string, Dictionary<string, string>> ini, string s, string k, bool fb)
    {
        var v = Ini.Get(ini, s, k, "").ToLowerInvariant();
        if (v == "1" || v == "true" || v == "yes") return true;
        if (v == "0" || v == "false" || v == "no") return false;
        return fb;
    }

    private static int I(Dictionary<string, Dictionary<string, string>> ini, string s, string k, int fb)
        => int.TryParse(Ini.Get(ini, s, k, ""), out int v) ? v : fb;
}

/// <summary>
/// 图形配置窗口（WinForms，与托盘「当前预设」对话框同一套，避免 WPF/WinForms 跨线程坑）。
/// 布局全部用绝对坐标 —— 窗口固定大小，不用 TableLayoutPanel（上一版它把「侧键2 按键」
/// 那行排错了位，绝对定位彻底杜绝）。保存即写 ini；可选重启使配置生效。
/// </summary>
internal sealed class ConfigForm : Form
{
    private const int LabX = 10;      // 标签列
    private const int CtlX = 185;     // 控件列
    private const int RowH = 34;      // 行高

    private readonly string _iniPath;
    private readonly ConfigModel _m;

    private CheckBox _panCtrl, _panAlt, _panShift, _panWin;
    private ComboBox _panKey;
    private CheckBox _menuCtrl, _menuAlt, _menuShift, _menuWin;
    private ComboBox _menuKey;
    private CheckBox _swap;
    private TextBox _target, _rawDevice, _presets;
    private NumericUpDown _threshold, _offX, _offY, _rminx, _rminy, _rmaxx, _rmaxy;
    private ComboBox _penSource;
    private CheckBox _dbgKeys, _dbgShot, _dbgRaw;

    // 圆盘预设编辑
    private static readonly string[] Tools = { "pen","highlighter","eraser","text","selectRect","selectRegion","selectObject","hand","verticalSpace" };
    private static readonly string[] ToolCn = { "钢笔","荧光笔","橡皮","文字","选矩形","选区域","选对象","手形","竖向空白" };
    private static readonly string[] Sizes = { "veryThin","thin","medium","thick","veryThick" };
    private static readonly string[] SizeCn = { "极细","细","中","粗","极粗" };
    private sealed class SecRow
    {
        public Sector Sector;
        public TextBox Label;
        public ComboBox Tool;
        public ComboBox Size;
        public Button ColorBtn;
        public TextBox Hex;
        public bool LabelTouched;   // 用户手动改过名称，则不再自动生成
    }
    private readonly List<SecRow> _secRows = new();
    private bool _loading = true;                    // BuildPresets 构造期间不触发自动命名
    public Action PresetsSaved;                      // 保存预设后让常驻进程热重载（无需重启）

    public ConfigForm(string iniPath)
    {
        _iniPath = iniPath;
        _m = ConfigModel.FromIni(iniPath);

        Text = "XpPenPad 设置";
        ClientSize = new Size(486, 470);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;

        var tabs = new TabControl
        {
            Left = 6, Top = 6,
            Width = 474, Height = 416,
        };

        var tHot = new TabPage("热键");
        BuildHotkeys(tHot);
        var tBeh = new TabPage("行为");
        BuildBehavior(tBeh);
        var tCal = new TabPage("标定");
        BuildCalibration(tCal);
        var tPre = new TabPage("圆盘预设");
        BuildPresets(tPre);
        var tDbg = new TabPage("路径 / 调试");
        BuildMisc(tDbg);
        tabs.TabPages.AddRange(new[] { tHot, tBeh, tCal, tPre, tDbg });

        var btnSave = new Button { Text = "保存", Width = 90, Height = 28, Left = 290, Top = 6 };
        var btnCancel = new Button { Text = "取消", Width = 90, Height = 28, Left = 388, Top = 6, DialogResult = DialogResult.Cancel };
        btnSave.Click += OnSave;

        var btnPanel = new Panel { Left = 0, Top = 424, Width = 486, Height = 42 };
        btnPanel.Controls.AddRange(new Control[] { btnCancel, btnSave });

        Controls.Add(tabs);
        Controls.Add(btnPanel);
        AcceptButton = btnSave;
        CancelButton = btnCancel;
    }

    // ---- 绝对定位小工具 ----
    private static Label Lab(string text, int x, int y, int w = 170)
        => new Label { Text = text, Left = x, Top = y + 4, Width = w, Height = 22 };

    private static CheckBox Chk(string text, int x, int y, bool @checked)
        => new CheckBox { Text = text, Left = x, Top = y, AutoSize = true, Checked = @checked };

    private static ComboBox KeyCombo(string sel, int x, int y)
    {
        var cb = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Left = x, Top = y, Width = 150 };
        for (int i = 1; i <= 24; i++) cb.Items.Add("F" + i);
        if (cb.Items.Contains(sel)) cb.SelectedItem = sel; else cb.SelectedIndex = 0;
        return cb;
    }

    private static ComboBox SourceCombo(string sel, int x, int y)
    {
        var cb = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Left = x, Top = y, Width = 150 };
        cb.Items.AddRange(new object[] { "auto", "raw", "cursor" });
        cb.SelectedItem = sel;
        return cb;
    }

    private static NumericUpDown Num(int val, decimal min, decimal max, int x, int y)
        => new NumericUpDown { Left = x, Top = y, Width = 120, Minimum = min, Maximum = max, Value = Math.Max(min, Math.Min(max, val)) };

    private static TextBox Txt(string text, int x, int y, int w = 220)
        => new TextBox { Text = text, Left = x, Top = y, Width = w };

    private void BuildHotkeys(TabPage page)
    {
        int y = 16;

        page.Controls.Add(Lab("侧键1（平移）修饰键", LabX, y));
        _panCtrl  = Chk("Ctrl",  CtlX, y, _m.PanCtrl);
        _panAlt   = Chk("Alt",   CtlX + 60, y, _m.PanAlt);
        _panShift = Chk("Shift", CtlX + 118, y, _m.PanShift);
        _panWin   = Chk("Win",   CtlX + 182, y, _m.PanWin);
        page.Controls.AddRange(new Control[] { _panCtrl, _panAlt, _panShift, _panWin });
        y += RowH;

        page.Controls.Add(Lab("侧键1 按键", LabX, y));
        _panKey = KeyCombo(_m.PanKey, CtlX, y);
        page.Controls.Add(_panKey);
        y += RowH;

        page.Controls.Add(Lab("侧键2（圆盘）修饰键", LabX, y));
        _menuCtrl  = Chk("Ctrl",  CtlX, y, _m.MenuCtrl);
        _menuAlt   = Chk("Alt",   CtlX + 60, y, _m.MenuAlt);
        _menuShift = Chk("Shift", CtlX + 118, y, _m.MenuShift);
        _menuWin   = Chk("Win",   CtlX + 182, y, _m.MenuWin);
        page.Controls.AddRange(new Control[] { _menuCtrl, _menuAlt, _menuShift, _menuWin });
        y += RowH;

        page.Controls.Add(Lab("侧键2 按键", LabX, y));
        _menuKey = KeyCombo(_m.MenuKey, CtlX, y);
        page.Controls.Add(_menuKey);
        y += RowH;

        _swap = new CheckBox
        {
            Text = "互换侧键1/2 的功能（侧键1=圆盘，侧键2=平移）",
            Left = LabX, Top = y, AutoSize = true, Checked = _m.SwapButtons,
        };
        page.Controls.Add(_swap);
        y += RowH + 6;
        page.Controls.Add(new Label
        {
            Text = "勾选后功能对调，物理按键不变（驱动里侧键1 仍发 F3、侧键2 仍发 F4）。\n保存并重启后生效。",
            Left = LabX, Top = y, Width = 440, Height = 40,
        });
    }

    private void BuildBehavior(TabPage page)
    {
        int y = 16;
        page.Controls.Add(Lab("目标进程（* = 全局）", LabX, y));
        _target = Txt(_m.Target, CtlX, y);
        page.Controls.Add(_target);
        y += RowH;

        page.Controls.Add(Lab("拖动阈值（px）", LabX, y));
        _threshold = Num(_m.Threshold, 0, 400, CtlX, y);
        page.Controls.Add(_threshold);
        y += RowH;

        page.Controls.Add(Lab("笔坐标来源", LabX, y));
        _penSource = SourceCombo(_m.PenSource, CtlX, y);
        page.Controls.Add(_penSource);
        y += RowH;

        page.Controls.Add(Lab("坐标偏移 X（px）", LabX, y));
        _offX = Num(_m.PenOffsetX, -2000, 2000, CtlX, y);
        page.Controls.Add(_offX);
        y += RowH;

        page.Controls.Add(Lab("坐标偏移 Y（px）", LabX, y));
        _offY = Num(_m.PenOffsetY, -2000, 2000, CtlX, y);
        page.Controls.Add(_offY);
        y += RowH;

        page.Controls.Add(Lab("原始设备过滤（子串）", LabX, y));
        _rawDevice = Txt(_m.RawDevice, CtlX, y);
        page.Controls.Add(_rawDevice);
    }

    private void BuildCalibration(TabPage page)
    {
        int y = 16;
        page.Controls.Add(Lab("原始 X 最小", LabX, y));
        _rminx = Num(_m.RawMinX, 0, 65535, CtlX, y);
        page.Controls.Add(_rminx);
        y += RowH;

        page.Controls.Add(Lab("原始 Y 最小", LabX, y));
        _rminy = Num(_m.RawMinY, 0, 65535, CtlX, y);
        page.Controls.Add(_rminy);
        y += RowH;

        page.Controls.Add(Lab("原始 X 最大", LabX, y));
        _rmaxx = Num(_m.RawMaxX, 0, 65535, CtlX, y);
        page.Controls.Add(_rmaxx);
        y += RowH;

        page.Controls.Add(Lab("原始 Y 最大", LabX, y));
        _rmaxy = Num(_m.RawMaxY, 0, 65535, CtlX, y);
        page.Controls.Add(_rmaxy);
        y += RowH + 8;

        page.Controls.Add(new Label
        {
            Text = "量程默认 0..32767（已定标，勿随意改）。换板子重标：\n" +
                   "XpPenPad.exe --calib 30 扫满数位板 → 取极值回填这里。",
            Left = LabX, Top = y, Width = 440, Height = 44,
        });
    }

    private void BuildMisc(TabPage page)
    {
        int y = 16;
        page.Controls.Add(Lab("预设文件（留空自动）", LabX, y));
        _presets = Txt(_m.Presets, CtlX, y);
        page.Controls.Add(_presets);
        y += RowH;

        _dbgKeys = Chk("记录按键（排查按了没反应）", LabX, y, _m.DebugKeys);
        page.Controls.Add(_dbgKeys);
        y += RowH;

        _dbgShot = Chk("圆盘定位后截屏（menu_shot.png）", LabX, y, _m.DebugShot);
        page.Controls.Add(_dbgShot);
        y += RowH;

        _dbgRaw = Chk("记录每个原始输入事件（很吵）", LabX, y, _m.DebugRaw);
        page.Controls.Add(_dbgRaw);
    }

    private void BuildPresets(TabPage page)
    {
        string pp = ResolvePresetsPath();
        var sectors = PresetStore.Load(pp);

        int hy = 10;
        page.Controls.Add(new Label { Text = "#", Left = 8, Top = hy, Width = 20 });
        page.Controls.Add(new Label { Text = "名称", Left = 30, Top = hy, Width = 88 });
        page.Controls.Add(new Label { Text = "工具", Left = 122, Top = hy, Width = 80 });
        page.Controls.Add(new Label { Text = "粗细", Left = 224, Top = hy, Width = 70 });
        page.Controls.Add(new Label { Text = "颜色", Left = 306, Top = hy, Width = 60 });

        int ry = 32, rh = 30;
        foreach (var sec in sectors)
        {
            var row = new SecRow { Sector = sec };

            row.Label = new TextBox { Text = sec.Label, Left = 30, Top = ry, Width = 88 };
            row.Label.TextChanged += (s, e) => row.LabelTouched = true;
            page.Controls.Add(row.Label);

            row.Tool = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Left = 122, Top = ry, Width = 96 };
            row.Tool.Items.AddRange(ToolCn);
            int ti = Array.IndexOf(Tools, sec.Tool);
            row.Tool.SelectedIndex = ti >= 0 ? ti : 0;
            page.Controls.Add(row.Tool);

            row.Size = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Left = 224, Top = ry, Width = 76 };
            row.Size.Items.AddRange(SizeCn);
            int si = Array.IndexOf(Sizes, sec.Size);
            row.Size.SelectedIndex = si >= 0 ? si : 1;
            page.Controls.Add(row.Size);

            Color c = HexToColor(sec.Color);
            row.ColorBtn = new Button { Left = 306, Top = ry, Width = 26, Height = 22, BackColor = c, FlatStyle = FlatStyle.Flat };
            row.ColorBtn.Click += (s, e) =>
            {
                using var dlg = new ColorDialog { Color = row.ColorBtn.BackColor, FullOpen = true };
                if (dlg.ShowDialog() == DialogResult.OK)
                {
                    row.ColorBtn.BackColor = dlg.Color;
                    row.Hex.Text = ColorToHex(dlg.Color);
                    AutoName(row);
                }
            };
            page.Controls.Add(row.ColorBtn);

            row.Hex = new TextBox { Text = sec.Color, Left = 338, Top = ry, Width = 118 };
            page.Controls.Add(row.Hex);

            // 工具/粗细 改变 -> 名称自动跟随（除非用户手动改过名称）
            row.Tool.SelectedIndexChanged += (s, e) => { UpdateRowEnabled(row); AutoName(row); };
            row.Size.SelectedIndexChanged += (s, e) => AutoName(row);

            _secRows.Add(row);
            UpdateRowEnabled(row);
            ry += rh;
        }

        _loading = false;

        if (sectors.Count == 0)
        {
            page.Controls.Add(new Label
            {
                Text = "未找到预设文件：\n" + pp + "\n\n请先在「路径 / 调试」页指定预设文件，\n或确认 Xournal++ 插件已安装。",
                Left = 10, Top = 40, Width = 440, Height = 80,
            });
        }
        else
        {
            page.Controls.Add(new Label
            {
                Text = "提示：改「工具 / 颜色 / 粗细」时，名称会自动生成；\n想自定义名称，直接改左侧文本框即可（之后改工具不再覆盖）。",
                Left = 8, Top = ry + 4, Width = 460, Height = 40,
            });
        }
    }

    private static void UpdateRowEnabled(SecRow row)
    {
        string t = row.Tool.SelectedIndex >= 0 ? Tools[row.Tool.SelectedIndex] : row.Sector.Tool;
        bool enabled = t != "eraser" && t != "hand";
        row.Size.Enabled = enabled;
        row.ColorBtn.Enabled = enabled;
        row.Hex.Enabled = enabled;
    }

    private string ResolvePresetsPath()
    {
        string p = (_presets != null ? _presets.Text : "").Trim();
        if (!string.IsNullOrEmpty(p)) return p;
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "xournalpp", "plugins", "drawboard-pen", "presets.ini");
    }

    private void SavePresets()
    {
        if (_secRows.Count == 0) return;
        string pp = ResolvePresetsPath();
        try
        {
            foreach (var row in _secRows)
            {
                string sec = "sector" + row.Sector.Index;
                string tool = row.Tool.SelectedIndex >= 0 ? Tools[row.Tool.SelectedIndex] : row.Sector.Tool;
                string size = row.Size.SelectedIndex >= 0 ? Sizes[row.Size.SelectedIndex] : row.Sector.Size;
                Ini.UpdateKey(pp, sec, "label", row.Label.Text.Trim());
                Ini.UpdateKey(pp, sec, "tool", tool);
                if (tool != "eraser" && tool != "hand")
                {
                    Ini.UpdateKey(pp, sec, "size", size);
                    Ini.UpdateKey(pp, sec, "color", row.Hex.Text.Trim());
                }
            }
            Log.Write("presets saved via GUI -> " + pp);
        }
        catch (Exception ex)
        {
            Log.Write("presets save failed: " + ex.Message);
            MessageBox.Show("预设文件写入失败：" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static Color HexToColor(string hex)
    {
        if (hex != null && hex.Length == 6 &&
            int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int rgb))
            return Color.FromArgb(0xFF, (rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
        return Color.Gray;
    }

    private static string ColorToHex(Color c)
        => c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2");

    // 常见颜色的中文名（按 RGB 就近匹配），用于自动生成扇区名称
    private static readonly (string Name, int R, int G, int B)[] Palette =
    {
        ("黑", 0, 0, 0), ("白", 255, 255, 255), ("红", 220, 0, 0), ("深红", 139, 0, 0),
        ("绿", 0, 128, 0), ("深绿", 46, 125, 50), ("蓝", 0, 0, 200), ("深蓝", 21, 101, 192),
        ("黄", 240, 200, 0), ("荧光黄", 255, 224, 130), ("荧光绿", 197, 225, 165),
        ("青", 0, 180, 180), ("橙", 255, 150, 0), ("紫", 140, 0, 140),
        ("粉", 255, 160, 200), ("灰", 130, 130, 130), ("棕", 150, 90, 40),
    };

    private static string ColorName(string hex)
    {
        if (string.IsNullOrEmpty(hex)) return "";
        if (hex.Length == 6 && int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int rgb))
        {
            int r = (rgb >> 16) & 0xFF, g = (rgb >> 8) & 0xFF, b = rgb & 0xFF;
            string best = "";
            int bestD = int.MaxValue;
            foreach (var p in Palette)
            {
                int d = (p.R - r) * (p.R - r) + (p.G - g) * (p.G - g) + (p.B - b) * (p.B - b);
                if (d < bestD) { bestD = d; best = p.Name; }
            }
            return best;
        }
        return hex;   // 不在调色板内则回退显示 hex
    }

    /// <summary>根据 工具/颜色/粗细 生成扇区名称。</summary>
    private static string GenName(SecRow row)
    {
        string tool = row.Tool.SelectedIndex >= 0 ? Tools[row.Tool.SelectedIndex] : row.Sector.Tool;
        if (tool == "eraser") return "橡皮";
        if (tool == "hand") return "手形";
        if (tool == "pen" || tool == "highlighter")
        {
            string cn = ColorName(row.Hex.Text.Trim());
            int si = row.Size.SelectedIndex;
            string sz = si >= 0 ? SizeCn[si] : "";
            string nm = (cn + (sz.Length > 0 ? " " + sz : "")).Trim();
            return nm.Length > 0 ? nm : (tool == "pen" ? "钢笔" : "荧光笔");
        }
        int ti = row.Tool.SelectedIndex >= 0 ? row.Tool.SelectedIndex : 0;
        return ToolCn[ti];
    }

    /// <summary>自动命名：构造期(_loading)与手动改过名称(LabelTouched)时跳过。</summary>
    private void AutoName(SecRow row)
    {
        if (_loading || row.LabelTouched) return;
        row.Label.Text = GenName(row);
    }

    private void OnSave(object sender, EventArgs e)
    {
        _m.PanCtrl = _panCtrl.Checked; _m.PanAlt = _panAlt.Checked;
        _m.PanShift = _panShift.Checked; _m.PanWin = _panWin.Checked;
        _m.PanKey = _panKey.SelectedItem?.ToString() ?? _m.PanKey;
        _m.MenuCtrl = _menuCtrl.Checked; _m.MenuAlt = _menuAlt.Checked;
        _m.MenuShift = _menuShift.Checked; _m.MenuWin = _menuWin.Checked;
        _m.MenuKey = _menuKey.SelectedItem?.ToString() ?? _m.MenuKey;
        _m.SwapButtons = _swap.Checked;

        _m.Target = _target.Text.Trim();
        _m.Threshold = (int)_threshold.Value;
        _m.PenSource = _penSource.SelectedItem?.ToString() ?? _m.PenSource;
        _m.PenOffsetX = (int)_offX.Value; _m.PenOffsetY = (int)_offY.Value;
        _m.RawDevice = _rawDevice.Text.Trim();
        _m.RawMinX = (int)_rminx.Value; _m.RawMinY = (int)_rminy.Value;
        _m.RawMaxX = (int)_rmaxx.Value; _m.RawMaxY = (int)_rmaxy.Value;

        _m.Presets = _presets.Text.Trim();
        _m.DebugKeys = _dbgKeys.Checked; _m.DebugShot = _dbgShot.Checked; _m.DebugRaw = _dbgRaw.Checked;

        try
        {
            ConfigModel.ApplyToIni(_iniPath, _m);
            Log.Write("config saved via GUI -> " + _iniPath);
            SavePresets();
            PresetsSaved?.Invoke();   // 常驻进程热重载预设，圆盘/托盘菜单立即生效
        }
        catch (Exception ex)
        {
            MessageBox.Show("写入配置文件失败：" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        var r = MessageBox.Show(
            "配置已保存。\n预设已即时生效（圆盘与托盘菜单会自动刷新）。\n是否还要立即重启 XpPenPad，使其它配置（热键 / 行为 / 标定）生效？\n（不重启则其它配置下次手动启动时再生效）",
            "配置已保存", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (r == DialogResult.Yes)
            Restart();
        else
            DialogResult = DialogResult.OK;
        Close();
    }

    private void Restart()
    {
        try
        {
            string exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe))
                exe = Path.Combine(AppContext.BaseDirectory, "XpPenPad.exe");
            Log.Write("restart -> " + exe);
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
            System.Windows.Application.Current?.Shutdown();
        }
        catch (Exception ex)
        {
            Log.Write("restart failed: " + ex.Message);
            MessageBox.Show("自动重启失败，请手动重启 XpPenPad。\n" + ex.Message, "提示",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
