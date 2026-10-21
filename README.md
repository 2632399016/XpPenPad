<p align="center"><img src="docs/icon.png" width="128" alt="XpPenPad icon"></p>

# XpPenPad

把 **Gaomon 1060PRO 数位板侧键** 变成 Xournal++ 的高效操控：侧键 1 = 切换手形平移，侧键 2 = 弹出**圆盘笔刷预设菜单**（8 格，可选钢笔 / 荧光笔 / 橡皮 / 手形等，每格带颜色与粗细）。配套一个 Xournal++ Lua 插件（`drawboard-pen`），把圆盘选择实时下发到画布；所有改动**无需重启 Xournal++ 即可生效**。

> **依赖 Xournal++**（必需）：官网 https://xournalpp.github.io/ · GitHub https://github.com/xournalpp/xournalpp
> XpPenPad 是 Xournal++ 的配套外挂，单独用没有意义。驱动用 .NET 9 自包含单文件，常驻系统托盘。

## 功能特性
- 数位板侧键 → 全局热键（F 键，避开 Xournal++ 菜单助记符冲突）。
- 圆盘预设：8 格可视化编辑，名称随工具 / 颜色 / 粗细**自动生成**（手填名称受保护）。
- 保存即热重载：预设变更进程内即时生效，不重启 Xournal++、不重启小工具。
- 笔坐标标定（`--calib`）、托盘「重载预设」「设置…」。
- 侧键可交换、圆盘位置可微调。

## 架构与数据流
```
Gaomon 驱动 -> 裸 F 键 -> XpPenPad(常驻, RawInput 全局钩子)
                              | 侧键1 -> 切换手形平移（发 F13）
                              | 侧键2 -> 弹出圆盘 -> 选格（发 Alt+1..8）
                              v 快捷键
            Xournal++ 插件 drawboard-pen (main.lua) 接收 -> 切换工具/颜色/粗细
```
`presets.ini` 是两端共享的单一数据源（默认在 `%LOCALAPPDATA%\xournalpp\plugins\drawboard-pen\presets.ini`）：插件每次调用都实时重读，所以预设改动天然热生效。

## 目录结构
```
XpPenPad/                  .NET 9 源码 (C#, WPF+WinForms 混合)
  *.cs, XpPenPad.csproj    主程序、控制器、RawInput、圆盘窗口、配置窗
  xpPenPad.ini             示例配置（F 键映射、目标进程、笔坐标量程）
plugins/drawboard-pen/     Xournal++ 插件 (main.lua / plugin.ini / presets.ini)
build/                    build.ps1 / deploy.ps1 / smoke.ps1（可移植，基于本仓库定位）
docs/guide.md              完整使用与部署指南
docs/radial-preview.png    圆盘外观预览图
```

## 构建（需要 .NET 9 SDK）
```powershell
# 方式一：仓库内置脚本（dotnet 走 PATH 或 $env:DOTNET）
.\build\build.ps1

# 方式二：手动
dotnet publish XpPenPad\XpPenPad.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o XpPenPad\dist
# 再把 XpPenPad\bin\Release\net9.0-windows\win-x64\ 下 5 个 WPF 原生 DLL 拷到 dist\
```
产物：`XpPenPad/dist/XpPenPad.exe`（~143 MB 单文件）+ 5 个原生 DLL（`wpfgfx_cor3.dll`、`PresentationNative_cor3.dll`、`PenImc_cor3.dll`、`vcruntime140_cor3.dll`、`D3DCompiler_47_cor3.dll`）。

## 部署
```powershell
.\build\deploy.ps1   # 1) 停掉常驻  2) 复制插件到 %LOCALAPPDATA%\xournalpp\plugins\drawboard-pen  3) 重启常驻
```
然后在 Xournal++ 里启用 `drawboard-pen` 插件。

## 使用
1. 安装并运行一次 Xournal++，让它生成配置目录。
2. 把 `plugins/drawboard-pen` 复制到 `%LOCALAPPDATA%\xournalpp\plugins\drawboard-pen`（小工具与插件共享此处的 `presets.ini`）。'''<img width="685" height="145" alt="image" src="https://github.com/user-attachments/assets/1542ef0b-57af-46e7-979c-69c7a185a2df" />'''

3. Gaomon 驱动里把两个侧键设为**裸 F 键**（如F3、F4）。
4. 运行 `XpPenPad.exe`，托盘出现图标。
5. 侧键 1：切换手形 / 平移；侧键 2：弹出圆盘，转动笔或方向选择格子，松手确认。
6. 托盘「设置…」→「圆盘预设」可视化编辑 8 格；改工具 / 颜色 / 粗细名称自动变；保存即生效。

## 开箱即用（Release）
嫌自己构建麻烦，可直接下载 Release 里的 `XpPenPad-v1.0.0.zip`：已含 `XpPenPad.exe` + 5 个原生 DLL + 示例配置 + `plugins/drawboard-pen` 插件，**解压即跑**（仍需先装 Xournal++ 并启用插件）。详见压缩包内 `README.txt` 与上方安装步骤。

## 已知限制
- 仅 Windows；依赖 Xournal++ 插件通道（`Alt+1..8`）。
- 数位板侧键只能发裸 F 键、仅脉冲；笔不驱动系统光标（走 Windows Ink）。
- `dist/` 不入库，发布包见 GitHub Releases。

## License
MIT — 见 [LICENSE](LICENSE)。
