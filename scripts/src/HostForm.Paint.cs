// 【拆分】界面绘制：工具条、格子表头、状态灯、Mica、主题。（从原 AgentHost.cs 原样搬出，逻辑未改）

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

internal sealed partial class HostForm
{
    // 顶上那一条从左到右：一行状态（画在 OnPaint 里）| 按钮们 …… 语言、最小化、最大化、关闭。
    // 按钮宽度按文字量出来，中英文混排不会被截断。
    void LayoutToolbar()
    {
        // 一行状态和按钮直接坐在窗口底上：没有卡片，不写「虚拟窗口」这个名字。
        // （2026-10-12 之前是一张悬浮的白色胶囊卡片，上面一行名字、下面一行状态，高 48。设计评审说这条带子只有左边
        //   一小段有东西、名字每天看也不用再认；先做成试用开关，用户在真宿主上用过说可以，定下来。比胶囊矮 16，给了格子。）
        // 按钮那一排（高 32）上面离窗口上边 BarAir，下面离格子也是 BarAir：正好在窗口上边和格子之间的正中。
        // （试用时第一版是离顶 4、高 40 里居中：按钮上面只有 8、下面却有 16，整排贴着窗口上边。用户看了说
        //   「目前顶边栏总感觉离边框太紧张了，最右边的缩放，最小化，关闭倒是还可以」—— 右上角几个键跟这一排同一条中线，跟着走；
        //   右边留出跟左边一样的边距，跟按钮之间隔开一段。）
        int barY = D(BarAir), barH = D(32);
        int bw = D(46), bh = D(36);
        int by = barY + (barH - bh) / 2;
        _xBtn.Bounds = new Rectangle(ClientSize.Width - D(12) - bw, by, bw, bh);
        _maxBtn.Bounds = new Rectangle(_xBtn.Left - D(2) - bw, by, bw, bh);
        _minBtn.Bounds = new Rectangle(_maxBtn.Left - D(2) - bw, by, bw, bh);
        _maxBtn.Text = WindowState == FormWindowState.Maximized ? "" : "";
        // 这个 ✕ 跟另外两键长得一模一样，语义却最重（默认连四家客户端一起关）。
        string xTip = Prefs.Get("closeagents", true)
            ? "桌面客户端一起关闭；CLI 还原为独立窗口（可在「设置」里调整）"
            : "关掉虚拟窗口（客户端留着，只还成独立窗口）";
        if (xTip != _xTip) { _xTip = xTip; Tip(_xBtn, xTip); }
        // 界面语言：最小化左边的空白处（用户 2026-09-26 指定的位置）。
        // （设计评审提过把语言和明暗这两个按钮收进设置；2026-10-12 做成试用开关给用户试，他没开，定下来不收。）
        if (_langBtn != null) _langBtn.Bounds = new Rectangle(_minBtn.Left - D(10) - bw, by, bw, bh);
        int right = (_langBtn != null ? _langBtn.Left : _minBtn.Left) - D(24);
        GlassButton[] row = new GlassButton[] { _claim, _arrange, _cycle, _view, _launch, _settings, _themeBtn };
        // 字和按钮占的那一块只包住内容，不拉到右上角几个键跟前。
        // need = 左边距 + 状态那一行 + 分隔 + 每个按钮（文字 + 内边距 + 间隔）。
        _titleW = TitleBlockWidth();
        int need = BarPad + _titleW + D(24);
        for (int i = 0; i < row.Length; i++) need += TextW(L.T(row[i].Text), row[i].Font) + D(40);
        int pillW = Math.Min(Math.Max(D(400), need - D(8) + D(20)), Math.Max(D(400), right - D(16)));
        // _pill 是「字和按钮占的那一块」（名字是它还是一张胶囊卡片时留下的），重画时认它：比按钮那一排上下各高出 4 ——
        // 临时的一句话折成两行要 40 高。格子从哪儿排起、哪儿算工具条（拖动窗口、弹窗口菜单）另记在 _toolbarH。
        _pill = new Rectangle(GapPx + D(4), barY - D(4), pillW, barH + D(8));
        _toolbarH = barY + barH + Math.Max(0, D(BarAir) - GapPx);
        int h = barH;
        int y = _pill.Y + (_pill.Height - h) / 2;
        int x = _pill.X + BarPad + _titleW + D(24);
        // 最小尺寸有两条要求，取大的那条：
        //  1) 工具栏整排 + 右上角三键放得下（以前只有这一条）；
        //  2) 每格还剩得下一块能用的内容区。只满足第 1 条的话，缩到最小时
        //     四格每格只有 453×257 逻辑像素 —— 所有 Electron 客户端都顶不住，
        //     会永久停在「未铺满」，而那个提示会让人以为是嵌入坏了。
        int barW = Math.Max(D(960), need + D(32) + D(46) * 4 + D(50));
        int cells = Math.Max(1, _slots.Count);
        int cols = (int)Math.Ceiling(Math.Sqrt(cells));
        int rows = (cells + cols - 1) / cols;
        int gridW = cols * (D(360) + D(16)) + (cols + 1) * GapPx;
        int gridH = ToolbarH + rows * (D(280) + HeaderH + D(8)) + (rows + 1) * GapPx;
        // 别大过屏幕：格子一多这个值会超出工作区，那样窗口就再也缩不动了。
        Rectangle work = Screen.GetWorkingArea(this);
        Size min = new Size(
            Math.Min(Math.Max(barW, gridW), work.Width * 9 / 10),
            Math.Min(Math.Max(D(640), gridH), work.Height * 9 / 10));
        // 最大化的时候别碰 MinimumSize：WinForms 的 setter 会顺手把窗口尺寸夹一遍，
        // 正好把最大化状态弄丢 —— 2026-09-20 实测，加了「按格子数算最小尺寸」之后
        // 宿主开起来就是最小尺寸那么大（1440×1110），不再是最大化。
        // 反正它只在缩小窗口时才起作用，等用户还原成普通窗口再设；
        // 还原会触发 SizeChanged -> Relayout，下一拍就补上了。
        if (WindowState == FormWindowState.Normal && MinimumSize != min) MinimumSize = min;
        for (int i = 0; i < row.Length; i++)
        {
            GlassButton b = row[i];
            b.Quiet = !b.Primary;
            b.Radius = D(8);
            b.Bounds = new Rectangle(x, y, TextW(L.T(b.Text), b.Font) + D(32), h);
            x = b.Right + D(8);
        }
    }

    // 量一次就记住。LayoutToolbar 一趟要量 6 个按钮 + 标题 + 副标题，
    // 以前每量一次新建一个 Graphics（等于取一次窗口 DC），而 Relayout 跟着
    // 每个 pulse-dirty 走 —— 这是在心跳里做重活，宿主一卡格子跟着卡。
    readonly Dictionary<string, int> _textW = new Dictionary<string, int>();

    int TextW(string t, Font f)
    {
        // 字号是「点」，换一块 DPI 不同的屏同样的字宽度就变了，所以 _scale 进 key。
        string key = (t ?? "") + "" + f.Name + "" + f.Size
                   + "" + (int)f.Style + "" + _scale;
        int w;
        if (_textW.TryGetValue(key, out w)) return w;
        using (Graphics g = CreateGraphics())
        {
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            w = (int)Math.Ceiling(g.MeasureString(t ?? "", f).Width);
        }
        _textW[key] = w;
        return w;
    }

    string _xTip;

    Font _subFont, _nameFont;
    Font NameFont { get { return _nameFont ?? (_nameFont = Fonts.Medium(11f)); } }
    Font SubFont { get { return _subFont ?? (_subFont = Fonts.Regular(8.5f)); } }

    // 状态那一行排版时量出来多宽：按钮是照它摆的，画的时候也照它（见 OnPaintBody），不现量。
    // 以前两头各量各的，还都按「此刻那一行写着什么」量：那一行临时换成一句更长的话（Toast，比如
    // 「Codex 在问你，停手后换过去」）时没人重排按钮，字就从这一块里伸出去、压在第一个按钮上
    // （2026-10-12 截图里看到的，中英文都会）。
    int _titleW;

    // 按平常挂着的那一行量（字跟按钮一样大），不算临时的一句话：不然赶上有提示时重排一次，这一块就一直宽着
    int TitleBlockWidth() { return TextW(StandingSubTitle(), Font); }

    // 状态那一行左边离这一块的边多远
    int BarPad { get { return D(6); } }

    // 按钮那一排上面（到窗口上边）和下面（到格子）各留多少，按 96dpi 写。调大了格子跟着往下让（14 让 4、16 让 8）
    const int BarAir = 12;

    // 一句话提示。借状态那一行显示几秒就没 —— 不另开一块 UI，
    // 也不弹模态框（弹框会把焦点从格子里抢走）。
    string _toast;
    DateTime _toastUntil;

    void Toast(string msg)
    {
        _toast = msg;
        _toastUntil = DateTime.Now.AddSeconds(5);
        Invalidate(_pill);
    }

    string SubTitle()
    {
        if (_toast != null && DateTime.Now < _toastUntil) return _toast;
        return StandingSubTitle();
    }

    // 状态那一行平常写的（没有临时提示时）
    string StandingSubTitle()
    {
        int n = _slots.Count == 0 ? _startBtns.Count : _slots.Count;
        int total = Layouts.VariantCount(n);
        string layout = Layouts.NameAt(n, _layoutIx);
        string which = WrapHint(_layoutIx, total);
        if (_slots.Count == 0)
            return L.T(Surface() == "mixed" ? "自由组合" : Surface() == "cli" ? "CLI" : "客户端") + " · " + layout + which;
        if (_queueMode) return QueueSubTitle();
        if (_solo != null) return L.F("放大 {0} · 点还原回格子", _solo.Kind.Name);
        return L.F("{0} 格 · {1}", _slots.Count, layout + which);
    }

    void LayoutStartCards(Rectangle box, bool show)
    {
        int n = _startBtns.Count;
        _startPanes.Clear();
        for (int i = 0; i < n; i++) _startBtns[i].Visible = show;
        if (!show || n == 0) return;
        UseLayoutFor(n);
        Rectangle[] panes = Layouts.Compute(n, _layoutIx, box, GapPx);
        for (int i = 0; i < n && i < panes.Length; i++)
        {
            Rectangle pane = panes[i];
            _startPanes.Add(pane);
            GlassButton b = _startBtns[i];
            b.Location = new Point(
                pane.X + (pane.Width - b.Width) / 2,
                pane.Y + (pane.Height - b.Height) / 2);
        }
    }

    // 已嵌入时的状态文字。铺满就一句「已嵌入」；没铺满就把两边尺寸都写出来，
    // 「窗 1300×800 / 格 1268×711」—— 窗比格大是应用自己的最小尺寸顶住了，
    // 窗比格小是它按记住的旧尺寸改回去了，两种原因处理方式完全不同。
    static string EmbeddedState(Slot s)
    {
        if (!Embed.Misfit(s)) return "已嵌入";
        Rectangle r;
        if (!Embed.ClientScreenRect(s.Hwnd, out r)) return "已嵌入";
        Size sz = s.Site.ClientSize;
        return L.F("未铺满 · 窗 {0}×{1} / 格 {2}×{3}", r.Width, r.Height, sz.Width, sz.Height);
    }

    void HideChrome(Slot s)
    {
        // 只藏表头，不改 Site 尺寸：一改 Resize 会 Fit 到 0，把 Electron 压扁。
        // Site 本身垫在前面那格底下还是藏起来，排版结束时 SyncStack 统一定（见 HostForm.Stack.cs）。
        s.TitleLbl.Visible = false;
        s.StateLbl.Visible = false;
        s.DropBtn.Visible = false;
        if (s.ZoomBtn != null) s.ZoomBtn.Visible = false;
    }

    // 表头左边从左到右：图标 → 名字。
    // （2026-10-12 之前最左边还有一个编号，给 Ctrl+Alt+N 看的。设计评审说它占着最靠前的位置却很少用得上；
    //   先做成试用开关，用户在真宿主上用过说可以，定下来不画。热键还在，第几格照排版顺序数。）
    int IconX(Rectangle pane) { return pane.X + D(12); }
    int NameX(Slot s) { return IconX(s.Pane) + (s.IconBmp != null ? D(20) + D(8) : 0); }

    void PlaceChrome(Slot s)
    {
        // 队列视图里不在中间的：小卡片（左右的签、侧栏的方块）或者藏着。PlaceAll、心跳好几处都会调到这里，
        // 在这儿统一认，免得哪条路径又把藏着的 Site 显示出来（四个 Site 叠在同一个位置，
        // 表头写 OpenCode、里面露出来的却是 Codex —— 2026-09-25 踩到）。
        if (_queueMode && s != QueueFront)
        {
            if (s.Pane.Width > 0) PlaceChip(s); else HideChrome(s);
            return;
        }
        if (!s.TitleLbl.AutoSize) s.TitleLbl.AutoSize = true;   // 侧栏卡片里限过宽，回到表头恢复
        s.TitleLbl.Visible = true;
        s.StateLbl.Visible = true;
        s.DropBtn.Visible = true;
        bool wasHidden = !s.Site.Visible;
        s.Site.Visible = true;
        // 从藏着变成显示：里面的 Chromium 多半已经以为自己被挡住、停在最后一帧不动了（那一帧看着像画好了，
        // 切换动画的「等它画好」也骗得过去）。叫醒它重画 —— 切换时这一下在浮层底下，看不见。
        if (wasHidden) Wake(s, "露面");
        int mid = s.Pane.Y + HeaderH / 2;
        SyncIcon(s);
        int nameX = NameX(s);
        s.TitleLbl.Location = new Point(nameX, mid - s.TitleLbl.Height / 2);
        s.StateLbl.Location = new Point(s.TitleLbl.Right + D(8), mid - s.StateLbl.Height / 2);
        // 表头可以被 config.json 调矮，胶囊不能顶满它，上下各留一点
        int chipH = Math.Min(D(30), HeaderH - D(8));
        // 右边两个方的图标按钮，挨着；是干什么的写在悬停提示里
        // （2026-10-12 之前是写着「放大」「移出」的两个宽按钮，同一次定下来的）
        int chipW = chipH;
        int chipGap = D(4);
        int top = mid - chipH / 2;
        int x = s.Pane.Right - D(12);
        s.DropBtn.Bounds = new Rectangle(x - chipW, top, chipW, chipH);
        // 「移出」是把窗口还成独立窗口，不是关掉它：用「另开窗口」那个图标，不用叉
        SetChip(s.DropBtn, "移出", "");
        // 格子窄到表头放不下时，先藏「放大」再藏「移出」，别压到名字上（双击表头照样能放大）。
        int nameEnd = s.StateLbl.Right + D(8);
        if (s.ZoomBtn != null)
        {
            // 网格和队列同一个按钮：队列里只有中间那张有。
            // 该不该显示先在这儿算好再设，不回头读 Visible：窗体没显示时读回来一律是 false（它连父窗口一起算），
            // 离屏测试里这个按钮就一直没摆上位置。
            bool zoomShown = _slots.Count > 1 && (!_queueMode || s == QueueFront);
            bool zoomedHere = _queueMode ? (_queueZoom && s == QueueFront) : _solo == s;
            SetChip(s.ZoomBtn, zoomedHere ? "还原" : "放大", zoomedHere ? "" : "");
            var zr = new Rectangle(s.DropBtn.Left - chipGap - chipW, top, chipW, chipH);
            if (zoomShown && zr.Left < nameEnd) zoomShown = false;
            if (zoomShown) s.ZoomBtn.Bounds = zr;
            s.ZoomBtn.Visible = zoomShown;
        }
        s.DropBtn.Visible = s.DropBtn.Left >= nameEnd;
        s.Site.Bounds = s.Content;
        bool live = s.Hwnd != IntPtr.Zero && Native.IsWindow(s.Hwnd);
        s.LaunchBtn.Visible = !live;
        s.LaunchBtn.Location = new Point(
            Math.Max(8, (s.Site.ClientSize.Width - s.LaunchBtn.Width) / 2),
            Math.Max(8, (s.Site.ClientSize.Height - s.LaunchBtn.Height) / 2));
        SyncStateLabel(s);
    }

    // 表头右边那两个小按钮：上面是一个图标（Segoe Fluent Icons / MDL2 里的字形），是干什么的写在悬停提示里。
    // 每次排版都会走到这儿，没变就不动（提示反复重设会一直弹不出来）。
    void SetChip(GlassButton b, string tip, string glyph)
    {
        if (b.Text != glyph) b.Text = glyph;
        string had;
        _tipText.TryGetValue(b, out had);
        if (had != tip) Tip(b, tip);
    }

    Font _chipIconFont;
    Font ChipIconFont
    {
        get
        {
            if (_chipIconFont != null) return _chipIconFont;
            _chipIconFont = new Font("Segoe Fluent Icons", 9.5f);
            if (_chipIconFont.Name != "Segoe Fluent Icons") _chipIconFont = new Font("Segoe MDL2 Assets", 9.5f);
            return _chipIconFont;
        }
    }

    // 这一格的图标：窗口在就跟窗口要；还没开的先用名单里那家的（跟「＋ 新会话」面板同一个来源）——
    // 以前只跟窗口要，一家都没开时队列侧栏是一排看不见的空位，只有悬停才知道那儿有东西。
    void SyncIcon(Slot s)
    {
        if (s.Hwnd != IntPtr.Zero && s.IconFrom != s.Hwnd && Native.IsWindow(s.Hwnd))
        {
            Bitmap own = Native.WindowIcon(s.Hwnd);
            if (own != null) s.IconBmp = own;   // 窗口没给图标就留着原来的
            s.IconFrom = s.Hwnd;
        }
        else if (s.IconBmp == null && s.Hwnd == IntPtr.Zero && !s.Custom)
        {
            AgentKind fam = Catalog.ById(s.Kind.FamilyId);
            if (fam != null) s.IconBmp = AgentIcons.Get(fam, D(26));
        }
    }

    // 表头状态：已嵌入就只点一个灯，否则写字。队列视图侧栏的小卡片也用这一段。
    void SyncStateLabel(Slot s)
    {
        bool live = s.Hwnd != IntPtr.Zero && Native.IsWindow(s.Hwnd);
        if (live)
        {
            string embed = s.Embedded ? EmbeddedState(s) : (s.StateLbl.Text == "嵌入失败" ? "嵌入失败" : "找到窗口，待嵌入");
            s.StateLbl.Text = embed;
            // 嵌入正常时只点一个点：它在跑就绿，其它一律灰。
            // 不写「刚回完 / 在等你」之类 —— 状态里的 done 只表示「最后一条是它的回复、
            // 且在 15 分钟内」，既不知道你派没派活、也不知道你在不在等它，写出来就是过度解读。
            bool dot = s.Embedded && embed == "已嵌入";
            s.StateLbl.DotOnly = dot;
            s.StateLbl.Tone = dot
                ? (s.AgentState == "working" ? 2 : s.AgentState == "asking" ? 5 : s.DoneUnseen ? 4 : (s.AgentState == null ? 0 : 3))
                : 0;
        }
        else if (s.StateLbl.Text != "正在启动…" && s.StateLbl.Text != "没找到启动入口")
        {
            s.StateLbl.Text = "没找到窗口";
            s.StateLbl.Tone = 0;
        }
    }

    internal static void RefreshAgentIndicator(Slot s)
    {
        // Only color changes: do not move controls, resize clients or repair focus.
        if (s.StateLbl == null || !s.Embedded || !s.StateLbl.DotOnly) return;
        int tone = s.AgentState == "working" ? 2 : s.AgentState == "asking" ? 5 : s.DoneUnseen ? 4 : (s.AgentState == null ? 0 : 3);
        if (s.StateLbl.Tone == tone) return;
        s.StateLbl.Tone = tone;
        s.StateLbl.Invalidate();
    }

    // 黄灯只有你正看着宿主才看得见 —— 做完了却在别的程序里忙，等于没通知。
    // 闪任务栏按钮，直到宿主（或它的某一格）回到前台。
    bool _flashing;

    void SyncFlash(bool anyUnseen, bool hostInFront)
    {
        bool want = anyUnseen && !hostInFront && Prefs.Get("notify", true);
        if (want == _flashing) return;
        _flashing = want;
        try
        {
            var fi = new Native.FLASHWINFO();
            fi.cbSize = (uint)Marshal.SizeOf(typeof(Native.FLASHWINFO));
            fi.hwnd = Handle;
            fi.dwFlags = want ? (Native.FLASHW_TRAY | Native.FLASHW_TIMERNOFG) : Native.FLASHW_STOP;
            fi.uCount = 0;
            fi.dwTimeout = 0;
            Native.FlashWindowEx(ref fi);
        }
        catch { }
    }

    // 表头上只有一个点（用户定：界面少加小字），细节放悬停里：它在干什么、多久了，
    // 停下来的还说它要你做什么。字怎么拼见 TipNotes。
    // name：侧栏方块上没有名字，那里传表头的全名进来（终端会话带着项目：「Codex CLI · 项目」）
    static string AgentTip(Slot s, string name = null)
    {
        if (!s.Embedded || s.AgentState == null) return null;
        return TipNotes.Tip(name ?? s.Kind.Name, s.AgentState, s.AgentSecs, s.AgentNote);
    }

    // 多了几行字，默认 5 秒就收起来读不完
    readonly ToolTip _tips = new ToolTip { AutoPopDelay = 20000 };

    void SyncTip(Slot s)
    {
        SyncTabNote(s);
        string t = AgentTip(s);
        if (t == s.TipText) return;
        s.TipText = t;
        _tips.SetToolTip(s.TitleLbl, t);
        _tips.SetToolTip(s.StateLbl, t);
    }

    // 背景材质：浅色 Mica；深色用 Acrylic —— 用户要「半透明的那种黑」，深色 Mica 几乎是实心黑，显得闷
    void ApplyBackdrop()
    {
        try
        {
            int bd = Theme.Light ? Native.DWMSBT_MAINWINDOW : Native.DWMSBT_TRANSIENTWINDOW;
            Native.DwmSetInt(Handle, Native.DWMWA_SYSTEMBACKDROP_TYPE, ref bd, 4);
        }
        catch { }
    }

    // 整个客户区交给 Mica：DWM 把边框延伸到整片客户区，alpha=0 的地方就是 Mica。
    // Win10 / 旧版 Win11 不认 SYSTEMBACKDROP_TYPE，退化成普通的系统底色，不影响使用。
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        SyncSpaceHook();
        try
        {
            int dark = Theme.Light ? 0 : 1;
            Native.DwmSetInt(Handle, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, 4);
            ApplyBackdrop();
            var m = new Native.MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            Native.DwmExtendFrameIntoClientArea(Handle, ref m);
            Native.SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_FRAMECHANGED);
        }
        catch { }
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        // 深色：Acrylic 上再压一层半透明黑。只有 Acrylic 的话透出来的是壁纸的模糊，偏中灰，不像「黑」；
        // 压 45% 还看得见后面的模糊，通透感留着
        try { e.Graphics.Clear(Theme.Backdrop); }
        catch (Exception ex) { PaintGuard.Failed(this, ex); }
    }

    // 拖到另一块 DPI 不同的屏上时，别让 WinForms 自己缩放。
    // .NET Framework 版（没在 app.config 里开 PerMonitorV2）根本不处理 WM_DPICHANGED：
    // 窗口保持原像素尺寸，字体不动。新 .NET 默认会按新 DPI 改字体、按系统建议改窗口大小，
    // 而宿主所有尺寸走的是开机时算一次的 _scale（D()），两边一混，表头、胶囊、按钮就对不齐了。
    // 所以这里取消掉，保持跟以前一样；真要做跨屏缩放得连 _scale 一起重算，另说。
    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        e.Cancel = true;
        base.OnDpiChanged(e);
    }

    // 按钮和卡片画的时候直接读 Theme.*，会自己跟上；标签的 ForeColor 是
    // 建的时候抄过去的，得挨个改回来。
    void ReloadTheme(bool force = false)
    {
        bool was = Theme.Light;
        Theme.Load();
        if (was == Theme.Light && !force) return;
        BackColor = Theme.Light ? Color.FromArgb(243, 243, 243) : Color.FromArgb(26, 26, 26);
        ForeColor = Ink;
        try
        {
            int dark = Theme.Light ? 0 : 1;
            Native.DwmSetInt(Handle, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, 4);
        }
        catch { }
        ApplyBackdrop();
        for (int i = 0; i < _slots.Count; i++)
        {
            _slots[i].TitleLbl.ForeColor = Ink;
            _slots[i].StateLbl.ForeColor = Dim;
        }
        Invalidate(true);
    }

    internal static long PaintTicks, PaintCalls;
    protected override void OnPaint(PaintEventArgs e)
    {
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        try { OnPaintBody(e); }
        // 画失败不往外抛：WinForms 会把宿主永久画成红叉，子控件借这里画底（PaintUnder）的也跟着红。见 PaintGuard
        catch (Exception ex) { PaintGuard.Failed(this, ex); }
        finally { PaintTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0; PaintCalls++; }
    }

    void OnPaintBody(PaintEventArgs e)
    {
        base.OnPaint(e);
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        // 子控件重画时也会走到这里（PaintUnder），只画跟重画区域相交的那几块。
        Rectangle clip = Rectangle.Inflate(e.ClipRectangle, D(24), D(24));
        if (clip.IntersectsWith(_pill))
        {
            int tx = _pill.X + BarPad;
            // 还没排过版就先画了一次（.NET 10 启动时会）：那一次现量
            int tw = _titleW > 0 ? _titleW : TitleBlockWidth();
            bool toast = _toast != null && DateTime.Now < _toastUntil;
            string sub = toast ? _toast : StandingSubTitle();
            // 就一行状态，垂直居中。临时的一句话比这一块宽：折成两行写全（用小一号的字）。
            // 不把这一块撑宽 —— 按钮会跟着右移，手正要点的那个就换了位置；也不截成省略号 —— 这种话多半是要紧的。
            var line = new RectangleF(tx, _pill.Y, tw + D(4), _pill.Height);
            if (toast && TextW(sub, Font) > tw + D(4)) Theme.TextWrap(g, sub, SubFont, Ink, line);
            else Theme.Text(g, sub, Font, toast ? Ink : Dim, line, StringAlignment.Near);
            using (var pen = new Pen(Line))
            {
                int lx = tx + tw + D(12);
                g.DrawLine(pen, lx, _pill.Y + D(12), lx, _pill.Bottom - D(12));
            }
        }

        if (_slots.Count == 0)
        {
            for (int i = 0; i < _startPanes.Count; i++)
            {
                Rectangle p = _startPanes[i];
                Theme.Card(g, p, D(12), Pane, _scale);
                if (i < _startKinds.Count)
                    Theme.Text(g, _startKinds[i].Name, NameFont, Ink,
                        new RectangleF(p.X + D(18), p.Y, p.Width - D(36), HeaderH), StringAlignment.Near);
            }
        }
        if (_queueMode && _slots.Count > 0) PaintQueueExtras(g, clip);
        foreach (int i in PaintOrder())
        {
            Rectangle p = _slots[i].Pane;
            if (p.Width <= 0 || !clip.IntersectsWith(p)) continue;
            // 队列视图里不在中间的小卡片：只画图标（灯是子控件）。侧栏的方块没有底，鼠标停着时垫一层浅底；
            // 右边的索引签有底，连同悬停的浅底已经在 PaintQueueExtras 里跟页边一起画了
            if (_queueMode && _slots[i] != QueueFront)
            {
                if (_slots[i] == _railHot && TabOf(_slots[i]) < 0 && LeftTabOf(_slots[i]) < 0)
                    using (GraphicsPath hp = Theme.Round(new RectangleF(p.X, p.Y, p.Width, p.Height), D(10)))
                    using (var hb = new SolidBrush(Theme.BtnHot))
                        g.FillPath(hb, hp);
                if (_slots[i].IconBmp != null) Theme.Icon(g, _slots[i].IconBmp, ChipIconRect(_slots[i]), D(4), D(7));
                continue;
            }
            Theme.Card(g, p, D(12), Pane, _scale);
            bool chip = false;
            if (_dragging && _dragTarget == _slots[i])
                using (var targetPen = new Pen(Theme.Brand, D(3)))
                    g.DrawRectangle(targetPen, p.X + D(3), p.Y + D(3), p.Width - D(6), HeaderH - D(6));
            // 键盘打进哪一格，以前界面上一点线索都没有。给有焦点的那格描一圈。
            if (_slots[i].Hwnd != IntPtr.Zero && _slots[i].Hwnd == _lastFocused)
            {
                float bw = Math.Max(2f, D(2));
                using (GraphicsPath ring = Theme.Round(
                           new RectangleF(p.X + bw / 2f, p.Y + bw / 2f, p.Width - bw, p.Height - bw), D(12)))
                using (var rp = new Pen(Theme.Brand, bw))
                    g.DrawPath(rp, ring);
            }
            int hh = chip ? p.Height : HeaderH;
            if (!chip)
                using (var pen = new Pen(Line))
                    g.DrawLine(pen, p.X + D(12), p.Y + HeaderH - 1, p.Right - D(12), p.Y + HeaderH - 1);
            Bitmap ib = _slots[i].IconBmp;
            if (ib != null)
            {
                int sz = D(20);
                var ir = new Rectangle(IconX(p), p.Y + (hh - sz) / 2, sz, sz);
                Theme.Icon(g, ib, ir, D(3), D(6));   // 按图标深浅垫底，见 Theme.Icon
            }
        }
    }

    // 「田字 1/5」——不写出来就不知道后面还有没有排版可换。
    static string WrapHint(int ix, int total)
    {
        if (total <= 1) return "";
        int i = ix % total;
        if (i < 0) i += total;
        return "  " + (i + 1) + "/" + total;
    }
}
