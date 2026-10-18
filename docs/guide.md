# XpPenPad — 软件梳理 / 使用与部署指南

> 适用版本：2026-10-18 构建（含图形配置窗口、圆盘预设可视化编辑、保存后热重载）
> 定位：**Windows 常驻小工具 + Xournal++ Lua 插件**，把高漫 1060PRO 数位笔的两个侧键，
> 变成「侧键1 = 切换式手型平移」「侧键2 = 圆盘选笔」。
> 全程**不编译 Xournal++**，纯外挂。

---

## 1. 整体架构与数据流

```
┌─────────────┐   侧键1=F3    ┌──────────────────┐    Alt+数字 / F13    ┌──────────────────────┐
│  高漫驱动    │ ───────────▶ │  XpPenPad (常驻)  │ ──────────────────▶ │  Xournal++ 插件       │
│  (笔侧键)    │   侧键2=F4    │  (键盘钩子+状态机)│                     │  (main.lua)           │
└─────────────┘ ───────────▶ └──────────────────┘                     └──────────┬───────────┘
                                                                                   │ changeActionState
                                                                                   ▼
                                                                          Xournal++ 换笔/换色/换粗细/手型
```

- **小工具（XpPenPad.exe）**：拦截数位板侧键发出的 F3/F4，按状态机决定发给 Xournal++ 什么键。
- **插件（main.lua）**：把 `Alt+1..8`、`F13` 等加速键翻译成「应用第 N 格预设 / 切手型」。
- **双方共用 `presets.ini`**：圆盘 8 格的 名称 / 工具 / 粗细 / 颜色 都在这一个文件里。

关键实测结论（决定架构）：高漫驱动按侧键**只发一次"按下+抬起"脉冲**，系统层面根本没有
"按住"；且笔**不驱动系统光标**（走 Windows Ink 通路）。所以平移做成"切换式"，圆盘位置走
**HID 原始坐标**（已标定量程 `0..32767`）。

---

## 2. 文件结构

### 2.1 源码 `D:\Tools\XournalppTweaks\XpPenPad\`

| 文件 | 职责 |
|---|---|
| `Program.cs` | 入口：`Main`（单实例互斥、托盘菜单、命令行模式）；`SelfTest`；`ResolvePresets` |
| `Config.cs` | `Ini`（极简 INI 解析/定点修改）、`Log`、`Sector`、`PresetStore.Load`、`AppConfig.Load` |
| `ConfigForm.cs` | **图形配置窗口**（5 个标签页，纯代码 WinForms 绝对定位） |
| `PadController.cs` | 状态机：平移（兜底）/ 圆盘 / 选格 / 发键；`ReloadPresets()` |
| `RadialWindow.cs` | WPF 圆盘窗口（`WS_EX_NOACTIVATE` + `WS_EX_TOOLWINDOW`，**不抢焦点**） |
| `RawInput.cs` | 原始输入：枚举设备、解析笔 HID 坐标（x64 偏移已校正） |
| `Interop.cs` | 低层键盘钩子、发键（`SendInput`/`SendCombo`/`PostKey`）、前台判定 |
| `XpPenPad.csproj` | .NET 9 `WinExe`，`<UseWPF>`+`<UseWindowsForms>` 双开，自包含单文件发布 |
| `xpPenPad.ini` | 小工具配置（改完**需重启**小工具才生效，见 §3.4） |

### 2.2 交付物 `D:\Tools\XournalppTweaks\XpPenPad\dist\`

- `XpPenPad.exe` —— 自包含单文件（约 143 MB，无需本机装 .NET 运行时）
- 5 个 WPF 原生 DLL（**单文件包里没有，必须散落在旁边**，否则秒崩）：
  `wpfgfx_cor3.dll` / `PresentationNative_cor3.dll` / `PenImc_cor3.dll` /
  `vcruntime140_cor3.dll` / `D3DCompiler_47_cor3.dll`
- `xpPenPad.ini` —— 运行配置（从源码目录同步过来）
- 运行产物：`xpPenPad.log`（运行日志）、`menu_shot.png`（圆盘定位截图）、`raw_calib.csv`（标定采集）

### 2.3 插件 `D:\Tools\XournalppTweaks\plugins\drawboard-pen\`（源）→ 部署到 `%LOCALAPPDATA%\xournalpp\plugins\drawboard-pen\`

| 文件 | 职责 |
|---|---|
| `main.lua` | 注册 `Alt+1..8`（应用第 N 格预设）、`F13`/`Ctrl+Alt+F13`（手型）、诊断加速键；读 `presets.ini` 应用预设 |
| `plugin.ini` | 插件元信息（`enabled=true`） |
| `presets.ini` | 8 格圆盘预设（`label` / `tool` / `size` / `color`） |

> 说明：插件每次被调用都**重新读** `presets.ini`，所以改预设后**不用重启 Xournal++**。

### 2.4 构建 / 部署脚本 `D:\_kpa2_build\`

| 脚本 | 作用 |
|---|---|
| `xp_build.ps1` | 发布单文件到 `dist`，回拷 5 个原生 DLL，先杀旧进程 |
| `xp_deploy.ps1` | 杀旧进程 → 复制插件 `main.lua` → 启动常驻 |
| `xp_smoke.ps1` | 冒烟：`--test` 自检 + `--calib 3`，汇总到 `xp_smoke.log` |
| `xp_calib.py` / `cursor_sampler.ps1` | 标定扫描 / 系统光标轨迹采样（排查用） |

---

## 3. 使用说明

### 3.1 驱动侧（一次性设置）
高漫驱动里把两个侧键设成**裸功能键**（实测修饰键不生效）：
- 侧键1 → `F3`
- 侧键2 → `F4`

### 3.2 日常操作（小工具运行时）
| 操作 | 效果 |
|---|---|
| 点一下**侧键1 (F3)** | 切换「手型平移」；再点一下切回原来的笔 |
| 点一下**侧键2 (F4)** | 在下笔点弹出圆盘 |
| 圆盘开着**移动笔** | 高亮对应扇区（按方向/距离选格） |
| 再点一下侧键2 | 确认当前高亮的笔预设 |
| 同位置连点两下（几乎没动） | 切橡皮 |
| 圆盘开着按 `Esc` | 取消 |
| 6 秒不操作（选格时重新计时，15 秒上限） | 自动关闭 |

### 3.3 托盘菜单（右下角图标）
- **当前预设** —— 查看 8 格内容
- **重载预设** —— 手动让小工具重读 `presets.ini`
- **打开配置文件夹** —— 打开 `dist` 目录
- **设置…** —— 打开图形配置窗口（见 §3.4）
- **暂停 / 退出**

### 3.4 图形配置（托盘「设置…」）
5 个标签页，保存逻辑：

| 标签页 | 内容 | 保存后 |
|---|---|---|
| 热键 | 侧键1/2 的修饰键 + 按键（F1~F24）；**互换侧键1/2 功能** 开关 | 需重启 |
| 行为 | 目标进程、拖动阈值、笔坐标来源(auto/raw/cursor)、坐标偏移、设备过滤、笔原始量程 | 需重启 |
| 标定 | `pen_raw` 四值（默认 0..32767） | 需重启 |
| **圆盘预设** | 8 格的 **名称 / 工具 / 粗细 / 颜色** | **即时生效（热重载，无需重启）** |
| 路径 / 调试 | 预设文件路径；三个 debug 开关 | — |

**圆盘预设页要点（本次新增）：**
- 改「工具 / 颜色 / 粗细」任意一项，**名称会自动生成**（如「黑 细」「荧光黄 粗」；橡皮→「橡皮」、手形→「手形」）。
- 颜色名由 17 色调色板按 RGB 就近匹配，未知色回退显示 hex。
- 想自定义名称：直接改左侧文本框即可，之后改工具**不再覆盖**你手改的名称。
- 保存后圆盘与托盘「当前预设」下一次打开即反映新值。

### 3.5 `presets.ini` 格式
```ini
[sector1]
label=黑 细          ; 圆盘上显示的名字（2~4 字最佳）
tool=pen             ; pen/highlighter/eraser/text/selectRect/selectRegion/selectObject/hand/verticalSpace
size=thin            ; veryThin/thin/medium/thick/veryThick（eraser/hand 可省略）
color=000000         ; 6 位十六进制 RGB，不要写 # 号
```
- 最多 8 格，从 `[sector1]` 连续编号，不要断号。
- `eraser` / `hand` 不用写 `size` 与 `color`。
- 改完不用重启 Xournal++：插件每次调用都重读；小工具托盘「重载预设」可强制刷新。

---

## 4. 部署说明

### 4.1 首次部署（环境就绪后）
1. **装 Xournal++**：用 1.3.8 便携版，解压到 `D:\XournalPlusPlus`（主程序 `bin\xournalpp.exe`）。
2. **部署插件**：把 `D:\Tools\XournalppTweaks\plugins\drawboard-pen\` 整体复制到
   `%LOCALAPPDATA%\xournalpp\plugins\drawboard-pen\`。
3. **部署小工具**：
   - `xp_build.ps1` —— 发布到 `dist`（自动杀旧进程、回拷原生 DLL）。
   - `xp_deploy.ps1` —— 杀旧进程 → 复制插件 → 启动常驻。
4. **验证插件加载**：启动 Xournal++，Plugin 菜单里应出现「圆盘 · 黑 细」等条目；
   `%LOCALAPPDATA%\xournalpp\penpad.log` 有 `initUi: registered 8 sectors + hand + eraser`。

### 4.2 日常更新
- **改小工具源码后**：`xp_build.ps1` → `xp_deploy.ps1`。
- **改预设（presets.ini）**：
  - 直接编辑 `%LOCALAPPDATA%\xournalpp\plugins\drawboard-pen\presets.ini`；或
  - 托盘「设置…」→ 圆盘预设，保存后**即时生效**。
- **改小工具配置（xpPenPad.ini）**：编辑 `dist\xpPenPad.ini` 后，**重启小工具**（托盘退出再启动，或在配置窗口保存时选「重启」）。

### 4.3 命令行自检（无界面也能验证）
| 命令 | 作用 |
|---|---|
| `XpPenPad.exe --test` | 状态机 12 步自检（含 ini 往返、窗口构造、预设往返），结果进 `xpPenPad.log` |
| `XpPenPad.exe --calib 30` | 标定采集：笔扫满数位板 30 秒，原始坐标写 `raw_calib.csv` |
| `XpPenPad.exe --preview out.png` | 画圆盘并截图（看外观） |
| `XpPenPad.exe --devices` | 列出本机所有原始输入设备（确认笔是 HID 数字化仪） |
| `XpPenPad.exe --sendkey Ctrl+Alt+F12 --to xournalpp` | 发组合键测试（日志含焦点/插入结果） |
| `XpPenPad.exe --dry` | 常驻但只记录不发键，验证钩子链路 |

### 4.4 排错
| 现象 | 先看哪里 | 判读 |
|---|---|---|
| 按侧键完全没反应 | `dist\xpPenPad.log` 里有没有 `KEY vk=0x7x` | 没有=驱动没发键；有但 `targetOK=False`=前台判定失败 |
| 圆盘位置偏 | `dist\menu_shot.png` 红/蓝十字差多少 | 调 `pen_offset_x/y` 或 `pen_raw_*` 量程 |
| 选了笔但 XP 没变 | `%LOCALAPPDATA%\xournalpp\penpad.log` 的 `[sector N] APPLIED` | 没有=按键没到插件；有=插件层面 OK，查 XP 加速键 |
| 启动即崩 `0xC0000135` | 5 个 WPF 原生 DLL 是否在 `dist` 旁 | 缺失 → 重跑 `xp_build.ps1` |

---

## 5. 已知限制 / 设计取舍（一笔带过）
- **侧键只发脉冲**：高漫驱动不"按住"，平移做成"切换式"（点一下切手型，再点切回）。
- **笔不驱动系统光标**：圆盘位置走 HID 原始坐标，原生量程实测 `0..32767`（驱动界面 50800×31760 是激活区单位，**不是** HID 原始值）。
- **插件绝不注册 `Alt+字母`**：`Alt+H` 会撞 Xournal++ 菜单「帮助(H)」助记键，弹出帮助窗口；只用 F 键。
- **互换侧键功能**：只调 pan/menu 两组（键+修饰键），物理按键不变（侧键1 仍发 F3）。

---

## 6. 常用路径速查
| 用途 | 路径 |
|---|---|
| 小工具源码 | `D:\Tools\XournalppTweaks\XpPenPad\` |
| 小工具交付 | `D:\Tools\XournalppTweaks\XpPenPad\dist\` |
| 插件源 | `D:\Tools\XournalppTweaks\plugins\drawboard-pen\` |
| 已部署插件 / 预设 | `%LOCALAPPDATA%\xournalpp\plugins\drawboard-pen\` |
| 构建/部署脚本 | `D:\_kpa2_build\` |
| 小工具运行日志 | `dist\xpPenPad.log` |
| 插件应用日志 | `%LOCALAPPDATA%\xournalpp\penpad.log` |
