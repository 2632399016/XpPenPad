using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Point = System.Windows.Point;
using Size = System.Windows.Size;
using Color = System.Windows.Media.Color;
using Brushes = System.Windows.Media.Brushes;
using FontFamily = System.Windows.Media.FontFamily;
using FontWeights = System.Windows.FontWeights;

namespace XpPenPad;

/// <summary>
/// 圆盘叠加层：无边框、透明、置顶、不抢焦点。
/// 只负责画和响应"高亮第几格"，状态机在 PadController 里。
/// </summary>
internal sealed class RadialWindow : Window
{
    private const double ROuter = 148;   // 外半径（DIP）
    private const double RInner = 46;    // 内半径（DIP），小于这个算中键死区
    private const double LabelR = 100;   // 文字环半径（DIP）
    private const double GapDeg = 2.4;   // 扇区之间的角度缝隙

    private readonly Canvas _canvas = new();
    private readonly List<Path> _paths = new();
    private readonly List<TextBlock> _texts = new();
    private readonly List<Ellipse> _dots = new();

    public int SectorCount { get; private set; }

    public RadialWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        ShowActivated = false;          // 关键：弹出时不抢焦点
        Focusable = false;
        IsHitTestVisible = false;       // 鼠标事件完全不拦
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;

        double size = 2 * (ROuter + 4);
        Width = size;
        Height = size;

        _canvas.Width = size;
        _canvas.Height = size;
        _canvas.Background = Brushes.Transparent;
        Content = _canvas;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // 关键：WPF 的 ShowActivated=false 并不保证窗口永远不激活，
        // 圆盘一显示就把前台抢走，随后目标判定 targetOK 变 false，下一次按键就被丢掉。
        // 必须加 WS_EX_NOACTIVATE 才能做到"看得见但永不夺焦"。
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        int ex = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
        NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE,
                                    ex | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW);

        NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
                                   NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE |
                                   NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
    }

    /// <summary>在屏幕坐标 (xPx, yPx) 处打开圆盘，返回实际生效的中心。</summary>
    public (int cx, int cy) ShowAt(int xPx, int yPx, List<Sector> sectors)
    {
        Build(sectors);

        // 先用 DIP 粗估一次，减少"第一帧闪在别处"的观感
        double scale = DpiScale();
        Left = xPx / scale - Width / 2.0;
        Top = yPx / scale - Height / 2.0;

        if (!IsVisible) Show();
        else Topmost = true;

        // 窗口有了真实矩形之后再用像素精确对位（并按屏幕边界夹住）
        return PositionTo(xPx, yPx);
    }

    /// <summary>
    /// 把圆盘中心对到屏幕坐标 (xPx,yPx)，并夹进显示器工作区保证整个圆盘可见。
    /// 返回实际生效的中心（被夹过的话会和传入值不同，命中判定要用这个）。
    ///
    /// 注意：这里刻意不用 WPF 的 Left/Top（DIP）做换算 —— 在缩放显示器上
    /// DIP↔像素 的换算一旦和窗口自身 DPI 不一致，圆盘就会整体偏移。
    /// 全部走 Win32 像素坐标（GetWindowRect + SetWindowPos）最稳。
    /// </summary>
    public (int cx, int cy) PositionTo(int xPx, int yPx)
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return (xPx, yPx);

        if (!NativeMethods.GetWindowRect(hwnd, out NativeMethods.RECT r)) return (xPx, yPx);
        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        if (w <= 0 || h <= 0) return (xPx, yPx);

        int left = xPx - w / 2;
        int top = yPx - h / 2;

        try
        {
            var wa = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(xPx, yPx)).WorkingArea;
            if (left < wa.Left) left = wa.Left;
            if (top < wa.Top) top = wa.Top;
            if (left + w > wa.Right) left = wa.Right - w;
            if (top + h > wa.Bottom) top = wa.Bottom - h;
        }
        catch { /* 取不到工作区就不夹 */ }

        NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, left, top, 0, 0,
                                   NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE |
                                   NativeMethods.SWP_SHOWWINDOW);

        return (left + w / 2, top + h / 2);
    }

    public void CloseOverlay()
    {
        if (IsVisible) Hide();
    }

    private static double DpiScale()
    {
        try
        {
            uint dpi = NativeMethods.GetDpiForSystem();
            if (dpi >= 72 && dpi <= 480) return dpi / 96.0;
        }
        catch { }
        return 1.0;
    }

    // ------------------------------------------------------------ 几何

    private static Point OnRing(double cx, double cy, double r, double deg)
    {
        double rad = (deg - 90) * Math.PI / 180.0;
        return new Point(cx + r * Math.Cos(rad), cy + r * Math.Sin(rad));
    }

    private static Geometry AnnularSector(double cx, double cy, double r0, double r1,
                                          double a0, double a1)
    {
        Point p0 = OnRing(cx, cy, r1, a0);
        Point p1 = OnRing(cx, cy, r1, a1);
        Point q1 = OnRing(cx, cy, r0, a1);
        Point q0 = OnRing(cx, cy, r0, a0);

        bool large = (a1 - a0) > 180;
        var fig = new PathFigure { StartPoint = p0, IsClosed = true, IsFilled = true };
        fig.Segments.Add(new ArcSegment(p1, new Size(r1, r1), 0, large, SweepDirection.Clockwise, true));
        fig.Segments.Add(new LineSegment(q1, true));
        fig.Segments.Add(new ArcSegment(q0, new Size(r0, r0), 0, large, SweepDirection.Counterclockwise, true));

        var geo = new PathGeometry();
        geo.Figures.Add(fig);
        return geo;
    }

    private void Build(List<Sector> sectors)
    {
        _canvas.Children.Clear();
        _paths.Clear();
        _texts.Clear();
        _dots.Clear();

        double size = Width;
        double c = size / 2.0;
        SectorCount = sectors.Count;
        if (SectorCount == 0) return;

        // 底盘
        var disc = new Ellipse
        {
            Width = 2 * (ROuter + 2),
            Height = 2 * (ROuter + 2),
            Fill = new SolidColorBrush(Color.FromArgb(0xC4, 0x18, 0x1B, 0x20)),
            Stroke = new SolidColorBrush(Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF)),
            StrokeThickness = 1,
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(disc, c - ROuter - 2);
        Canvas.SetTop(disc, c - ROuter - 2);
        _canvas.Children.Add(disc);

        double span = 360.0 / SectorCount;

        for (int i = 0; i < SectorCount; i++)
        {
            double mid = (i + 0.5) * span;
            double a0 = i * span + GapDeg / 2;
            double a1 = (i + 1) * span - GapDeg / 2;

            var path = new Path
            {
                Data = AnnularSector(c, c, RInner, ROuter, a0, a1),
                Fill = Brushes.Transparent,
                Stroke = new SolidColorBrush(Color.FromArgb(0x2A, 0xFF, 0xFF, 0xFF)),
                StrokeThickness = 1,
                IsHitTestVisible = false,
            };
            _canvas.Children.Add(path);
            _paths.Add(path);

            // 颜色小圆点
            Point dot = OnRing(c, c, (RInner + ROuter) / 2.0 - 20, mid);
            var ell = new Ellipse
            {
                Width = 10,
                Height = 10,
                Fill = new SolidColorBrush(sectors[i].Swatch),
                Stroke = new SolidColorBrush(Color.FromArgb(0x66, 0, 0, 0)),
                StrokeThickness = 1,
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(ell, dot.X - 5);
            Canvas.SetTop(ell, dot.Y - 5);
            _canvas.Children.Add(ell);
            _dots.Add(ell);

            // 文字
            Point tp = OnRing(c, c, LabelR, mid);
            var tb = new TextBlock
            {
                Text = sectors[i].Label,
                FontSize = 12.5,
                FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI"),
                Foreground = Brushes.White,
                TextAlignment = TextAlignment.Center,
                Width = 86,
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(tb, tp.X - 43);
            Canvas.SetTop(tb, tp.Y - 9);
            _canvas.Children.Add(tb);
            _texts.Add(tb);
        }

        // 中心提示
        var hint = new TextBlock
        {
            Text = "松开应用",
            FontSize = 10,
            Foreground = new SolidColorBrush(Color.FromArgb(0xAA, 0xFF, 0xFF, 0xFF)),
            Width = RInner * 2,
            TextAlignment = TextAlignment.Center,
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(hint, c - RInner);
        Canvas.SetTop(hint, c - 7);
        _canvas.Children.Add(hint);

        Highlight(0);
    }

    /// <summary>高亮第 idx 格（1-based）。传 0 表示不高亮。</summary>
    public void Highlight(int idx)
    {
        for (int i = 0; i < _paths.Count; i++)
        {
            bool on = (i + 1) == idx;
            _paths[i].Fill = on
                ? new SolidColorBrush(Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF))
                : Brushes.Transparent;
            _paths[i].Stroke = new SolidColorBrush(on
                ? Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)
                : Color.FromArgb(0x2A, 0xFF, 0xFF, 0xFF));
            _texts[i].Foreground = on
                ? new SolidColorBrush(Color.FromRgb(0x10, 0x10, 0x10))
                : new SolidColorBrush(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF));
            _texts[i].FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
        }
    }

    /// <summary>按光标位置算命中哪一格；落在死区返回 0。</summary>
    public int HitTest(int anchorX, int anchorY, int cursorX, int cursorY)
    {
        if (SectorCount == 0) return 0;

        double dx = cursorX - anchorX;
        double dy = cursorY - anchorY;
        double dist = Math.Sqrt(dx * dx + dy * dy);

        double scale = DpiScale();
        if (dist < RInner * scale) return 0;      // 死区
        if (dist > (ROuter + 40) * scale) return 0; // 太远，视为未选中

        double deg = Math.Atan2(dy, dx) * 180.0 / Math.PI + 90.0;
        while (deg < 0) deg += 360;
        while (deg >= 360) deg -= 360;

        double span = 360.0 / SectorCount;
        int idx = (int)(deg / span) + 1;
        if (idx < 1) idx = 1;
        if (idx > SectorCount) idx = SectorCount;
        return idx;
    }
}

internal static class NativeMethods
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern uint GetDpiForSystem();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(IntPtr hwnd);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_NOACTIVATE = 0x08000000;
    public const int WS_EX_TOOLWINDOW = 0x00000080;

    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_SHOWWINDOW = 0x0040;

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowLong")]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetWindowLong")]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    public static int GetWindowLong(IntPtr hWnd, int nIndex)
        => IntPtr.Size == 8 ? (int)GetWindowLongPtr64(hWnd, nIndex).ToInt64() : GetWindowLong32(hWnd, nIndex);

    public static void SetWindowLong(IntPtr hWnd, int nIndex, int value)
    {
        if (IntPtr.Size == 8) SetWindowLongPtr64(hWnd, nIndex, new IntPtr(value));
        else SetWindowLong32(hWnd, nIndex, value);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
                                           int X, int Y, int cx, int cy, uint uFlags);
}
