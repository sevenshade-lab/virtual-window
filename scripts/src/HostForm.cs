// 【拆分】宿主主窗体：字段、构造、工具栏按钮的创建。（从原 AgentHost.cs 原样搬出，逻辑未改）

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

internal sealed partial class HostForm : Form
{
    public const string Title = "虚拟窗口";
    const int Slack = 16;

    // 概念稿 2「悬浮卡片」：格子是漂在 Mica 上的白卡片；顶上一行工具条直接坐在窗口底上
    //（2026-10-12 之前工具条也是一张悬浮的胶囊卡片）。
    // 所有尺寸按 96dpi 写，D() 换成当前屏幕的像素（宿主是 per-monitor DPI aware）。
    float _scale = 1f;
    int D(int v) { return (int)Math.Round(v * _scale); }
    Rectangle _pill;
    // _pill：工具条上字和按钮占的那一块。_toolbarH：工具条的下边，格子从这儿再隔一道间隔排起。都在 LayoutToolbar 里定
    int _toolbarH;
    int ToolbarH { get { return _toolbarH; } }
    int HeaderH { get { return D(Cfg.Header); } }
    int GapPx { get { return D(Cfg.Gap); } }

    readonly List<Slot> _slots = new List<Slot>();
    readonly List<GlassButton> _startBtns = new List<GlassButton>();
    readonly List<AgentKind> _startKinds = new List<AgentKind>();
    readonly List<Rectangle> _startPanes = new List<Rectangle>();
    readonly Timer _tick;
    readonly Timer _resizeTick;
    LayoutTransition _transition;
    bool _interactiveResize, _resizePending;
    readonly GlassButton _claim;
    readonly GlassButton _arrange;
    readonly GlassButton _cycle;
    readonly GlassButton _view;
    readonly GlassButton _launch;
    readonly GlassButton _settings;
    DateTime _lockUntil = DateTime.MinValue;
    bool _placing;
    int _layoutIx;
    int _layoutForN = -1;
    Slot _solo;

    static Color Pane { get { return Theme.Pane; } }
    static Color Line { get { return Theme.Line; } }
    static Color Ink { get { return Theme.Ink; } }
    static Color Dim { get { return Theme.Dim; } }

    public HostForm()
    {
        Text = Title;
        Name = "AgentHostForm";
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
        // 宿主永远不接管输入法，IME 一律归被点的那个子窗口。
        ImeMode = ImeMode.Disable;
        KeyPreview = false;
        Theme.Load();
        L.Load();
        // BackColor 只给 WinForms 自己用；真正的底是 Mica，OnPaintBackground 不画它。
        BackColor = Theme.Light ? Color.FromArgb(243, 243, 243) : Color.FromArgb(26, 26, 26);
        ForeColor = Ink;
        Font = Fonts.Regular(9.5f);
        MinimumSize = new Size(720, 480);
        StartPosition = FormStartPosition.Manual;
        FormBorderStyle = FormBorderStyle.Sizable;
        // 上次关的时候在哪块屏、多大、是不是最大化。以前一律主屏最大化 ——
        // 在副屏摆好、或者不想占满整屏的，重启一次就白摆。
        Rectangle saved = Prefs.GetRect("bounds");
        bool onScreen = false;
        if (saved.Width >= 400 && saved.Height >= 300)
            foreach (Screen sc in Screen.AllScreens)
                if (sc.WorkingArea.IntersectsWith(saved)) { onScreen = true; break; }
        if (onScreen)
        {
            Bounds = saved;
            WindowState = Prefs.Get("maximized", true)
                ? FormWindowState.Maximized : FormWindowState.Normal;
        }
        else WindowState = FormWindowState.Maximized;   // 没存过，或者那块屏不在了
        _wantSolo = Prefs.GetStr("solo", "");
        DoubleBuffered = true;
        using (Graphics g = CreateGraphics()) _scale = Math.Max(1f, g.DpiX / 96f);

        _claim = MkBtn("纳入已开的", 14, 8, 100, 32);
        _claim.Click += delegate { ClaimRunning(true); };
        Controls.Add(_claim);

        _arrange = MkBtn("重新排版", 120, 8, 84, 32);
        _arrange.Click += delegate { Relayout(true); };
        Controls.Add(_arrange);

        _cycle = MkBtn("换排版", 210, 8, 76, 32);
        _cycle.Click += delegate { CycleLayout(); };
        Controls.Add(_cycle);

        // 网格 ⇄ 队列（队列视图见 HostForm.Queue.cs）
        _view = MkBtn("队列", 290, 8, 64, 32);
        _view.Click += delegate { ToggleQueueMode(); };
        Controls.Add(_view);
        _queueMode = Prefs.Get("queue", false);

        // 「＋ 新会话」：点开是图标面板（HostForm.NewSession.cs），代替以前的「启动 ▾」长菜单
        _launch = MkBtn("＋ 新会话", 370, 8, 96, 32);
        _launch.Primary = true;
        // 「设置」：以前挤在「启动 ▾」菜单里的纳入别的窗口、方案、偏好、日志
        _settings = MkBtn("设置", 470, 8, 56, 32);
        _settings.Click += ShowSettings;
        Controls.Add(_settings);
        CreateThemeButton();

        _minBtn = MkCap("", delegate { WindowState = FormWindowState.Minimized; });
        _maxBtn = MkCap("", delegate
        {
            WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;
        });
        _maxBtn.NativeMaximize = true;
        _xBtn = MkCap("", delegate { Close(); });
        _xBtn.Danger = true;
        _launch.Click += ShowNewSession;
        Controls.Add(_launch);


        // 空白页的启动卡片：只列装了桌面端的（名单十几家，其余的在「＋ 新会话」里），最多 6 张
        foreach (AgentKind k in Catalog.All)
        {
            if (!Catalog.HasDesktop(k) || _startKinds.Count >= 6) continue;
            _startKinds.Add(k);
            GlassButton b = MkBtn("启动 " + k.Name, 0, 0, D(160), D(40));
            AgentKind captured = k;
            b.Click += delegate { AddOrLaunch(captured); };
            _startBtns.Add(b);
            Controls.Add(b);
        }

        Tip(_claim, "把已经开着的客户端推进格子");
        Tip(_arrange, "按当前这套排版，把窗口重新贴一遍");
        Tip(_cycle, "换下一套排版（同样格子数下轮换）");
        Tip(_launch, "开一家的桌面端，或者在某个文件夹里开它的终端");
        Tip(_settings, "外观、偏好、状态灯钩子、名单、添加窗口、组合、日志");
        Tip(_minBtn, "最小化（格子里的窗口跟着收起）");
        CreateLangButton();
        SyncViewButton();

        _tick = new Timer();
        _tick.Interval = 700;
        _tick.Tick += delegate { Pulse(); };
        _tick.Start();
        HandleCreated += delegate { StartNotifyWatch(); };
        if (IsHandleCreated) StartNotifyWatch();

        // 悬停判定得比心跳细，但它只是取一下光标位置再跟几个矩形比，很便宜。
        _hover = new Timer();
        _hover.Interval = 120;
        _hover.Tick += delegate { HoverTick(); };
        _hover.Start();
        Load += delegate { Attach(HostWins.Visible()); Relayout(true); };
        // 窗口先画出来，再去启动没开着的那几家（见 LaunchClosedOnOpen）
        Shown += delegate { BeginInvoke(new Action(LaunchClosedOnOpen)); };

        // ResizeEnd 拖完大小、拖完位置都会来一次，存这一下就够
        //（LocationChanged 一次拖动来几十下，每下写一次盘没必要）
        _resizeTick = new Timer();
        _resizeTick.Interval = 32;
        _resizeTick.Tick += delegate
        {
            if (!_resizePending) return;
            _resizePending = false;
            if (WindowState != FormWindowState.Minimized) Relayout(false);
        };
        ResizeBegin += delegate { _interactiveResize = true; _resizeTick.Start(); };
        ResizeEnd += delegate
        {
            _interactiveResize = false;
            _resizePending = false;
            _resizeTick.Stop();
            SaveBounds();
            Relayout(true);
        };
        SizeChanged += delegate
        {
            if (_interactiveResize) _resizePending = true;
            else if (WindowState != FormWindowState.Minimized) Relayout(false);
        };
        MouseDoubleClick += OnHeaderDoubleClick;
        FormClosing += OnHostClosing;
        WireDrag(this);
        MouseMove += RailHover;
        MouseLeave += delegate { RailHover(this, new MouseEventArgs(MouseButtons.None, 0, -1, -1, 0)); };
        MouseCaptureChanged += delegate { if (!Capture) EndSlotDrag(false); };
        LoadLastSlots();
    }

    GlassButton MkBtn(string text, int x, int y, int w, int h)
    {
        var b = new GlassButton();
        b.Text = text;
        b.Radius = D(20);   // 全圆角胶囊（Google 的按钮形状）；Round 会按高度截到半圆
        b.Bounds = new Rectangle(x, y, w, h);
        return b;
    }

    // 自己画的窗口三键。系统的那套在自定义边框下会被画到窗口外面（实测被裁一半），
    // 所以去掉 WS_SYSMENU 隐藏系统三键；保留 WS_CAPTION 让系统播放窗口动画。
    GlassButton _minBtn, _maxBtn, _xBtn;
    bool _captionMaxDown;

    GlassButton MkCap(string glyph, EventHandler click)
    {
        var b = MkBtn(glyph, 0, 0, D(46), D(36));
        b.Quiet = true;
        b.Radius = D(6);
        b.Font = new Font("Segoe Fluent Icons", 8f);
        if (b.Font.Name != "Segoe Fluent Icons") b.Font = new Font("Segoe MDL2 Assets", 8f);
        b.Click += click;
        Controls.Add(b);
        return b;
    }

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            // Keep WS_CAPTION for Windows minimize/restore transitions. WM_NCCALCSIZE
            // below removes the visual title bar while retaining our custom chrome.
            cp.Style |= 0x00C00000;
            cp.Style &= ~0x00080000;   // WS_SYSMENU: custom caption buttons already provide these actions.
            return cp;
        }
    }

    // 表头右边的小图标按钮。上面的图标和悬停提示排版时才定（SetChip）
    GlassButton MkChip()
    {
        GlassButton b = MkBtn("", 0, 0, D(30), D(30));
        b.Quiet = true;
        b.Radius = D(8);
        b.Font = ChipIconFont;   // 大家共用一份
        return b;
    }

    GlassLabel MkLabel(string text, int x, int y, int w, int h)
    {
        var l = new GlassLabel();
        l.Text = text;
        l.Bounds = new Rectangle(x, y, w, h);
        l.ForeColor = Ink;
        l.BackColor = Color.Transparent;
        l.AutoSize = false;
        return l;
    }

    // 自己的菜单开着的时候别抢焦点：交焦点会把菜单顶掉。
    bool _menuOpen;
}
