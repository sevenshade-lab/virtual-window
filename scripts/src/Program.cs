// 【拆分】命令行入口（--claim）、单实例、异常兜底。（从原 AgentHost.cs 原样搬出，逻辑未改）

using System;
using System.Drawing;
using System.Text;
using System.Windows.Forms;

// 独立虚拟窗口：各家 agent 进等大格子。

internal static class Program
{
    // 命令行：--claim = 开起来就「纳入已开的」。已经开着的话，把这句话转给那个宿主。
    // 外部程序要打开它并纳入已开的窗口时就这么叫。
    public const int WM_APP_CLAIM = 0x8000 + 0x51;
    public const int WM_APP_COLLAPSE = 0x8000 + 0x52;
    // 等于点一下「换排版」。外面（别的程序、测试脚本）要切排版时发这个，不用去点按钮。
    public const int WM_APP_CYCLE = 0x8000 + 0x53;
    // 切换 网格 ⇄ 队列 视图（外面、测试脚本用）
    public const int WM_APP_VIEW = 0x8000 + 0x54;
    // 打开「Agent 名单」页（外面、测试脚本用）
    public const int WM_APP_ROSTER = 0x8000 + 0x55;
    // 放大 / 还原：网格里是有焦点那格，队列里是中间那张（外面、测试脚本用；以前是「完成」）
    public const int WM_APP_ZOOM = 0x8000 + 0x56;

    [STAThread]
    static void Main(string[] args)
    {
        // CLI 会话的钩子调的是这条（见 CliStates）：写个状态文件就走，
        // 不注册编码、不碰单实例互斥、不起界面，也**不往标准输出写任何东西**——
        // Claude Code 会把 UserPromptSubmit 钩子的输出拼进对话上下文。
        // 兼容：旧版按 --settings 起的 CLI 会话钩子还在调这条（现在由 agenthost-hook.cjs 接手）。
        // 删了的话那些钩子一触发就会把宿主界面弹出来。
        if (args.Length >= 2 && args[0] == "--cli-state") { CliStates.Write(args[1]); return; }
        // 调试：名单里每家装没装、状态从哪来，写到 %LOCALAPPDATA%\AgentHostgents-report.txt
        if (args.Length >= 1 && args[0] == "--list-agents") { Catalog.WriteReport(); return; }
        // 装 / 卸各家的状态钩子（HookSetup），结果写 %LOCALAPPDATA%\AgentHost\hooks-report.txt
        if (args.Length >= 1 && args[0] == "--install-hooks") { HookSetup.ApplyToFile(true); return; }
        if (args.Length >= 1 && args[0] == "--uninstall-hooks") { HookSetup.ApplyToFile(false); return; }
        ResetInheritedTz();
        // .NET 默认只带 UTF-8/UTF-16/ASCII 等几种编码，Encoding.GetEncoding(936)（GBK）之类
        // 会直接抛异常；Framework 里是系统自带的。先注册上，以后读终端吐出来的 GBK 文本不会炸。
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        bool claim = Array.IndexOf(args, "--claim") >= 0;
        // DPI 感知只由 AgentHost.manifest 决定（PerMonitorV2，进程一启动系统就按它设好）。
        // 以前这里还调一次 SetProcessDpiAwareness(2)（PerMonitor v1）：manifest 已经设过，
        // 那一下必然失败、从来没生效过，只会让人以为是 v1。新 .NET 里也不调
        // Application.SetHighDpiMode —— 两处各设一次就是 WFO0003 说的冲突，留 manifest 一处。

        bool fresh;
        using (var mtx = new System.Threading.Mutex(true, @"Local\VirtualWindowAgentHost", out fresh))
        {
            if (!fresh)
            {
                IntPtr exist = Native.FindWindow(null, HostForm.Title);
                if (exist != IntPtr.Zero)
                {
                    Surface(exist);
                    if (claim) Native.PostMessage(exist, WM_APP_CLAIM, IntPtr.Zero, IntPtr.Zero);
                }
                return;
            }
            Cfg.Load();
            // 上一任宿主没来得及还原的「已关节流」，这里认领回来，退出时一并还原。
            try { Qos.AdoptLeftovers(); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            // 新 .NET 的默认字体从「系统 GUI 字体」（中文系统上是宋体 9pt）换成了 Segoe UI 9pt，
            // Segoe UI 没有中文字形。宿主自己的控件都显式设了雅黑，这里把兜底也设成雅黑，
            // 漏设字体的地方（以后新加的对话框、菜单项）不会冒出一块 Segoe UI 回退字。
            // 必须在建任何窗口之前调。
            Application.SetDefaultFont(Fonts.Regular(9f));
            var form = new HostForm();
            if (claim) form.Shown += delegate { form.BeginInvoke(new Action(form.ClaimFromOutside)); };
            Application.ThreadException += delegate(object s, System.Threading.ThreadExceptionEventArgs e) { OnUiException(e.Exception); };
            AppDomain.CurrentDomain.UnhandledException += delegate { form.EmergencyRelease(); };
            Application.ApplicationExit += delegate { Watch.ReleaseAll(true); };
            AppDomain.CurrentDomain.ProcessExit += delegate { Watch.ReleaseAll(true); };
            Application.Run(form);
        }
    }

    // 宿主已经开着、又被启动了一次（Start-Host.cmd、快捷方式、别的程序带 --claim 启动）：把已有的那个窗口叫到前面。
    // 只有最小化着才还原 —— 还原回它最小化之前的样子，原来最大化的还是最大化。
    // 以前不管什么状态都 ShowWindow(SW_RESTORE)：对最大化着的窗口那是「还原成普通窗口」，想把它叫到前面，
    // 它却不再是最大化了；对建好了还没显示的窗口（宿主正在启动）则是提前把它显示出来。
    // 2026-10-12 换版后发现宿主成了普通窗口（尺寸跟最大化时一样大，底边十几个像素在任务栏后面，不容易发觉），
    // 查的时候看到这一处。那一次是不是它造成的没有证据。
    internal static void Surface(IntPtr exist)
    {
        if (Native.IsIconic(exist)) Native.ShowWindow(exist, Native.SW_RESTORE);
        Native.SetForegroundWindow(exist);
    }

    // 界面线程上漏出来的异常（某个按钮、定时器、消息处理里抛的）：记下来，宿主照常跑。
    // 以前这里接的是 EmergencyRelease —— 停心跳、把各格还成顶层窗口，本意是「宿主要死了，先把人家的窗口还回去」。
    // 可挂了 ThreadException 之后 WinForms 并不会让进程退出：一次异常过后宿主还开着，心跳却永久停了，
    // 灯不再更新、队列不再换人，各格还被踢了出去。2026-10-12 就是这样「半死」了 17 分钟，直到用户让重开
    //（focus.log 里最后一条 state … -> … 停在出事那一刻，钩子的会话文件却一直在更新）。
    // 真要退的时候（UnhandledException、ProcessExit、ApplicationExit）才还窗口，那几处没动。
    internal static void OnUiException(Exception ex)
    {
        try
        {
            string text = ex == null ? "(null)" : ex.ToString();
            if (text.Length > 3000) text = text.Substring(0, 3000) + " …";
            Log.W("exception on ui thread (host keeps running): " + text);
        }
        catch { }
    }

    // 谁把宿主拉起来，宿主就继承谁的环境变量，再原样传给它启动的桌面端。
    // 拉起宿主的程序如果把自己的 TZ 钉成了 CST-8 这类值并留给子进程，宿主
    // 就带着这个值去开 Grok Bot：Electron 不认识 CST-8，时区成了 Etc/Unknown，聊天区一排时间就抛
    // RangeError，整块显示「出错了」，重新加载、从格子里重开都没用（2026-10-11 用户报；同一刻
    // OpenCode、Antigravity 也带上了，商店版的 Claude / Codex 走系统激活，不继承）。
    // 桌面端该拿到的是用户自己在系统里配的 TZ（多数人没配），跟从开始菜单打开时一样，
    // 不是拉起宿主那一方的。宿主自己用不到 TZ（.NET 在 Windows 上只认系统时区）。
    static void ResetInheritedTz()
    {
        try
        {
            string had = Environment.GetEnvironmentVariable("TZ");
            string own = Environment.GetEnvironmentVariable("TZ", EnvironmentVariableTarget.User);
            if (string.IsNullOrEmpty(own)) own = Environment.GetEnvironmentVariable("TZ", EnvironmentVariableTarget.Machine);
            if (string.IsNullOrEmpty(own)) own = null;
            if (had == own) return;
            Environment.SetEnvironmentVariable("TZ", own);
            Log.W("env: 继承到 TZ=" + (had ?? "（无）") + "，桌面端按系统里配的来：" + (own ?? "（没配，去掉）"));
        }
        catch { }
    }
}
