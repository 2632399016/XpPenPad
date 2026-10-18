using System;
using System.Collections.Generic;
using System.Windows.Threading;

namespace XpPenPad;

internal enum PadState { Idle, Panning, Menu }

/// <summary>
/// 状态机。两种触发源的物理特性完全不同，必须区别对待：
///
/// 1) 平移（侧键1）—— 首选交给 Xournal++ 原生：
///    驱动把笔侧键透传成"笔按键"，XP 的 stylus 按键配置设成手型，
///    按住即平移、落笔自动回工具（StylusInputHandler::changeTool）。
///    这里的 Panning 只是兜底：只有当驱动真的"按住发键"时才生效。
///
/// 2) 圆盘（侧键2）—— 实测高漫驱动按侧键只发一次"按下+抬起"脉冲，
///    系统层面按键立刻就是松开的，所以不可能做"按住拖动"。
///    改成「点一下出圆盘 → 移动笔选格 → 再点一下确认」。
///    在同一位置连点两下（没移动，落在内圆死区）= 切换橡皮。
/// </summary>
internal sealed class PadController : IDisposable
{
    private const int PanHoldMs = 120;      // 按键要持续这么久才算"按住"，否则视为脉冲
    private const int MenuTimeoutMs = 15000; // 圆盘开着不理它会自动关掉（移动笔会重新计时）

    private readonly AppConfig _cfg;
    private readonly RadialWindow _radial;
    private readonly DispatcherTimer _timer;

    private List<Sector> _sectors = new();

    private PadState _state = PadState.Idle;
    private int _anchorX, _anchorY;   // 打开时笔尖的位置
    private int _hitX, _hitY;         // 圆盘实际中心（贴边被夹过的话与 anchor 不同）
    private bool _repositioned;
    private int _highlight;
    private int _activeSector = 1;
    private bool _eraserMode;
    private bool _handOn;

    private bool _panArmed;
    private DateTime _panArmedAt;
    private DateTime _menuOpenedAt;

    public bool Paused;
    public bool DryRun;
    public Func<(int x, int y)> CursorProvider = Interop.Cursor;

    /// <summary>圆盘定位完成后回调 (中心x, 中心y)，用于排错截图。</summary>
    public Action<int, int> OnMenuPositioned;

    public IReadOnlyList<Sector> Sectors => _sectors;

    public PadController(AppConfig cfg, RadialWindow radial)
    {
        _cfg = cfg;
        _radial = radial;

        _timer = new DispatcherTimer(DispatcherPriority.Input)
        {
            Interval = TimeSpan.FromMilliseconds(8),
        };
        _timer.Tick += (_, _) => Tick();
    }

    public void Start() => _timer.Start();
    public void Dispose() { _timer.Stop(); }

    // ------------------------------------------------------------ 配置

    public void ReloadPresets()
    {
        _sectors = PresetStore.Load(_cfg.PresetsPath);
        if (_activeSector > Math.Max(1, _sectors.Count)) _activeSector = 1;
        Log.Write($"reload presets: {_sectors.Count} sectors from {_cfg.PresetsPath}");
    }

    public string StatusText()
        => $"{_sectors.Count} 格  第{_activeSector}格"
           + (_eraserMode ? "（橡皮）" : "")
           + (_handOn ? "（手型）" : "");

    private int EraserSector()
    {
        for (int i = 0; i < _sectors.Count; i++)
            if (string.Equals(_sectors[i].Tool, "eraser", StringComparison.OrdinalIgnoreCase))
                return _sectors[i].Index;
        return 0;
    }

    // ------------------------------------------------------------ 钩子入口

    /// <summary>返回 true 表示吞掉该按键。</summary>
    public bool OnKey(int vk, bool down)
    {
        if (Paused) return false;

        bool ctrl = Interop.IsDown(Interop.VK_CONTROL);
        bool alt = Interop.IsDown(Interop.VK_MENU);
        bool shift = Interop.IsDown(Interop.VK_SHIFT);

        if (down && _cfg.DebugKeys && (ctrl || alt || shift || (vk >= 0x70 && vk <= 0x87)))
        {
            Log.Write($"KEY vk=0x{vk:X}({AppConfig.VkToName(vk)}) ctrl={ctrl} alt={alt} shift={shift} " +
                      $"fg={Interop.ForegroundProcessName()} targetOK={IsTargetActive()}");
        }

        if (!IsTargetActive()) return false;

        if (vk == _cfg.PanVk)
        {
            // 高漫驱动按侧键只发"按下+抬起"的脉冲，系统层面根本没有"按住"这个状态，
            // 所以"按住平移"在这个硬件上做不到。退而求其次：切换式。
            //   按一下 -> F13 -> 插件切手型（此时拖笔就是平移）
            //   再按一下 -> Alt+<当前笔> -> 回到笔
            //   从圆盘选笔也会自动退出手型
            if (down && _state != PadState.Menu)
            {
                _handOn = !_handOn;
                if (_handOn)
                {
                    // 用 Ctrl+Alt+F13 而不是裸 F13：本机实测 <Ctrl><Alt>F12 这类组合
                    // 在 Xournal++ 里确定能触发插件加速键，裸功能键还没验证过。
                    // 插件那边两个都注册了，无论哪个能触发都能生效。
                    // 自检模式必须绕过：Ctrl+Alt+F13 走的是 SendCombo，
                    // 不像 Send() 那样自带 DryRun 判断，不拦住就会在 --test 里真发键。
                    if (DryRun) Log.Write("  [dry] Ctrl+Alt+F13 (hand)");
                    else Interop.SendCombo(true, true, false, false, 0x7C);
                    Log.Write("pan toggle -> hand ON");
                }
                else
                {
                    Send(RestoreChar());
                    Log.Write("pan toggle -> hand OFF -> " + RestoreChar());
                }
            }
            return true;
        }

        if (vk == _cfg.MenuVk)
        {
            if (down) OnMenuDown();
            return true;   // 抬起不做任何事：驱动只会给脉冲，抬起没有语义
        }

        if (down && vk == Interop.VK_ESCAPE && _state == PadState.Menu)
        {
            CloseMenu("escape");
            return true;
        }

        return false;
    }

    private bool IsTargetActive()
    {
        if (_cfg.Target == "*") return true;

        // 圆盘开着时一律放行：那个窗口是我们自己的，它可能成为前台窗口，
        // 但用户此刻按的就是"确认"，不能因为前台是自己就把按键丢掉。
        if (_state == PadState.Menu) return true;

        return string.Equals(Interop.ForegroundProcessName(), _cfg.Target, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------ 平移（兜底）

    /// <summary>不立刻动手：先挂起，等 Tick 确认按键确实还按着（真按住）才平移。</summary>
    private void ArmPan()
    {
        if (_state != PadState.Idle) return;
        _panArmed = true;
        _panArmedAt = DateTime.UtcNow;
    }

    private void OnPanUp()
    {
        _panArmed = false;
        if (_state != PadState.Panning) return;
        _state = PadState.Idle;
        Send(RestoreChar());
        Log.Write("pan up -> restore " + RestoreChar());
    }

    // ------------------------------------------------------------ 圆盘

    private void OnMenuDown()
    {
        if (_state == PadState.Menu) { CommitMenu(); return; }
        if (_state != PadState.Idle) return;

        var (x, y) = CursorProvider();
        _anchorX = x; _anchorY = y;
        _hitX = x; _hitY = y;
        _repositioned = false;
        _highlight = 0;
        _state = PadState.Menu;
        _menuOpenedAt = DateTime.UtcNow;

        if (!DryRun)
        {
            // ShowAt 会返回实际生效的中心（贴屏幕边缘时会被夹回来）
            (_hitX, _hitY) = _radial.ShowAt(_anchorX, _anchorY, _sectors);
            _radial.Highlight(0);
        }
        Log.Write($"menu opened @ ({x},{y}) -> center ({_hitX},{_hitY})" +
                  (PenSource.Tracker != null ? "  [" + PenSource.Describe() + "]" : ""));
    }

    private void CommitMenu()
    {
        if (_highlight >= 1 && _highlight <= _sectors.Count)
        {
            _activeSector = _highlight;
            _eraserMode = false;
            Send((char)('0' + _activeSector));
            Log.Write($"menu commit -> sector {_activeSector} ({_sectors[_activeSector - 1].Label})");
        }
        else
        {
            // 没移动就再点一下 = 切换橡皮（沿用你原来选的那个交互）
            _eraserMode = !_eraserMode;
            if (_eraserMode)
            {
                int es = EraserSector();
                if (es > 0) { Send((char)('0' + es)); Log.Write($"menu commit -> eraser (sector {es})"); }
                else { _eraserMode = false; Send((char)('0' + Math.Clamp(_activeSector, 1, 8))); Log.Write("menu commit -> 配置里没有橡皮扇区"); }
            }
            else
            {
                Send((char)('0' + Math.Clamp(_activeSector, 1, 8)));
                Log.Write($"menu commit -> back to sector {_activeSector}");
            }
        }

        _handOn = false;   // 从圆盘选了笔，就自动退出手型
        CloseMenu(null);
    }

    private void CloseMenu(string why)
    {
        _state = PadState.Idle;
        _highlight = 0;
        if (!DryRun) _radial.CloseOverlay();
        if (why != null) Log.Write("menu closed: " + why);
    }

    // ------------------------------------------------------------ 周期任务

    private void Tick()
    {
        // 平移：确认是"真按住"而不是驱动给的脉冲
        if (_panArmed && _state == PadState.Idle &&
            (DateTime.UtcNow - _panArmedAt).TotalMilliseconds >= PanHoldMs)
        {
            if (Interop.IsDown(_cfg.PanVk))
            {
                _panArmed = false;
                _state = PadState.Panning;
                Send('h');
                Log.Write("pan down -> ALT+H (hand)  [key really held]");
            }
            else
            {
                _panArmed = false;
                Log.Write("pan key was a pulse (driver taps keys) -> ignored");
            }
        }

        if (_state == PadState.Panning && !Interop.IsDown(_cfg.PanVk))
        {
            _state = PadState.Idle;
            Send(RestoreChar());
            Log.Write("pan up (safety) -> restore " + RestoreChar());
        }

        if (_state == PadState.Menu)
        {
            // 窗口第一次布局完成后再校正一次中心（那时 GetWindowRect 才是最终尺寸）
            if (!DryRun && !_repositioned &&
                (DateTime.UtcNow - _menuOpenedAt).TotalMilliseconds > 150)
            {
                (_hitX, _hitY) = _radial.PositionTo(_anchorX, _anchorY);
                _repositioned = true;
                Log.Write($"menu center -> ({_hitX},{_hitY})  pen was at ({_anchorX},{_anchorY})");
                try { OnMenuPositioned?.Invoke(_hitX, _hitY); } catch { }
            }

            var (x, y) = CursorProvider();
            int hit = DryRun ? DryHit(x, y) : _radial.HitTest(_hitX, _hitY, x, y);
            if (hit != _highlight)
            {
                _highlight = hit;
                _menuOpenedAt = DateTime.UtcNow;   // 只要还在选，就别超时
                if (!DryRun) _radial.Highlight(hit);
                Log.Write($"highlight -> {hit}");
            }

            if ((DateTime.UtcNow - _menuOpenedAt).TotalMilliseconds > MenuTimeoutMs)
                CloseMenu("timeout");
        }
    }

    private int DryHit(int x, int y)
    {
        if (_sectors.Count == 0) return 0;
        double dx = x - _anchorX, dy = y - _anchorY;
        double dist = Math.Sqrt(dx * dx + dy * dy);
        if (dist < 46) return 0;
        if (dist > 188) return 0;

        double deg = Math.Atan2(dy, dx) * 180.0 / Math.PI + 90.0;
        while (deg < 0) deg += 360;
        while (deg >= 360) deg -= 360;

        double span = 360.0 / _sectors.Count;
        int idx = (int)(deg / span) + 1;
        return Math.Clamp(idx, 1, _sectors.Count);
    }

    // ------------------------------------------------------------ 发键

    private char RestoreChar()
    {
        if (_eraserMode)
        {
            int es = EraserSector();
            if (es > 0) return (char)('0' + es);
        }
        return (char)('0' + Math.Clamp(_activeSector, 1, 8));
    }

    private void Send(char c)
    {
        if (DryRun) { Log.Write($"  [dry] ALT+{char.ToUpperInvariant(c)}"); return; }
        Interop.SendAlt(c);
    }

    // ------------------------------------------------------------ 供自检调用

    public bool TestKey(int vk, bool down) => OnKey(vk, down);
    public void TestTick() => Tick();
}
