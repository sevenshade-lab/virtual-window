// 界面语言：中文 / English（用户 2026-09-26：日常用英文更简洁）。Prefs "lang" = auto / zh / en，
// auto 跟系统界面语言。右上角最小化左边的「EN / 中」一点就切。
//
// 做法：程序内部照旧用中文（状态标签的 Text 还拿来比较，比如「已嵌入」「正在启动…」），
// 只在显示的那一刻查表翻译 —— 按钮、标签画字时过一遍 L.T，自己画的面板、菜单、提示框、弹框在生成时过一遍。
// 这样切语言不用重建界面，内部逻辑也一个字不用动。窗口标题「虚拟窗口」不翻：编译脚本和外部程序按它找窗口。
// 带数字 / 名字的句子用 L.F("中文模板 {0}", …)，模板本身是查表的键。
//
// 中文显示也过一张表（ZhMap，用户 2026-09-27：界面用词要整洁）：代码里的中文是内部键，偏口语
// （「在干活」「闲着」「一家」），显示时换成顺口、统一的说法（别往书面腔改，用户嫌像英文直译）。术语统一：排版→布局、
// 客户端→桌面端、家→个；「格子」保留。改显示只改 ZhMap，别动代码里的键（状态会写进 status.json 给外部程序读）。
using System;
using System.Collections.Generic;
using System.Globalization;

internal static class L
{
    public static bool En;

    public static string Pref
    {
        get
        {
            string p = Prefs.GetStr("lang", "auto");
            return p == "zh" || p == "en" ? p : "auto";
        }
    }

    public static void Load()
    {
        string p = Pref;
        En = p == "en" || (p == "auto" && CultureInfo.CurrentUICulture.TwoLetterISOLanguageName != "zh");
    }

    public static string T(string zh)
    {
        if (string.IsNullOrEmpty(zh)) return zh;
        string shown;
        if (!En)
        {
            if (ZhMap.TryGetValue(zh, out shown)) return shown;
            foreach (var kv in ZhPrefixes)
                if (zh.StartsWith(kv.Key, StringComparison.Ordinal)) return kv.Value + zh.Substring(kv.Key.Length);
            return zh;
        }
        if (Map.TryGetValue(zh, out shown)) return shown;
        // 前缀拼出来的（「启动 Cursor」「拖动换位置 · D:\...」）
        foreach (var kv in Prefixes)
            if (zh.StartsWith(kv.Key, StringComparison.Ordinal)) return kv.Value + zh.Substring(kv.Key.Length);
        return zh;
    }

    public static string F(string zhFormat, params object[] args)
    {
        return string.Format(T(zhFormat), args);
    }

    // 列举用的顿号
    public static string Sep { get { return En ? ", " : "、"; } }

    static readonly KeyValuePair<string, string>[] Prefixes =
    {
        new KeyValuePair<string, string>("启动 ", "Launch "),
        new KeyValuePair<string, string>("拖动换位置 · ", "Drag to move · "),
    };

    static readonly Dictionary<string, string> Map = new Dictionary<string, string>
    {
        // 工具栏
        { "虚拟窗口", "Virtual Window" },
        { "纳入已开的", "Adopt" },
        { "重新排版", "Re-tile" },
        { "换排版", "Layout" },
        { "队列", "Queue" },
        { "网格", "Grid" },
        { "跳过", "Skip" },
        { "＋ 新会话", "+ New" },
        { "设置", "Settings" },
        { "把已经开着的客户端推进格子", "Pull already-open apps into the panes" },
        { "按当前这套排版，把窗口重新贴一遍", "Re-apply the current layout" },
        { "换下一套排版（同样格子数下轮换）", "Next layout for this number of panes" },
        { "开一家的桌面端，或者在某个文件夹里开它的终端", "Open an agent's desktop app, or its CLI in a folder" },
        { "外观、偏好、状态灯钩子、名单、添加窗口、组合、日志", "Appearance, preferences, status hooks, agents, windows, sets, log" },
        { "最小化（格子里的窗口跟着收起）", "Minimize (embedded apps go with it)" },
        { "桌面客户端一起关闭；CLI 还原为独立窗口（可在「设置」里调整）", "Close, desktop apps included; CLI sessions become separate windows (change in Settings)" },
        { "关掉虚拟窗口（客户端留着，只还成独立窗口）", "Close (apps stay open as separate windows)" },
        { "切回格子视图", "Back to grid view" },
        { "一次只看一个：等你回复的排队，在干活的收到侧栏", "One at a time: sessions waiting for you queue up, working ones sit on the left" },
        { "这个先不看，排到队尾", "Not now: move it to the end of the queue" },
        { "拖动换位置 · 双击放大", "Drag to move · double-click to zoom" },
        { "切换到英文界面", "Switch to English" },
        { "切换到中文界面", "Switch to Chinese" },

        // 外观
        { "浅色", "Light" },
        { "深色", "Dark" },
        { "跟随系统", "System" },
        { "谷歌蓝", "Blue" },
        { "经典橙", "Terracotta" },
        { "外观：{0}（点一下切明暗，右键可选跟随系统）", "Appearance: {0} (click to toggle, right-click for System)" },

        // 格子表头、状态
        { "移出", "Remove" },
        { "放大", "Zoom" },
        { "还原", "Restore" },
        { "未接入", "Not connected" },
        { "已嵌入", "Embedded" },
        { "嵌入失败", "Embed failed" },
        { "找到窗口，待嵌入", "Found, embedding…" },
        { "正在启动…", "Starting…" },
        { "没找到启动入口", "No launcher found" },
        { "没找到窗口", "Not open" },
        { "移出失败", "Remove failed" },
        { "启动未完成，可重试", "Launch incomplete, retry" },
        { "还原失败，未关闭", "Release failed, not closed" },
        { "未铺满 · 窗 {0}×{1} / 格 {2}×{3}", "Not filling · window {0}×{1} / pane {2}×{3}" },
        { "{0} 在跑 · 已经 {1}", "{0} working · {1}" },
        { "{0} 在问你，等你选", "{0} is asking you" },
        { "{0} 要用 {1}，等你批准", "{0} needs your approval to use {1}" },
        { "{0} 等你批准", "{0} needs your approval" },
        { "{0} 问了你一个问题", "{0} asked you a question" },
        { "{0} 计划写好了，等你看", "{0} has a plan ready for you" },
        { "最后问你：{0}", "Ends with: {0}" },
        { "文件夹：{0}", "Folder: {0}" },
        { "{0} 做完了 · {1}前", "{0} finished · {1} ago" },
        { "{0} 闲着 · {1}", "{0} idle · {1}" },
        { "{0} 没在跑 · {1}", "{0} not running · {1}" },
        { "{0} 秒", "{0}s" },
        { "{0} 分钟", "{0} min" },
        { "{0} 小时", "{0} h" },
        { "{0} 天", "{0} d" },

        // 副标题、排版名
        { "自由组合", "Mixed" },
        { "客户端", "Apps" },
        { "放大 {0} · 点还原回格子", "Zoomed: {0} · Restore to go back" },
        { "放大 {0} · 点还原回队列", "Zoomed: {0} · Restore to go back" },
        { "{0} 格 · {1}", "{0} panes · {1}" },
        { "队列 · 等你 {0} · 在干活 {1}", "Queue · {0} waiting · {1} working" },
        { "整面", "Full" },
        { "左右", "Side by side" },
        { "上下", "Stacked" },
        { "田字空一格", "2×2, one empty" },
        { "三列", "3 columns" },
        { "三行", "3 rows" },
        { "左主右二", "Main left, 2 right" },
        { "上主下二", "Main top, 2 below" },
        { "田字", "2×2" },
        { "四列", "4 columns" },
        { "四行", "4 rows" },
        { "左主右三", "Main left, 3 right" },
        { "上主下三", "Main top, 3 below" },
        { "上二下三", "2 over 3" },
        { "上三下二", "3 over 2" },
        { "左二右三", "2 left, 3 right" },
        { "六宫空一格", "3×2, one empty" },
        { "两行三列", "2 rows × 3" },
        { "三行两列", "3 rows × 2" },
        { "左二右四", "2 left, 4 right" },
        { "近方形", "Square" },
        { "偏横", "Wide" },
        { "偏竖", "Tall" },

        // 队列侧栏
        { "排队", "Waiting" },
        { "在干活", "Working" },
        { "没有人在等你", "Nobody is waiting" },
        { "干完活的会话会排到这里", "Finished sessions line up here" },

        // 一句话提示
        { "后面没有别人在等", "No one else is waiting" },
        { "先纳入或启动几家，再切到队列", "Adopt or launch some apps first" },
        { "{0} 在问你，停手后换过去", "{0} is asking. Switching when you stop typing" },
        { "格子已满（最多 {0} 格）", "All panes are full (max {0})" },
        { "没有找到 {0} CLI，请先安装或加入 PATH", "{0} CLI not found. Install it or add it to PATH" },
        { "选择 {0} CLI 的工作目录（每次新建一个独立会话）", "Choose a working folder for {0} CLI (each opens a new session)" },
        { "没找到 {0} 的启动入口", "Couldn't find how to launch {0}" },
        { "桌面上没有别的客户端可以纳入", "No other apps to adopt" },
        { "还没有日志", "No log yet" },
        { "打不开日志", "Can't open the log" },

        // 菜单
        { "添加其他窗口", "Add a window" },
        { "把别的窗口放进格子（已经 {0} 格，满了）", "Add a window (all {0} panes are full)" },
        { "把别的窗口放进格子（没找到别的窗口）", "Add a window (none found)" },
        { "启动未打开的会话", "Launch closed sessions" },
        { "保存当前组合…", "Save current set…" },
        { "恢复组合", "Restore set" },
        { "上次组合", "Last set" },
        { "还原(&R)", "&Restore" },
        { "移动(&M)", "&Move" },
        { "大小(&S)", "&Size" },
        { "最小化(&N)", "Mi&nimize" },
        { "最大化(&X)", "Ma&ximize" },
        { "关闭(&C)\tAlt+F4", "&Close\tAlt+F4" },

        // 组合
        { "保存组合", "Save set" },
        { "给这组窗口起个名字", "Name this set" },
        { "组合名称", "Set name" },
        { "我的组合 {0}", "My set {0}" },
        { "记住顺序、排版和 CLI 目录。\n手动添加的外部窗口需重新纳入。", "Remembers order, layout and CLI folders.\nManually added windows need to be added again." },
        { "保存", "Save" },
        { "取消", "Cancel" },
        { "当前只有外部窗口，没有可保存的 Agent 会话", "Only external windows here, no agent sessions to save" },
        { "组合已保存；下次可从「恢复组合」选择", "Set saved. Pick it later from Restore set" },
        { "有窗口未能还原，组合切换已停止", "Some windows couldn't be released. Stopped" },
        { "已恢复「{0}」；未打开的会话可点击启动", "Restored \"{0}\". Click closed sessions to launch them" },

        // 关闭
        { "{0} 还在跑。关掉虚拟窗口时要连它们一起关吗？\n\n「是」＝一起关掉；「否」＝只收起虚拟窗口，客户端留着；「取消」＝不关了。",
          "{0} still running. Close them together with the virtual window?\n\nYes = close them too; No = close only the virtual window and keep the apps; Cancel = don't close." },
        { "{0} 没能还原成独立窗口，这次先不关宿主，避免把它们的进程一起带走。\n可以点那格的「移出」再试，或者等它响应了再关。",
          "{0} couldn't be released as a separate window, so the virtual window stays open to avoid taking it down.\nTry Remove on that pane, or close again once it responds." },

        // 设置页
        { "外观", "Appearance" },
        { "行为", "Behavior" },
        { "状态灯", "Status lights" },
        { "连 Windows 一起切", "Switch Windows too" },
        { "跟个性化里选深色 / 浅色一样：各家 agent、任务栏、资源管理器一起变", "Agents, taskbar and Explorer switch along" },
        { "悬停切换焦点", "Focus follows hover" },
        { "鼠标在格子上停 0.25 秒，键盘焦点就跟过去", "Rest the mouse on a pane for 0.25 s to move keyboard focus there" },
        { "空格组合键切换", "Space-key switching" },
        { "按住空格再按 QWE / ASD 跳格；单独轻敲还是空格", "Hold Space, then QWE / ASD to jump panes" },
        { "做完时闪烁任务栏", "Flash taskbar when done" },
        { "某一家做完或在问你、而你不在虚拟窗口里时提醒一下", "When an agent finishes or asks while you're in another window" },
        { "打开时一并启动桌面端", "Launch desktop apps on open" },
        { "打开虚拟窗口时把上次那几家没开着的桌面端一起启动", "Starts last session's desktop apps that aren't running" },
        { "关闭时一并关闭桌面端", "Close desktop apps on exit" },
        { "关掉虚拟窗口时把嵌着的桌面端一起关；终端会话留成独立窗口", "Also closes embedded desktop apps; CLI stays open" },
        { "Agent 名单", "Agents" },
        { "组合", "Sets" },
        { "日志", "Log" },
        // 索引签上的一小句（TipNotes.Short）
        { "看计划", "Plan" },
        { "问你", "Asks" },
        { "等批准", "Allow?" },
        { "批准", "Allow" },
        { "等你选", "Pick" },
        { "做完了", "Done" },
        { "↻ 重新安装钩子", "↻ Reinstall hooks" },
        { "没装钩子（点下面「重新安装钩子」）", "No hooks (use Reinstall hooks below)" },
        { "已装，还要在 Codex 的钩子管理页信任一次", "Installed. Trust them once in Codex's hooks page" },
        { "已装，重启 OpenCode 后生效", "Installed. Restart OpenCode to apply" },
        { "已装，重启 Cursor 后生效", "Installed. Restart Cursor to apply" },
        { "已装，还没收到过消息", "Installed. Nothing received yet" },
        { "正常 · 最近一次 {0}", "OK · {0}" },
        { "刚刚", "just now" },
        { "{0} 分钟前", "{0} min ago" },
        { "{0} 小时前", "{0} h ago" },
        { "{0} 天前", "{0} d ago" },
        { "已重装。新装的要重启那一家才生效；Codex 改过命令要重新信任", "Reinstalled. Restart an agent to load new hooks; Codex needs re-trust if the command changed" },
        { "有钩子没装上，详情见 hooks-report.txt", "Some hooks failed, see hooks-report.txt" },

        // 新会话
        { "新会话", "New session" },
        { "在哪个文件夹开 {0} 终端？", "Open {0} CLI in which folder?" },
        { "终端", "CLI" },
        { "更多 agent　没装的、添加自定义", "More agents   not installed, add custom" },
        { "‹ 返回", "‹ Back" },
        { "＋ 选择其他文件夹…", "+ Choose another folder…" },
        { "已打开", "Open" },
        { "只有终端", "CLI only" },
        { "选择 {0} 终端的工作文件夹", "Choose a working folder for {0} CLI" },

        // 名单
        { "＋ 添加自定义 agent", "+ Add custom agent" },
        { "预设 {0} 家 · 已装 {1} 家", "{0} presets · {1} installed" },
        { " · 自定义 {0} 家", " · {0} custom" },
        { "　　没装的照样列着，装好后自动认出来", "    Uninstalled ones stay listed and are detected once installed" },
        { "它还在格子里，先移出再删。", "It's still in a pane. Remove it first." },
        { "从名单里删掉「{0}」？（只删名单，不卸载软件）", "Remove \"{0}\" from the list? (Doesn't uninstall anything)" },
        { "已加入名单：{0}", "Added: {0}" },
        { "添加自定义 agent", "Add custom agent" },
        { "名字（必填）", "Name (required)" },
        { "比如 Kimi", "e.g. Kimi" },
        { "桌面端的进程名", "Desktop process name" },
        { "任务管理器里看到的名字，不带 .exe，比如 Kimi", "As shown in Task Manager, without .exe, e.g. Kimi" },
        { "桌面端程序位置", "Desktop app location" },
        { "可以留空：开着的窗口照样能纳入，只是启动不了", "Optional: open windows can still be adopted, just not launched" },
        { "浏览…", "Browse…" },
        { "程序 (*.exe)|*.exe", "Programs (*.exe)|*.exe" },
        { "选桌面端程序", "Choose the desktop app" },
        { "CLI 命令", "CLI command" },
        { "命令行里敲的命令，比如 kimi；没有就留空", "The command you type, e.g. kimi; leave empty if none" },
        { "官网（可选）", "Website (optional)" },
        { "加入名单", "Add" },
        { "先写个名字", "Enter a name first" },
        { "进程名、程序位置、CLI 命令至少填一个，不然认不出它", "Fill in a process name, app location or CLI command so it can be recognized" },
        { "保存失败：{0}", "Save failed: {0}" },
        { "已在格子里", "In a pane" },
        { "启动", "Launch" },
        { "新建 CLI", "New CLI" },
        { "去官网", "Website" },
        { "删除", "Delete" },
        { "桌面端状态：官方钩子", "Desktop status: official hooks" },
        { "桌面端状态：看 CPU 兜底", "Desktop status: CPU fallback" },
        { "CLI 状态：官方钩子", "CLI status: official hooks" },
        { "CLI 状态：看 CPU 兜底", "CLI status: CPU fallback" },
        { "桌面端 已装", "Desktop ✓" },
        { "桌面端 未装", "No desktop app" },
        { "CLI 已装", "CLI ✓" },
        { "CLI 未装（{0}）", "No CLI ({0})" },
        { "自定义", "Custom" },

        // 启动失败的原因
        { "开始菜单里名叫 {0} 的快捷方式", "Start menu shortcut \"{0}\"" },
        { "{0} 桌面端没装", "{0} desktop app isn't installed" },
        { "（找过：{0}）", " (looked for: {0})" },
        { "。安装：{0}", ". Get it: {0}" },
        { "启动失败：{0}", "Launch failed: {0}" },
        { "没有找到 {0}（命令 {1}），请先安装或把命令加入 PATH", "{0} not found (command {1}). Install it or add it to PATH" },
        { "。", "." },
        { "工作目录不存在：{0}", "Folder doesn't exist: {0}" },
        { "启动 CLI 会话需要 Windows Terminal；也可以先自行打开终端，再从菜单纳入。", "CLI sessions need Windows Terminal. Or open a terminal yourself and add it from the menu." },
    };

    static readonly KeyValuePair<string, string>[] ZhPrefixes =
    {
    };

    // 中文显示润色：键 = 代码里的原文，值 = 界面上显示的。没列的原样显示。
    // 用户 2026-09-27：第一版改得太书面，「纳入」「稍后」「窗格」「尚未」读着像英文直译。
    // 口径：中文软件本来会怎么说就怎么说 —— 短、顺口，不硬凑书面词；「格子」保留（比「窗格」自然）。
    static readonly Dictionary<string, string> ZhMap = new Dictionary<string, string>
    {
        // 工具栏
        { "纳入已开的", "收拢窗口" },
        { "重新排版", "重新排列" },
        { "换排版", "换个布局" },
        { "跳过", "下一个" },
        { "把已经开着的客户端推进格子", "把已经打开的窗口收进格子" },
        { "按当前这套排版，把窗口重新贴一遍", "按当前布局重新摆一遍" },
        { "换下一套排版（同样格子数下轮换）", "换成下一种布局" },
        { "开一家的桌面端，或者在某个文件夹里开它的终端", "打开桌面端，或者在某个文件夹里开终端" },
        { "外观、偏好、状态灯钩子、名单、添加窗口、组合、日志", "外观、偏好、状态灯、Agent 列表、组合和日志" },
        { "最小化（格子里的窗口跟着收起）", "最小化（格子里的窗口一起收起）" },
        { "桌面客户端一起关闭；CLI 还原为独立窗口（可在「设置」里调整）", "关闭（桌面端一起关掉，终端变回独立窗口）" },
        { "关掉虚拟窗口（客户端留着，只还成独立窗口）", "关闭（各家窗口留着，变回独立窗口）" },
        { "切回格子视图", "回到网格" },
        { "一次只看一个：等你回复的排队，在干活的收到侧栏", "一次看一个：等你回复的排队，还在跑的放左边" },
        { "这个先不看，排到队尾", "先放一放，排到最后" },
        { "谷歌蓝", "湖蓝" },
        { "经典橙", "陶土" },
        { "外观：{0}（点一下切明暗，右键可选跟随系统）", "外观：{0}（单击切换深浅，右键可跟随系统）" },

        // 格子表头、状态
        { "未接入", "未连接" },
        { "找到窗口，待嵌入", "正在嵌入…" },
        { "没找到启动入口", "找不到启动方式" },
        { "没找到窗口", "没打开" },
        { "启动未完成，可重试", "启动超时，点这里重试" },
        { "还原失败，未关闭", "没能还原，先不关" },
        { "未铺满 · 窗 {0}×{1} / 格 {2}×{3}", "没铺满 · 窗口 {0}×{1} / 格子 {2}×{3}" },
        { "{0} 在跑 · 已经 {1}", "{0} 运行中 · {1}" },
        { "{0} 在问你，等你选", "{0} 在等你确认" },
        { "{0} 做完了 · {1}前", "{0} 做完了 · {1}前" },
        { "{0} 闲着 · {1}", "{0} 空闲 · {1}" },
        { "{0} 没在跑 · {1}", "{0} 未运行 · {1}" },

        // 副标题、布局名
        { "客户端", "桌面端" },
        { "CLI", "终端" },
        { "放大 {0} · 点还原回格子", "{0} 已放大 · 点「还原」回到格子" },
        { "放大 {0} · 点还原回队列", "{0} 已放大 · 点「还原」回到队列" },
        { "队列 · 等你 {0} · 在干活 {1}", "队列 · 待处理 {0} · 进行中 {1}" },
        { "整面", "铺满" },
        { "左右", "左右并排" },
        { "上下", "上下并排" },
        { "田字空一格", "田字（空一格）" },
        { "六宫空一格", "六宫（空一格）" },
        { "左主右二", "左大右二" },
        { "上主下二", "上大下二" },
        { "左主右三", "左大右三" },
        { "上主下三", "上大下三" },
        { "偏横", "横向" },
        { "偏竖", "纵向" },

        // 队列侧栏
        { "排队", "待处理" },
        { "在干活", "进行中" },
        { "没有人在等你", "没有在等你的" },
        { "干完活的会话会排到这里", "做完的会排到这里" },

        // 一句话提示
        { "后面没有别人在等", "后面没有在等你的了" },
        { "先纳入或启动几家，再切到队列", "先收拢或打开几个窗口，再切到队列" },
        { "{0} 在问你，停手后换过去", "{0} 在等你确认，你停下打字就切过去" },
        { "格子已满（最多 {0} 格）", "格子满了（最多 {0} 个）" },
        { "没有找到 {0} CLI，请先安装或加入 PATH", "找不到 {0} 的命令行，请先安装或加进 PATH" },
        { "选择 {0} CLI 的工作目录（每次新建一个独立会话）", "选择 {0} 终端的工作文件夹（每次都开一个新会话）" },
        { "没找到 {0} 的启动入口", "找不到 {0} 的启动方式" },
        { "桌面上没有别的客户端可以纳入", "桌面上没有可以收进来的窗口" },
        { "打不开日志", "日志打不开" },

        // 菜单
        { "把别的窗口放进格子（已经 {0} 格，满了）", "添加其他窗口（{0} 个格子都满了）" },
        { "把别的窗口放进格子（没找到别的窗口）", "添加其他窗口（没有别的窗口）" },

        // 组合
        { "记住顺序、排版和 CLI 目录。\n手动添加的外部窗口需重新纳入。", "会记住顺序、布局和终端文件夹。\n手动添加的其他窗口，恢复后要再添加一次。" },
        { "当前只有外部窗口，没有可保存的 Agent 会话", "现在只有其他窗口，没有可以保存的 Agent" },
        { "组合已保存；下次可从「恢复组合」选择", "组合已保存，下次在「恢复组合」里选" },
        { "组合没有保存成功，请查看日志", "组合没保存成功，详情看日志" },
        { "有窗口未能还原，组合切换已停止", "有窗口没能还原，组合没换成" },
        { "已恢复「{0}」；未打开的会话可点击启动", "已恢复「{0}」，没打开的点一下就能启动" },
        { "组合读取失败，未能恢复", "组合读不出来，没能恢复" },

        // 关闭
        { "{0} 还在跑。关掉虚拟窗口时要连它们一起关吗？\n\n「是」＝一起关掉；「否」＝只收起虚拟窗口，客户端留着；「取消」＝不关了。",
          "{0} 还在运行，要一起关掉吗？\n\n是：一起关掉\n否：只关虚拟窗口，它们留着\n取消：先不关" },
        { "{0} 没能还原成独立窗口，这次先不关宿主，避免把它们的进程一起带走。\n可以点那格的「移出」再试，或者等它响应了再关。",
          "{0} 没能变回独立窗口。为了不把它一起关掉，虚拟窗口先不关。\n可以在那一格点「移出」再试，或者等它有响应了再关。" },

        // 设置页
        { "连 Windows 一起切", "Windows 跟着换" },
        { "跟个性化里选深色 / 浅色一样：各家 agent、任务栏、资源管理器一起变", "相当于在个性化里切深浅色：各家 Agent、任务栏、资源管理器一起变" },
        { "按住空格再按 QWE / ASD 跳格；单独轻敲还是空格", "按住空格再按 QWE / ASD 换格子；单独轻按还是空格" },
        { "做完时闪烁任务栏", "做完了闪一下任务栏" },
        { "某一家做完或在问你、而你不在虚拟窗口里时提醒一下", "你在别的窗口时，有 Agent 做完或在等你确认就提醒一下" },
        { "关掉虚拟窗口时把嵌着的桌面端一起关；终端会话留成独立窗口", "关掉虚拟窗口时，嵌着的桌面端一起关；终端变回独立窗口" },
        { "关闭时一并关闭桌面端", "关闭时连桌面端一起关" },
        { "打开时一并启动桌面端", "打开时连桌面端一起开" },
        { "打开虚拟窗口时把上次那几家没开着的桌面端一起启动", "打开虚拟窗口时，上次那几家没开着的桌面端一起开" },
        { "Agent 名单", "Agent 列表" },
        { "没装钩子（点下面「重新安装钩子」）", "没装钩子（点下面的「重新安装钩子」）" },
        { "已装，还要在 Codex 的钩子管理页信任一次", "已安装，还要在 Codex 的钩子页信任一次" },
        { "已装，重启 OpenCode 后生效", "已安装，重启 OpenCode 后生效" },
        { "已装，重启 Cursor 后生效", "已安装，重启 Cursor 后生效" },
        { "已装，还没收到过消息", "已安装，还没收到消息" },
        { "正常 · 最近一次 {0}", "正常 · {0}" },
        { "已重装。新装的要重启那一家才生效；Codex 改过命令要重新信任", "已重新安装。新装的要重启对应的 Agent 才生效；Codex 要重新信任" },
        { "有钩子没装上，详情见 hooks-report.txt", "有钩子没装上，详情看 hooks-report.txt" },

        // 新会话
        { "更多 agent　没装的、添加自定义", "更多 Agent　没装的、自定义" },
        { "只有终端", "仅终端" },

        // 名单
        { "＋ 添加自定义 agent", "＋ 添加自定义 Agent" },
        { "预设 {0} 家 · 已装 {1} 家", "预设 {0} 个 · 已安装 {1} 个" },
        { " · 自定义 {0} 家", " · 自定义 {0} 个" },
        { "　　没装的照样列着，装好后自动认出来", "　　没装的也列着，装好后自动识别" },
        { "它还在格子里，先移出再删。", "它还在格子里，先移出再删。" },
        { "从名单里删掉「{0}」？（只删名单，不卸载软件）", "从列表里删掉「{0}」？（不会卸载软件）" },
        { "已加入名单：{0}", "已添加：{0}" },
        { "添加自定义 agent", "添加自定义 Agent" },
        { "名字（必填）", "名称（必填）" },
        { "桌面端的进程名", "桌面端进程名" },
        { "可以留空：开着的窗口照样能纳入，只是启动不了", "可以留空：开着的窗口照样能收进来，只是没法从这里启动" },
        { "选桌面端程序", "选择桌面端程序" },
        { "加入名单", "添加" },
        { "先写个名字", "先填个名称" },
        { "进程名、程序位置、CLI 命令至少填一个，不然认不出它", "进程名、程序位置、CLI 命令至少填一个，不然认不出来" },
        { "新建 CLI", "新建终端" },
        { "桌面端状态：看 CPU 兜底", "桌面端状态：按 CPU 估计" },
        { "CLI 状态：看 CPU 兜底", "CLI 状态：按 CPU 估计" },
        { "桌面端 已装", "桌面端已安装" },
        { "桌面端 未装", "桌面端未安装" },
        { "CLI 已装", "CLI 已安装" },
        { "CLI 未装（{0}）", "CLI 未安装（{0}）" },

        // 启动失败的原因
        { "开始菜单里名叫 {0} 的快捷方式", "开始菜单里的「{0}」快捷方式" },
        { "{0} 桌面端没装", "没装 {0} 桌面端" },
        { "。安装：{0}", "。下载地址：{0}" },
        { "没有找到 {0}（命令 {1}），请先安装或把命令加入 PATH", "找不到 {0}（命令 {1}），请先安装或加进 PATH" },
        { "工作目录不存在：{0}", "文件夹不存在：{0}" },
        { "启动 CLI 会话需要 Windows Terminal；也可以先自行打开终端，再从菜单纳入。", "开终端要用 Windows Terminal；也可以自己先打开终端，再从菜单添加。" },
    };
}
