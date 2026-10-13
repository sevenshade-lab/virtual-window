// 【拆分】格子的数据：AgentKind / WinInfo / Slot。（从原 AgentHost.cs 原样搬出，逻辑未改）

using System;
using System.Drawing;
using System.Windows.Forms;

internal sealed class AgentKind
{
    public string Id;
    public string Name;
    public string[] Processes;
    public string[] CliProcesses;
    public string TitleMark;
    public bool Cli;
    public string FamilyId;
    public string Workspace;
    // ---- 名单（Catalog）里的启动与状态信息 ----
    public string[] Exes;          // 桌面端可能的安装位置（可含 %LOCALAPPDATA% 之类）
    public string[] Shortcuts;     // 开始菜单快捷方式名（装哪儿都找得到）
    public string AppsFolder;      // 商店版：shell:AppsFolder\<这个>
    public string CliCommand;      // CLI 命令名（不带扩展名）
    public string[] CliPaths;      // CLI 不在 PATH 时可能的位置
    public string Home;            // 没装时给的官网 / 安装地址
    // 状态从哪来：hooks（官方钩子写的会话文件，HookStates）/ cpu（看进程忙不忙，CpuStates 兜底）。
    public string DesktopState = "cpu";
    public bool HookFallbackCpu;   // 钩子还没数据时退回看 CPU（别家显示未知，因为 CPU 猜不准）
    // Electron / Chromium 系桌面端：宿主启动时带 Launch.EmbedArgs（关 DirectComposition + 关窗口遮挡计算），
    // 不带的话嵌成子窗口后换页丢帧、被误判成「被遮挡」而降速停画（Codex 切对话白屏、要等）。
    public bool Chromium;
    public string DesktopArgs;     // 启动桌面端额外带的参数（Cursor 的 --glass：只开 Agents 窗）
    public bool Custom;           // 用户在 agent-catalog.json 里自己加的
    public static AgentKind Session(AgentKind family, string id, string workspace)
    {
        var k = new AgentKind(id, family.Name + " CLI", family.Processes, family.CliProcesses);
        k.Cli = true;
        k.FamilyId = family.Id;
        k.Workspace = workspace;
        k.CliCommand = family.CliCommand;
        k.CliPaths = family.CliPaths;
        k.Home = family.Home;
        k.TitleMark = "虚拟窗口 · " + family.Name + " CLI [" + id + "]";
        return k;
    }
    public AgentKind(string id, string name, string[] processes, string[] cliProcesses)
    {
        Id = id;
        FamilyId = id;
        Name = name;
        Processes = processes;
        CliProcesses = cliProcesses;
        TitleMark = "虚拟窗口 · " + name;
    }
}

internal sealed class WinInfo
{
    public IntPtr Handle;
    public string Title;
    public string ProcessName;
    public string ExePath;
    public int Pid;
    public int Area;
}

internal sealed class Slot
{
    public AgentKind Kind;
    public IntPtr Hwnd;
    public Rectangle Pane;
    public Rectangle Content;
    public Panel Site;
    public GlassButton LaunchBtn;
    public GlassButton DropBtn;
    public GlassButton ZoomBtn;
    public GlassLabel TitleLbl;
    public GlassLabel StateLbl;
    public int SavedStyle;
    public int SavedEx;
    public bool Embedded;
    // 连续几次 Fit 后窗口尺寸仍与格子不一致。超过阈值就不再每拍硬压
    // （Electron 会按自己的 min/max 尺寸顶回来，反复压只会闪），改为把
    // 实际尺寸显示在状态里，让人看出是被撑大还是被缩小。
    public int MisfitStreak;
    // 这家客户端自己的图标（从它的窗口要），画在卡片表头名字左边。
    public Bitmap IconBmp;
    public IntPtr IconFrom;
    // 这家 agent 在干什么（working/done/idle/stopped），以及已经这样多久了。
    public string AgentState;
    public int AgentSecs;
    // 钩子顺带记下的：停在哪个工具上、最后说的话、在哪个目录（悬停提示用）。状态不是钩子来的就是 null。
    public HookNote AgentNote;
    public string TipText;
    // 索引签上写的那一小句（它停下来要你做什么）：排版时照它定签的高矮、画的时候写它；心跳里发现变了再改
    public string TabNote;
    // 它做完的那一下，你有没有点进来看过（「你上次看这格」存在 Seen 里，跨重启）。
    public bool DoneUnseen;
    // 客户端自己把窗口藏起来（点了它自己的 ✕、收进托盘）是什么时候。
    public DateTime HiddenSince;
    // 窗口没了之后进入「待关」观察期：它自己又开回来就取消，真没回来才收进程树。
    public DateTime ClosingSince;
    public int ClosingPid;
    // 你在宿主里点这一格 ✕ 的时刻。只有「点了 ✕ 之后才藏起来」才算你要关它 ——
    // 2026-09-27 Cursor 的 Agents 窗自己藏了几秒，被当成关掉、整棵进程树被收了三次（用户看到的是「异常关闭」）。
    public DateTime CloseClickedAt;
    public bool ClosingByUser;
    // 没点 ✕ 它自己藏了、被我们显示回来的时刻，和两分钟内连着几次（只写日志用）。2026-09-28 Cursor 自己藏了，
    // 宿主把它脱开成「独立窗口」—— 可窗口还是藏着的，用户看到的就是 Cursor 又自己关了。
    public DateTime ReshownAt;
    public int ReshownCount;
    // 自己从「把别的窗口放进格子」挑进来的，不在 Catalog 名单里。
    // 跟四家的区别：窗口没了就把格子撤掉（别按进程名再找一个顶上），
    // 也绝不因为「关虚拟窗口」或者「它自己藏起来了」去收它的进程树 ——
    // 那可能是用户的文件管理器、浏览器，不是我们起的。
    public bool Custom;
    // CPU 兜底判状态时这一刻之前不算数（刚换到队列中间，重画会冒一阵 CPU）
    public DateTime CpuMuteUntil;
    public IntPtr CpuHwnd;         // CPU 兜底上次看的是哪个窗口：换了（刚嵌进来）就先静默一阵
}
