--[[ ------------------------------------------------------------------
  penpad · Xournal++ 插件（Drawboard 风格笔控制）
  ------------------------------------------------------------------
  这一半只做两件事：
    1) 把 Alt+1..8 变成"应用第 N 格预设"（工具 + 粗细 + 颜色）
    2) 提供手型（平移）入口，由 XpPenPad 用 F13 / Ctrl+Alt+F13 触发

  注意：手型【不能】用 <Alt>h —— 那会撞上 Xournal++ 菜单的中文助记键「帮助(H)」，
  实测结果是弹出帮助窗口。字母类一律禁用，只用 F 键。

  真正决定"什么时候发哪个键"的是常驻小工具 XpPenPad：
    数位板侧键 → 小工具 → 发给本插件。
  所以单独用这个插件只能拿到快捷键，没有圆盘。

  预设内容全部在 presets.ini 里，本文件不含任何颜色/尺寸定义。
  每次调用都会重新读 presets.ini，改完不用重启。

  ── 两个必须遵守的限制（都是实测踩出来的）──
  1) 不要在 initUi 里调用换笔/换色 API。
     initUi 是 Control 构造函数里 registerToolbar() 触发的，那时主窗口还没建好，
     调 changeActionState("select-tool", ...) 会直接把 Xournal++ 打崩，
     而且是 C++ 崩溃，lua 的 pcall 也挡不住。
  2) 不要把加速键绑成裸数字/裸空格。
     GTK 的窗口加速键优先于焦点输入框，绑裸键会导致在文本框里打不出数字和空格。
     所以一律用 <Alt> 前缀。

  ── 排错 ──
  每次真正应用一次预设都会往 %LOCALAPPDATA%\xournalpp\penpad.log 写一行。
  按下侧键后如果日志没有新增，说明按键根本没到达插件（问题在驱动/小工具，不在本文件）。
  ------------------------------------------------------------------ ]]

local MAX_SECTORS = 8

-- 需要时可以把这里写成绝对路径，覆盖"与本插件同目录"的默认行为
local CONFIG_OVERRIDE = nil

-- 没有颜色能力的工具，不能调 changeToolColor
local NO_COLOR = { eraser = true, hand = true, verticalSpace = true }

-- ---------------------------------------------------------------- 日志
local LOG_ON = true

local function logLine(msg)
  if not LOG_ON then return end
  local base = os.getenv("LOCALAPPDATA")
  if not base then return end
  local f = io.open(base .. "/xournalpp/penpad.log", "a")
  if f then
    f:write(os.date("%Y-%m-%d %H:%M:%S"), "  ", msg, "\n")
    f:close()
  end
end

-- ---------------------------------------------------------------- 配置读取

local function pluginDir()
  -- Plugin::addPluginToLuaPath 把 "<plugindir>/?.lua" 前置进了 package.path
  local p = package.path or ""
  local d = p:match("^(.-)[/\\]%?%.lua")
  return d
end

local function configPath()
  if CONFIG_OVERRIDE then return CONFIG_OVERRIDE end
  local d = pluginDir()
  if not d then return nil end
  return d .. "/presets.ini"
end

local function trim(s)
  return (s:gsub("^%s+", ""):gsub("%s+$", ""))
end

--- 解析极简 INI：只认 [section] 与 key=value，# 和 ; 开头为注释
local function readIni(path)
  local f = io.open(path, "r")
  if not f then return nil, "打不开 " .. tostring(path) end

  local content = f:read("*a") or ""
  f:close()

  -- UTF-8 BOM：用记事本保存过就会有
  if content:sub(1, 3) == "\239\187\191" then
    content = content:sub(4)
  end

  local cfg = {}
  local cur = nil
  for line in content:gmatch("[^\r\n]+") do
    local s = trim(line)
    local head = s:sub(1, 1)
    if s ~= "" and head ~= "#" and head ~= ";" then
      local sec = s:match("^%[(.-)%]$")
      if sec then
        cur = {}
        cfg[trim(sec)] = cur
      elseif cur then
        local k, v = s:match("^([%w_]+)%s*=%s*(.-)$")
        if k then cur[k] = trim(v) end
      end
    end
  end
  return cfg
end

local function loadConfig()
  local path = configPath()
  if not path then return nil end
  local cfg, err = readIni(path)
  if not cfg then logLine(tostring(err)) end
  return cfg
end

-- ---------------------------------------------------------------- 应用预设

-- 走新版 API（app.changeActionState），与系统自带 PenSwitch 插件一致。
-- 动作名是 kebab-case：select-tool / tool-size / tool-eraser-type ...
-- 常量表在 app.C 下：Tool_pen / ToolSize_medium / EraserType_default ...
local function applySectorImpl(n)
  local cfg = loadConfig()
  local s = cfg and cfg["sector" .. n]
  if not s then
    logLine("[sector" .. n .. "] MISSING in presets.ini")
    return
  end

  local tool = s.tool
  if not tool or tool == "" then
    logLine("[sector" .. n .. "] 缺少 tool")
    return
  end

  local toolConst = app.C["Tool_" .. tool]
  if not toolConst then
    logLine("[sector" .. n .. "] 未知的 tool: " .. tostring(tool))
    return
  end

  -- 顺序：工具 -> 尺寸 -> 颜色
  app.changeActionState("select-tool", toolConst)

  if s.size and s.size ~= "" then
    local szConst = app.C["ToolSize_" .. s.size]
    if szConst then
      app.changeActionState("tool-size", szConst)
    else
      logLine("[sector" .. n .. "] 未知的 size: " .. tostring(s.size))
    end
  end

  if not NO_COLOR[tool] and s.color and s.color ~= "" then
    local hex = s.color:gsub("[^%x]", "")
    if #hex == 6 then
      app.changeToolColor({ ["color"] = tonumber(hex, 16), ["tool"] = tool })
    else
      logLine("[sector" .. n .. "] color 不是 6 位十六进制: " .. tostring(s.color))
    end
  end

  logLine(string.format("sector %d APPLIED  tool=%s size=%s color=%s label=%s",
                        n, tool, tostring(s.size), tostring(s.color), tostring(s.label)))
end

-- 包一层 pcall：Xournal++ 不会把 Lua 异常打印到我们能看的地方，
-- 出问题时必须自己落到日志里，否则表现为"完全没反应"
local function applySector(n)
  local ok, err = pcall(applySectorImpl, n)
  if not ok then
    logLine("APPLY ERROR sector " .. tostring(n) .. ": " .. tostring(err))
  end
end

-- ---------------------------------------------------------------- 注册

-- registerUi 的 callback 按名字在全局查，所以用 _G 批量生成
for i = 1, MAX_SECTORS do
  _G["penpadSector" .. i] = function() applySector(i) end
end

function penpadEraser()
  logLine("eraser requested")
  app.changeActionState("select-tool", app.C.Tool_eraser)
end

-- 诊断用：只写日志，不碰任何工具状态，用来确认"按键是否到达插件"
function penpadPingMod() logLine("PING ok  accel=<Ctrl><Alt>F12") end
function penpadPingRaw() logLine("PING ok  accel=F14") end

-- 手型（平移）。由 XpPenPad 在"切换式平移"打开时发 F13 触发。
-- 用 F13 而不是 <Alt>h：Alt+H 会被 Xournal++ 菜单的「帮助(H)」助记键抢走。
function penpadHand()
  logLine("hand requested (F13)")
  app.changeActionState("select-tool", app.C.Tool_hand)
end

function initUi()
  local cfg = loadConfig()
  local found = 0

  for i = 1, MAX_SECTORS do
    local s = cfg and cfg["sector" .. i]
    if s then
      found = found + 1
      app.registerUi({
        ["menu"]        = "圆盘 · " .. (s.label or ("第" .. i .. "格")),
        ["callback"]    = "penpadSector" .. i,
        ["accelerator"] = "<Alt>" .. i,
      })
    end
  end

  -- 注意：这里只注册 <Alt>数字。
  -- 千万别用 <Alt>h / <Alt>e 这类字母组合 —— Xournal++ 的菜单栏有中文助记键
  -- （帮助(H)、编辑(E)），实测 Alt+H 会被菜单抢走，弹出帮助窗口而不是切手型。
  -- （把侧键透传给 XP 原生这条路上，本驱动做不到 —— 见下面的说明）

  -- 手型（切换式平移）。两个加速键都注册，哪个能触发都能用：
  -- 小工具默认发 Ctrl+Alt+F13（组合键形式在本机实测确定能触发插件加速键），
  -- 裸 F13 也留着 —— 万一组合键不行，改小工具配置换成裸键即可，不用动这里。
  app.registerUi({ ["menu"] = "圆盘 · 手型（F13）",
                   ["callback"] = "penpadHand",
                   ["accelerator"] = "F13" })
  app.registerUi({ ["menu"] = "圆盘 · 手型（Ctrl+Alt+F13）",
                   ["callback"] = "penpadHand",
                   ["accelerator"] = "<Ctrl><Alt>F13" })

  -- 诊断用，可整段删掉
  app.registerUi({ ["menu"] = "penpad · 诊断A (Ctrl+Alt+F12)",
                   ["callback"] = "penpadPingMod",
                   ["accelerator"] = "<Ctrl><Alt>F12" })
  app.registerUi({ ["menu"] = "penpad · 诊断B (F14)",
                   ["callback"] = "penpadPingRaw",
                   ["accelerator"] = "F14" })

  if found == 0 then
    logLine("initUi: FAILED, 0 sectors read")
    print("[penpad] 没能从 presets.ini 读到任何扇区，请检查文件是否在本插件目录内")
  else
    logLine("initUi: registered " .. found .. " sectors + hand + eraser")
    print("[penpad] 已注册 " .. found .. " 个扇区预设")
  end
end
