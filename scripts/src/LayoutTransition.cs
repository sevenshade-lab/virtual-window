using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

// 换排版的「收缩再展开」。客户端窗口本身不做动画（逐帧改 Electron 的尺寸会一帧帧重排，
// 看着别扭），全在一层浮层里用截图演：
//   1. 截下每一格，在浮层里缩到 90% 并淡掉（四格错开）；
//   2. 这期间真窗口已经在浮层底下排到新位置、按新尺寸画好；
//   3. 再截一次（浮层设了「截屏时跳过」，截到的正是底下的新排版），
//      每格从 90% 放大淡入，播完揭开浮层。
// 卡片底下铺的是「底板」：整张截图把格子位置涂成底色、缝里保留原样（Mica 截不到没有卡片的样子）。
// 所以第一帧 = 盖上前的屏幕，最后一帧 = 揭开后的屏幕，首尾都不跳。
// 浮层不抢焦点、点击穿透，所以输入法和「点格子交焦点」那套都不受影响。
//
// 另一种演法 Morph（队列视图用）：每张卡片从旧位置平滑移动、缩放到新位置，途中从旧截图淡到新截图。
// 被别的卡片挡住的（队列里叠在后面的那几张）截不到完整的样子，用一张空白卡片代替（Plain）。
//
// 还有一种 Turn（队列里换人，2026-10-12）：先照淡出那样整张旧截图盖着，等底下新换上的那家画好，
// 把它截下来当底，再把旧截图里中间那张卡片像书页一样绕着左边往外翻过去（见 DrawTurn）。
//
// 画面由单独的渲染线程画、每帧 DwmFlush 对齐屏幕刷新：界面线程跟四家客户端共用输入队列，
// 放在 WinForms Timer 上，心跳、客户端消息插个队就掉一帧（实测最长 30ms 的间隔）。
internal sealed class LayoutTransition : Form
{
    const int OutMs = 140;
    const int InMs = 200;
    const int StaggerMs = 25;
    const float MinScale = 0.9f;
    const int GrabAtMs = 100;      // 开播后至少等这么久才截新排版：客户端按新尺寸重画要几十毫秒（60ms 时 Cursor 还没画完）
    const int MorphMs = 420;
    const int MorphFadeMs = 140;
    const int MorphGrabAtMs = 30;  // 队列视图里客户端尺寸不变，只是换哪一格显示，不用等它重画
    const int FadeHoldMs = 30;     // Fade：先盖满一小会儿，等新换上的那家画出第一帧（60 手感发粘，2026-09-26 减半）
    const int FadeMs = 160;
    const int TurnMs = 380;        // Turn：一页翻过去多久
    const int TurnBaseFadeMs = 120;   // Turn：页以外的地方（侧栏、页边、索引签）从旧画面淡到新画面多久
    const int WS_EX_LAYERED = 0x00080000;
    const int WS_EX_TRANSPARENT = 0x00000020;
    const int WS_EX_NOACTIVATE = 0x08000000;
    const int MA_NOACTIVATE = 3;

    readonly Rectangle _area;      // host 客户区坐标
    readonly Rectangle[] _from;    // 浮层坐标
    readonly bool _morph;
    bool _fadeOnly;                // Fade：整张旧截图盖着、整窗透明度淡掉（队列里换人用）
    readonly bool[] _fromPlain;
    bool[] _toPlain;
    int[] _order;                  // Morph 时的叠放顺序（先画的在下面）
    readonly float _radius;
    readonly Color _cardColor;
    readonly Color _bg;
    readonly Frame _old;
    Frame _new;
    Rectangle[] _to;

    Thread _render;
    volatile bool _stop;
    IntPtr _hwnd;
    IntPtr _hostHwnd;
    readonly int _slow = SlowFactor();
    bool _hiddenFromCapture;

    // 一次截图切出来的东西：底板 + 每格卡片，都常驻在 GDI 内存 DC 里。
    sealed class Frame : IDisposable
    {
        public GdiImage Plate;
        public GdiImage[] Cards;

        public static Frame From(Bitmap shot, Rectangle[] rs, Color bg, bool[] plain)
        {
            var f = new Frame { Cards = new GdiImage[rs.Length] };
            var bounds = new Rectangle(0, 0, shot.Width, shot.Height);
            for (int i = 0; i < rs.Length; i++)
            {
                if (plain != null && i < plain.Length && plain[i]) continue;
                Rectangle r = Rectangle.Intersect(rs[i], bounds);
                if (r.Width <= 0 || r.Height <= 0) continue;
                using (Bitmap b = shot.Clone(r, PixelFormat.Format32bppRgb))
                    f.Cards[i] = new GdiImage(b);
            }
            using (Bitmap plate = shot.Clone(bounds, PixelFormat.Format32bppRgb))
            {
                using (Graphics g = Graphics.FromImage(plate))
                using (var br = new SolidBrush(bg))
                    foreach (Rectangle r in rs)
                        if (r.Width > 0) g.FillRectangle(br, Rectangle.Inflate(r, 3, 3));   // 连卡片阴影一起涂掉
                f.Plate = new GdiImage(plate);
            }
            return f;
        }

        public void Dispose()
        {
            if (Plate != null) Plate.Dispose();
            if (Cards != null) foreach (GdiImage c in Cards) if (c != null) c.Dispose();
        }
    }

    LayoutTransition(Rectangle area, Rectangle screen, Rectangle[] from, Frame old, Color bg,
                     bool morph, bool[] fromPlain, float radius, Color cardColor)
    {
        _area = area;
        _from = from;
        _morph = morph;
        _fromPlain = fromPlain;
        _radius = radius;
        _cardColor = cardColor;
        _old = old;
        _bg = bg;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Bounds = screen;
        DoubleBuffered = false;   // 开播后画面全由渲染线程直接画，WinForms 不参与
    }

    protected override bool ShowWithoutActivation { get { return true; } }

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW;
            return cp;
        }
    }

    // 自己加的 WS_EX_LAYERED：不设一次透明度，分层窗口就是全透明、根本看不见。
    // WinForms 只在 Opacity < 1 时才替你设，Opacity = 1 时它什么都不做。
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Gdi.SetLayeredWindowAttributes(Handle, 0, 255, Gdi.LWA_ALPHA);
        // 关掉系统给新窗口的「打开」动画（从略小、半透明缩放淡入约 100ms）：
        // 浮层画的是原画面，可那 100ms 里它跟底下对不齐，看着就是点一下先闪一下。
        int on = 1;
        Gdi.DwmSetWindowAttribute(Handle, Gdi.DWMWA_TRANSITIONS_FORCEDISABLED, ref on, sizeof(int));
        // 让截屏跳过浮层（人眼照样看得见）：第二张就能用快的屏幕截图，截到的一定是底下的新排版。
        // PrintWindow 要把整个宿主重画一遍，实测 150~300ms，第二段会晚开播、中间停在空底板上。
        // 慢放调试时不设，好让截图工具看得见浮层；那时第二张退回 PrintWindow。
        _hiddenFromCapture = _slow == 1 && Gdi.SetWindowDisplayAffinity(Handle, Gdi.WDA_EXCLUDEFROMCAPTURE);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_MOUSEACTIVATE) { m.Result = (IntPtr)MA_NOACTIVATE; return; }
        base.WndProc(ref m);
    }

    protected override void OnPaintBackground(PaintEventArgs e) { }

    // 开播前（盖上到 Begin 之间）画第 0 帧：底板 + 原样大小的卡片，拼起来就是盖上前的屏幕。
    // 用 GDI 贴，GDI+ 画一张 2560x1500 的整图要一百多毫秒（实测「显示浮层」156ms）。
    // 开播后是渲染线程的事，这里不能再画，否则会盖掉它的帧。
    protected override void OnPaint(PaintEventArgs e)
    {
        if (_render != null) return;
        IntPtr hdc = e.Graphics.GetHdc();
        try
        {
            _old.Plate.CopyTo(hdc);
            for (int i = 0; i < _from.Length; i++) DrawCard(hdc, OldCard(i), _from[i], 1f, 1f);
        }
        finally { e.Graphics.ReleaseHdc(hdc); }
    }

    // area：要盖住的格子区域（host 客户区坐标）；panes：每格现在的位置（同一坐标系，藏着的格子给空矩形）。
    // 截不到（最小化、区域太小）返回 null，调用方照常直接排。
    public static LayoutTransition Cover(Form host, Rectangle area, Rectangle[] panes)
    {
        return Cover(host, area, panes, false, null, 0, Color.Empty);
    }

    // 队列里换人：每家窗口尺寸位置本来一样，换人只是换哪个显示。截一张整图盖上，
    // 底下瞬间换好，整窗透明度淡掉 —— 每帧只调一次 SetLayeredWindowAttributes，DWM 合成，
    // 不用第二次截图、不用拉伸（以前用变形，新截图 340ms 才到，交叉淡化挤在最后几十毫秒，看着卡）。
    public static LayoutTransition CoverFade(Form host, Rectangle area)
    {
        LayoutTransition f = CoverFadeFast(host, area)
                          ?? Cover(host, area, new Rectangle[0], false, null, 0, Color.Empty);
        if (f != null) f._fadeOnly = true;
        return f;
    }

    // 淡出只要一张整图当底板，不切卡片、不取缝隙色：屏幕直接 BitBlt 进底板一步到位。
    static LayoutTransition CoverFadeFast(Form host, Rectangle area)
    {
        // 宿主没显示在屏幕上（最小化；测试里只建窗体不显示）：没有画面可演，也别去截别人的屏
        if (!host.Visible || host.WindowState == FormWindowState.Minimized || area.Width < 40 || area.Height < 40) return null;
        if (SlowFactor() != 1) return null;   // 慢放调试走老路，好 dump 截图
        Rectangle screen = host.RectangleToScreen(area);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        GdiImage plate = GdiImage.FromScreen(screen);
        if (plate == null) return null;
        long tShot = sw.ElapsedMilliseconds;
        var old = new Frame { Plate = plate, Cards = new GdiImage[0] };
        try
        {
            var f = new LayoutTransition(area, screen, new Rectangle[0], old, Color.Empty, false, null, 0, Color.Empty);
            f._hostHwnd = host.Handle;
            f.Show(host);
            f.Update();
            long tShow = sw.ElapsedMilliseconds;
            Gdi.DwmFlush();
            Gdi.DwmFlush();
            Log.W(string.Format("layout-anim cover(fast): shot {0}ms, show {1}ms, flush {2}ms", tShot, tShow - tShot, sw.ElapsedMilliseconds - tShow));
            return f;
        }
        catch (Exception ex)
        {
            Log.W("layout-anim cover(fast) failed: " + ex.Message);
            old.Dispose();
            return null;
        }
    }

    // morph：队列视图的「变形」演法；plain：哪几格被挡住了、用空白卡片代替；radius/cardColor：空白卡片的样子。
    public static LayoutTransition Cover(Form host, Rectangle area, Rectangle[] panes,
                                         bool morph, bool[] plain, float radius, Color cardColor)
    {
        if (!host.Visible || host.WindowState == FormWindowState.Minimized || area.Width < 40 || area.Height < 40)
            return null;
        Rectangle screen = host.RectangleToScreen(area);
        // 第一张用屏幕截图（二三十毫秒）：这时浮层还没出来，截到的就是用户眼前的样子。
        // 实测和 PrintWindow 截的逐像素一致，而 PrintWindow 要 150ms。
        // 第二张（GrabNew）时浮层盖着：Win11 的屏幕截图会把分层窗口也截进去，所以浮层要设
        // WDA_EXCLUDEFROMCAPTURE；设不上（老系统、慢放调试）就退回 PrintWindow。
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Bitmap shot = Grab(screen) ?? PrintHost(host.Handle, area);
        long tShot = sw.ElapsedMilliseconds;
        if (shot == null) return null;
        DumpForDebug(shot, "anim-old.png");
        Frame old = null;
        try
        {
            var from = ToLocal(panes, area);
            Color bg = GapColor(shot, from);
            old = Frame.From(shot, from, bg, plain);
            long tFrame = sw.ElapsedMilliseconds;
            shot.Dispose();   // 切完就不要整图了
            shot = null;
            var f = new LayoutTransition(area, screen, from, old, bg, morph, plain, radius, cardColor);
            f._hostHwnd = host.Handle;
            f.Show(host);
            f.Update();
            long tShow = sw.ElapsedMilliseconds;
            // 窗口画好了不等于上了屏：要等 DWM 下一次合成。不等就回去换排版，
            // 会先露出一眼新排版、浮层才盖上来（用户看到的「先闪一下」）。
            Gdi.DwmFlush();
            Gdi.DwmFlush();
            Log.W(string.Format("layout-anim cover: shot {0}ms, cut {1}ms, show {2}ms, flush {3}ms", tShot, tFrame - tShot, tShow - tFrame, sw.ElapsedMilliseconds - tShow));
            return f;
        }
        catch (Exception ex)
        {
            Log.W("layout-anim cover failed: " + ex.Message);
            if (old != null) old.Dispose();
            if (shot != null) shot.Dispose();
            return null;
        }
    }

    // 新排版已经排好（host 客户区坐标），开始播。
    public void Begin(Rectangle[] panes)
    {
        Begin(panes, null, null);
    }

    // plain：新排版里哪几格被挡住（用空白卡片代替）；order：Morph 时的叠放顺序，先画的在下面。
    public void Begin(Rectangle[] panes, bool[] plain, int[] order)
    {
        _toPlain = plain;
        _order = order;
        _to = ToLocal(panes, _area);
        _hwnd = Handle;
        _render = new Thread(RenderLoop) { IsBackground = true, Name = "layout-anim", Priority = ThreadPriority.AboveNormal };
        _render.Start();
    }

    volatile bool _hostPainted;

    // 最多等多久。以前 300ms：嵌进来的 Chromium 以为自己被挡住时根本不出帧，等满就揭开一片空白
    //（2026-09-26 日志里 Codex、Cursor 反复等满 300ms）。现在不在前面的格子垫在底下、一直在画，
    // 正常几十毫秒就画好；真没画出来就在浮层底下叫醒它（SlowPaint），多给点时间，也别揭开空白。
    const int WaitPaintMaxMs = 700;
    const int SlowPaintMs = 100;
    Rectangle _waitRect;
    bool _slowFired;

    // 淡出前要等它画好的那块（屏幕坐标）：队列里新换上的那家的客户端区域。
    public void WaitForPaint(Rectangle screen) { _waitRect = screen; }

    // 等了 SlowPaintMs 还没画出来时调一次（渲染线程上调，调用方自己切回界面线程）
    public Action SlowPaint;

    // 淡出多长。换主题是整窗大变样，稍长一点的交叉淡化看着顺
    public int FadeDurationMs = FadeMs;

    // 队列里换人用：等新的那家画好之后不淡出，把旧截图里这一块（中间那张卡片，宿主客户区坐标）翻过去。
    // 只对 CoverFade 盖上的浮层有用；截不到底下的新画面就照旧淡出。
    Rectangle _turn;
    public void TurnPage(Rectangle hostRect)
    {
        _turn = new Rectangle(hostRect.X - _area.X, hostRect.Y - _area.Y, hostRect.Width, hostRect.Height);
    }

    // 换主题用：等这块屏幕「变过、再稳住」才淡出。宿主自己的颜色当场就画好了，
    // 嵌进来的几家要等它们各自读到新的系统配色、重画完（几百毫秒不等），早揭开就看见它们一下子翻过去。
    Rectangle _settleRect;
    int _settleMaxMs;
    public void WaitForSettle(Rectangle screen, int maxMs) { _settleRect = screen; _settleMaxMs = maxMs; }

    // 这块屏幕上有没有「画出来的东西」：去掉顶上标题 / 菜单栏那一条，平均缩成 64x40，
    // 偏离主色的点不到 2% 就算还没画。没画的 Electron 是一整块纯白 / 纯黑，只剩菜单栏 ——
    // 以前看极差，菜单栏的字就把它骗过去了（逐帧拍到 Cursor 白了 120ms，判定却说 30ms 就画好）。
    internal static bool Painted(Rectangle screen)
    {
        const int W = 64, H = 40;
        int cut = Math.Min(screen.Height / 6, 90);
        screen = new Rectangle(screen.X, screen.Y + cut, screen.Width, screen.Height - cut);
        if (screen.Width < 40 || screen.Height < 40) return true;
        try
        {
            using (var bmp = new Bitmap(W, H, PixelFormat.Format32bppRgb))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    IntPtr dc = g.GetHdc();
                    IntPtr sdc = Gdi.GetDC(IntPtr.Zero);
                    try
                    {
                        Gdi.SetStretchBltMode(dc, 4);   // HALFTONE：平均缩小，文字糊成灰块而不是被跳过
                        Gdi.StretchBlt(dc, 0, 0, W, H, sdc, screen.X, screen.Y, screen.Width, screen.Height, Gdi.SRCCOPY);
                    }
                    finally { Gdi.ReleaseDC(IntPtr.Zero, sdc); g.ReleaseHdc(dc); }
                }
                BitmapData d = bmp.LockBits(new Rectangle(0, 0, W, H), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
                try
                {
                    var lum = new int[W * H];
                    var hist = new int[64];
                    for (int y = 0; y < H; y++)
                        for (int x = 0; x < W; x++)
                        {
                            int p = System.Runtime.InteropServices.Marshal.ReadInt32(d.Scan0, y * d.Stride + x * 4);
                            int l = (((p >> 16) & 255) * 3 + ((p >> 8) & 255) * 6 + (p & 255)) / 10;
                            lum[y * W + x] = l;
                            hist[l >> 2]++;
                        }
                    int mode = 0;
                    for (int i = 1; i < 64; i++) if (hist[i] > hist[mode]) mode = i;
                    int center = mode * 4 + 2, off = 0;
                    foreach (int l in lum) if (Math.Abs(l - center) > 8) off++;
                    return off * 50 > lum.Length;   // > 2%
                }
                finally { bmp.UnlockBits(d); }
            }
        }
        catch { return true; }
    }

    // 宿主已按新排版重画完自己（卡片边框、标题），可以截第二张了。
    public void HostPainted() { _hostPainted = true; }

    // ---- 等画面稳定（换主题）----
    byte[] _settleFirst, _settlePrev;
    int _settleSampleAt = -1000, _settleChangeAt;
    bool _settleChanged;

    // 每 40ms 截一张 48x30 的缩略图（浮层不进截图，截到的是底下真实的画面）：
    // 跟第一张比差得多了 = 那几家开始换色了；跟上一张比几乎没变、持续 200ms = 换完了。
    // 一直没变（这几家不跟系统换色、或者没开连动）就等够 450ms 算数。
    bool Settled(int t)
    {
        if (t - _settleSampleAt >= 40)
        {
            _settleSampleAt = t;
            byte[] cur = Thumb(_settleRect);
            if (cur != null)
            {
                if (_settleFirst == null) { _settleFirst = cur; _settleChangeAt = t; }
                else
                {
                    if (MeanDiff(cur, _settleFirst) > 5) _settleChanged = true;
                    if (_settlePrev != null && MeanDiff(cur, _settlePrev) > 1.5) _settleChangeAt = t;
                }
                _settlePrev = cur;
            }
        }
        return _settleChanged ? t - _settleChangeAt >= 200 : t >= FadeHoldMs + 450;
    }

    static byte[] Thumb(Rectangle screen)
    {
        const int W = 48, H = 30;
        if (screen.Width < 40 || screen.Height < 40) return null;
        try
        {
            using (var bmp = new Bitmap(W, H, PixelFormat.Format32bppRgb))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    IntPtr dc = g.GetHdc();
                    IntPtr sdc = Gdi.GetDC(IntPtr.Zero);
                    try
                    {
                        Gdi.SetStretchBltMode(dc, 4);   // HALFTONE
                        Gdi.StretchBlt(dc, 0, 0, W, H, sdc, screen.X, screen.Y, screen.Width, screen.Height, Gdi.SRCCOPY);
                    }
                    finally { Gdi.ReleaseDC(IntPtr.Zero, sdc); g.ReleaseHdc(dc); }
                }
                BitmapData d = bmp.LockBits(new Rectangle(0, 0, W, H), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
                try
                {
                    var lum = new byte[W * H];
                    for (int y = 0; y < H; y++)
                        for (int x = 0; x < W; x++)
                        {
                            int p = System.Runtime.InteropServices.Marshal.ReadInt32(d.Scan0, y * d.Stride + x * 4);
                            lum[y * W + x] = (byte)((((p >> 16) & 255) * 3 + ((p >> 8) & 255) * 6 + (p & 255)) / 10);
                        }
                    return lum;
                }
                finally { bmp.UnlockBits(d); }
            }
        }
        catch { return null; }
    }

    static double MeanDiff(byte[] a, byte[] b)
    {
        long sum = 0;
        for (int i = 0; i < a.Length && i < b.Length; i++) sum += Math.Abs(a[i] - b[i]);
        return sum / (double)Math.Max(1, a.Length);
    }

    int OutTotal { get { return OutMs + StaggerMs * Math.Max(0, _from.Length - 1); } }
    int InTotal { get { return InMs + StaggerMs * Math.Max(0, _to.Length - 1); } }

    void RenderLoop()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        int slow = _slow;
        Size size = new Size(_area.Width, _area.Height);
        IntPtr wdc = Gdi.GetDC(_hwnd);
        IntPtr buf = Gdi.CreateCompatibleDC(wdc);
        IntPtr bmp = Gdi.CreateCompatibleBitmap(wdc, size.Width, size.Height);
        IntPtr oldBmp = Gdi.SelectObject(buf, bmp);
        // 内存 DC 默认的拉伸模式 BLACKONWHITE 缩小彩色图会把像素「与」在一起、整张发暗
        Gdi.SetStretchBltMode(buf, Gdi.COLORONCOLOR);
        Gdi.SetStretchBltMode(wdc, Gdi.COLORONCOLOR);
        // 变形动画画面超过 150 万像素就按半分辨率画（见 _rs）
        if (_morph && !_fadeOnly && (long)size.Width * size.Height > 1500000) _rs = 0.5f;
        // 开播前把要用到的空白卡片先做好（旧的挡住格、从小长大的那张），别在第一帧里现做
        if (_morph && !_fadeOnly)
        {
            int n0 = Math.Min(_from.Length, _to.Length);
            for (int i = 0; i < n0; i++)
            {
                OldCard(i);
                Rectangle a0 = _from[i], b0 = _to[i];
                if (a0.Width > 0 && b0.Width > 0 && (long)a0.Width * a0.Height * 4 < (long)b0.Width * b0.Height) Plain(b0.Size);
                if (_toPlain != null && i < _toPlain.Length && _toPlain[i]) Plain(b0.Size);
            }
            clock.Restart();   // 预热的时间不算进动画，不然开头直接跳过一截
        }
        Task<Frame> grab = null;
        GdiImage next = null;   // Turn：底下的新画面
        int inStart = -1, frames = 0, last = -1, maxGap = 0;
        double paintMs = 0;
        var gaps = new System.Text.StringBuilder();
        string end = "done";
        try
        {
            int ready = -1;
            int fadeAt = -1;
            while (!_stop && _fadeOnly)
            {
                int t = (int)(clock.ElapsedMilliseconds / slow);
                // 等新换上的那家真画出来了再淡：隐藏过的 Electron 重新显示时要现画，
                // 早淡就会露出一帧白底 / 黑底（用户 2026-09-26：切换时有几率闪屏）。
                // 浮层不进截图，采样到的就是底下的新画面；最多等 WaitPaintMaxMs。
                if (fadeAt < 0)
                {
                    bool due = t >= FadeHoldMs && _hostPainted;
                    bool go = false;
                    string why = "";
                    if (due)
                    {
                        if (!_settleRect.IsEmpty && _hiddenFromCapture)
                        {
                            go = Settled(t);
                            if (!go && t >= FadeHoldMs + _settleMaxMs) { go = true; why = " (settle timeout)"; }
                            else if (go) why = _settleChanged ? " (settled)" : " (no change)";
                        }
                        else if (_waitRect.IsEmpty || !_hiddenFromCapture) go = true;
                        else
                        {
                            go = Painted(_waitRect);
                            if (!go && !_slowFired && t >= FadeHoldMs + SlowPaintMs && SlowPaint != null)
                            {
                                _slowFired = true;
                                why = "";
                                try { SlowPaint(); } catch { }
                            }
                            if (!go && t >= FadeHoldMs + WaitPaintMaxMs) { go = true; why = " (paint timeout)"; }
                        }
                    }
                    if (go)
                    {
                        fadeAt = t;
                        Log.W("layout-anim fade waited " + t + "ms for paint" + why + (_slowFired ? " [woke it]" : ""));
                        // 要翻页：把底下画好的新画面截下来，交给下面那一段去翻；截不到就接着淡出
                        if (!_turn.IsEmpty && _turn.Width > 0)
                        {
                            long g0 = System.Diagnostics.Stopwatch.GetTimestamp();
                            next = GrabNext();
                            Log.W("layout-anim turn: grabbed new frame in "
                                + (System.Diagnostics.Stopwatch.GetTimestamp() - g0) * 1000 / System.Diagnostics.Stopwatch.Frequency + "ms"
                                + (next == null ? " (failed, fading instead)" : ""));
                            if (next != null) break;
                        }
                    }
                    else
                    {
                        frames++;
                        last = t;
                        if (Gdi.DwmFlush() != 0) Thread.Sleep(8);
                        continue;
                    }
                }
                float u = Clamp((t - fadeAt) / (float)FadeDurationMs);
                float e = 1f - (1f - u) * (1f - u) * (1f - u);   // 快出慢收：点下去马上看得出在变（慢进慢出头 1/4 只变 16%，像没点上）
                Gdi.SetLayeredWindowAttributes(_hwnd, 0, (byte)Math.Round((1f - e) * 255f), Gdi.LWA_ALPHA);
                if (last >= 0) { maxGap = Math.Max(maxGap, t - last); if (t - last > 25) gaps.Append(' ').Append(last).Append('+').Append(t - last); }
                last = t;
                frames++;
                if (u >= 1f) break;
                if (Gdi.DwmFlush() != 0) Thread.Sleep(8);
            }
            if (next != null && !_stop)
            {
                end = "turned";
                // 这一段每帧都整张重画（浮层一直不透明），画完的最后一帧就是底下的新画面，揭开不跳
                using (GdiImage rise = GdiImage.Ramp(true))
                using (GdiImage fall = GdiImage.Ramp(false))
                {
                    int t0 = (int)(clock.ElapsedMilliseconds / slow);
                    last = -1;
                    while (!_stop)
                    {
                        int t = (int)(clock.ElapsedMilliseconds / slow) - t0;
                        float u = Clamp(t / (float)TurnMs);
                        long p0 = System.Diagnostics.Stopwatch.GetTimestamp();
                        DrawTurn(buf, _old.Plate, next, rise, fall, _turn, u, Clamp(t / (float)TurnBaseFadeMs));
                        Gdi.BitBlt(wdc, 0, 0, size.Width, size.Height, buf, 0, 0, Gdi.SRCCOPY);
                        paintMs += (System.Diagnostics.Stopwatch.GetTimestamp() - p0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                        if (last >= 0) { maxGap = Math.Max(maxGap, t - last); if (t - last > 25) gaps.Append(' ').Append(last).Append('+').Append(t - last); }
                        last = t;
                        frames++;
                        if (u >= 1f) break;
                        if (Gdi.DwmFlush() != 0) Thread.Sleep(8);
                    }
                }
            }
            while (!_stop && _morph && !_fadeOnly)
            {
                int t = (int)(clock.ElapsedMilliseconds / slow);
                if (grab == null && _hostPainted && t >= MorphGrabAtMs) grab = GrabNew();
                if (ready < 0 && grab != null && grab.IsCompleted)
                {
                    _new = grab.Status == TaskStatus.RanToCompletion ? grab.Result : null;
                    ready = t;
                    inStart = t;
                }
                float move = Clamp(t / (float)MorphMs);
                float fade = (ready < 0 || _new == null) ? 0f : Clamp((t - ready) / (float)MorphFadeMs);
                // 截不到新排版（ready 了但 _new 为空）：演完移动就揭开，底下本来就是新排版
                if (move >= 1f && ready >= 0 && (fade >= 1f || _new == null)) break;
                if (t > 2000) { end = "timeout"; break; }

                long p0 = System.Diagnostics.Stopwatch.GetTimestamp();
                DrawMorph(buf, move, fade);
                if (_rs == 1f) Gdi.BitBlt(wdc, 0, 0, size.Width, size.Height, buf, 0, 0, Gdi.SRCCOPY);
                else Gdi.StretchBlt(wdc, 0, 0, size.Width, size.Height, buf, 0, 0,
                                    (int)Math.Ceiling(size.Width * _rs), (int)Math.Ceiling(size.Height * _rs), Gdi.SRCCOPY);
                paintMs += (System.Diagnostics.Stopwatch.GetTimestamp() - p0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                if (last >= 0)
                {
                    maxGap = Math.Max(maxGap, t - last);
                    if (t - last > 25) gaps.Append(' ').Append(last).Append('+').Append(t - last);
                }
                last = t;
                frames++;
                if (Gdi.DwmFlush() != 0) Thread.Sleep(8);
            }
            while (!_stop && !_morph && !_fadeOnly)
            {
                int t = (int)(clock.ElapsedMilliseconds / slow);
                // 还得等宿主把自己的卡片边框按新排版画完（HostPainted），否则截到的是新位置的客户端配旧边框。
                if (grab == null && _hostPainted && t >= GrabAtMs) grab = GrabNew();
                if (inStart < 0 && t >= OutTotal && grab.IsCompleted)
                {
                    // 截不到就不演第二段，直接揭开。
                    _new = grab.Status == TaskStatus.RanToCompletion ? grab.Result : null;
                    if (_new == null) { end = "no second shot"; break; }
                    inStart = t;
                }
                if (inStart >= 0 && t - inStart >= InTotal) break;

                long p0 = System.Diagnostics.Stopwatch.GetTimestamp();
                if (inStart < 0) DrawOut(buf, t);
                else DrawIn(buf, t - inStart);
                Gdi.BitBlt(wdc, 0, 0, size.Width, size.Height, buf, 0, 0, Gdi.SRCCOPY);
                paintMs += (System.Diagnostics.Stopwatch.GetTimestamp() - p0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                if (last >= 0)
                {
                    maxGap = Math.Max(maxGap, t - last);
                    if (t - last > 25) gaps.Append(' ').Append(last).Append('+').Append(t - last);
                }
                last = t;
                frames++;
                // 等屏幕下一次刷新再画下一帧；DWM 没开（远程桌面之类）就退回睡一小会儿。
                if (Gdi.DwmFlush() != 0) Thread.Sleep(8);
            }
        }
        catch (Exception ex) { end = "failed: " + ex.Message; }
        finally
        {
            Gdi.SelectObject(buf, oldBmp);
            Gdi.DeleteObject(bmp);
            Gdi.DeleteDC(buf);
            Gdi.ReleaseDC(_hwnd, wdc);
            if (next != null) next.Dispose();
            // 截好了却没轮到用（被连点收掉）：自己释放
            if (grab != null && _new == null)
                grab.ContinueWith(tk => { if (tk.Status == TaskStatus.RanToCompletion && tk.Result != null) tk.Result.Dispose(); });
        }
        if (_stop) return;   // 界面线程在收它，不用再请它关
        Log.W(string.Format("layout-anim {0}: {1} frames in {2}ms, paint avg {3:0.0}ms, max gap {4}ms, gaps(at+ms):{5} phase2 at {6}",
            end, frames, clock.ElapsedMilliseconds, frames == 0 ? 0 : paintMs / frames, maxGap, gaps, inStart));
        try { BeginInvoke((MethodInvoker)Close); } catch { }
    }

    // 调试用：%LOCALAPPDATA%\AgentHost\anim-slow.txt 里写个倍数（如 10），动画按倍数慢放，
    // 好用截图一帧帧看。没有这个文件就是正常速度。
    static int SlowFactor()
    {
        try
        {
            string p = System.IO.Path.Combine(Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? "", "AgentHost", "anim-slow.txt");
            int k;
            if (System.IO.File.Exists(p) && int.TryParse(System.IO.File.ReadAllText(p).Trim(), out k) && k > 1 && k <= 50) return k;
        }
        catch { }
        return 1;
    }

    // 调试用：有 %LOCALAPPDATA%\AgentHost\anim-dump.txt 时，把第二张截图存成 anim-new.png，
    // 看截到的是不是底下的新排版（浮层对截屏隐身后，外面的截图工具看不到它，只能这样验）。
    static void DumpForDebug(Bitmap shot, string name = "anim-new.png")
    {
        try
        {
            string dir = System.IO.Path.Combine(Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? "", "AgentHost");
            if (System.IO.File.Exists(System.IO.Path.Combine(dir, "anim-dump.txt")))
                shot.Save(System.IO.Path.Combine(dir, name), ImageFormat.Png);
        }
        catch { }
    }

    // ---- Morph（队列视图）----

    GdiImage OldCard(int i)
    {
        if (i < _old.Cards.Length && _old.Cards[i] != null) return _old.Cards[i];
        return _fromPlain != null && i < _fromPlain.Length && _fromPlain[i] ? Plain(_from[i].Size) : null;
    }

    GdiImage NewCard(int i)
    {
        if (_new != null && i < _new.Cards.Length && _new.Cards[i] != null) return _new.Cards[i];
        return _toPlain != null && i < _toPlain.Length && _toPlain[i] ? Plain(_to[i].Size) : null;
    }

    // 空白卡片（挡在后面、截不到的那几格）：按尺寸画一次缓存起来，之后都是拉伸贴图。
    readonly System.Collections.Generic.Dictionary<Size, GdiImage> _plain = new System.Collections.Generic.Dictionary<Size, GdiImage>();

    GdiImage Plain(Size sz)
    {
        if (sz.Width <= 0 || sz.Height <= 0) return null;
        GdiImage img;
        if (_plain.TryGetValue(sz, out img)) return img;
        // 半分辨率画的时候空白卡片也只做一半大（反正是拉伸贴上去）：全尺寸的大卡片用 GDI+ 抗锯齿画圆角
        // 要 ~100ms，以前是在第一帧里现做，每次变形开头都卡一下（日志 gaps 0+92 / 0+117）
        int w = Math.Max(1, (int)Math.Ceiling(sz.Width * _rs)), h = Math.Max(1, (int)Math.Ceiling(sz.Height * _rs));
        using (var b = new Bitmap(w, h, PixelFormat.Format32bppRgb))
        {
            using (Graphics g = Graphics.FromImage(b))
            {
                g.Clear(_bg);
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using (var path = Theme.Round(new RectangleF(0.5f, 0.5f, w - 1, h - 1), _radius * _rs))
                using (var br = new SolidBrush(_cardColor))
                using (var pen = new Pen(Theme.Line))
                {
                    g.FillPath(br, path);
                    g.DrawPath(pen, path);
                }
            }
            img = new GdiImage(b);
        }
        _plain[sz] = img;
        return img;
    }

    static Rectangle Lerp(Rectangle a, Rectangle b, float t)
    {
        return new Rectangle(
            a.X + (int)Math.Round((b.X - a.X) * t),
            a.Y + (int)Math.Round((b.Y - a.Y) * t),
            a.Width + (int)Math.Round((b.Width - a.Width) * t),
            a.Height + (int)Math.Round((b.Height - a.Height) * t));
    }

    // 变形动画按多大分辨率画（1 = 原尺寸）。GDI 的拉伸 / 半透明叠加全是 CPU 做的，开销跟目标像素数成正比：
    // 2560×1460 原尺寸画网格→队列一帧 35ms（只有 ~26 帧/秒，用户：切换动画帧率太低）。
    // 画面在动、只演 0.4 秒，半分辨率看不出来；开播前那一帧（OnPaint）和演完揭开都是原画质。
    float _rs = 1f;

    Rectangle Sc(Rectangle r)
    {
        if (_rs == 1f) return r;
        return new Rectangle((int)(r.X * _rs), (int)(r.Y * _rs), (int)Math.Ceiling(r.Width * _rs), (int)Math.Ceiling(r.Height * _rs));
    }

    void DrawPlate(IntPtr buf, GdiImage plate)
    {
        if (_rs == 1f) { plate.CopyTo(buf); return; }
        Gdi.StretchBlt(buf, 0, 0, (int)Math.Ceiling(plate.W * _rs), (int)Math.Ceiling(plate.H * _rs), plate.Dc, 0, 0, plate.W, plate.H, Gdi.SRCCOPY);
    }

    void DrawMorph(IntPtr buf, float move, float fade)
    {
        // 底板：新旧只差在缝里那几条（格子位置都涂成了同一个底色），淡到一半直接换，
        // 省一次整屏 AlphaBlend（每帧多 5~10ms）。
        DrawPlate(buf, _new != null && fade >= 0.5f ? _new.Plate : _old.Plate);
        float e = move < 0.5f ? 4f * move * move * move : 1f - (float)Math.Pow(-2f * move + 2f, 3) / 2f;   // 慢进慢出
        int n = Math.Min(_from.Length, _to.Length);
        for (int k = 0; k < n; k++)
        {
            int i = _order != null && k < _order.Length ? _order[k] : k;
            Rectangle a = _from[i], b = _to[i];
            GdiImage oc = OldCard(i), nc = NewCard(i);
            if (a.Width <= 0 && b.Width <= 0) continue;
            if (a.Width <= 0)          // 新出现的：在新位置淡入
            {
                DrawCard(buf, nc, Sc(b), 0.96f + 0.04f * e, e);
                continue;
            }
            if (b.Width <= 0)          // 消失的：在原地淡出
            {
                DrawCard(buf, oc, Sc(a), 1f - 0.04f * e, 1f - e);
                continue;
            }
            Rectangle r = Lerp(a, b, e);
            // 从侧栏小卡片长成中间大卡片的：把小卡片的截图硬放大几十倍只会糊成一片，
            // 先用空白卡片顶着，新截图到了再淡进内容。
            if ((long)a.Width * a.Height * 4 < (long)b.Width * b.Height) oc = Plain(b.Size);
            // 反过来，从中间缩回侧栏的：新样子是一张小卡片，太早淡进来会被放大成一个巨大的名字。
            // 等它缩得差不多了（移动过半以后）再换成小卡片的样子。
            float f = fade;
            if ((long)b.Width * b.Height * 4 < (long)a.Width * a.Height) f = Math.Min(fade, Clamp((e - 0.55f) / 0.35f));
            Rectangle rr = Sc(r);
            DrawCard(buf, oc, rr, 1f, 1f);
            if (f > 0f) DrawCard(buf, nc, rr, 1f, f);
        }
    }

    void DrawOut(IntPtr buf, int t)
    {
        _old.Plate.CopyTo(buf);
        for (int i = 0; i < _from.Length; i++)
        {
            float u = Clamp((t - i * StaggerMs) / (float)OutMs);
            float e = u * u;                                   // 先慢后快地收走
            DrawCard(buf, _old.Cards[i], _from[i], 1f - (1f - MinScale) * e, 1f - e);
        }
    }

    void DrawIn(IntPtr buf, int t)
    {
        _new.Plate.CopyTo(buf);
        for (int i = 0; i < _to.Length; i++)
        {
            float u = Clamp((t - i * StaggerMs) / (float)InMs);
            float e = 1f - (1f - u) * (1f - u) * (1f - u);     // 先快后稳地落定
            DrawCard(buf, _new.Cards[i], _to[i], MinScale + (1f - MinScale) * e, e);
        }
    }

    static void DrawCard(IntPtr buf, GdiImage c, Rectangle r, float scale, float alpha)
    {
        if (c == null || r.Width <= 0 || alpha <= 0.01f) return;
        int w = (int)Math.Round(r.Width * scale), h = (int)Math.Round(r.Height * scale);
        if (w <= 0 || h <= 0) return;
        int x = r.X + (r.Width - w) / 2, y = r.Y + (r.Height - h) / 2;
        if (alpha >= 0.999f)
        {
            // 不透明：原样大小直接贴，要拉伸就用 StretchBlt（比 AlphaBlend 快得多）。
            // 以前只看 scale==1 就走 BitBlt，变形时目标框在变、scale 却一直是 1，图就没被拉伸。
            if (w == c.W && h == c.H) Gdi.BitBlt(buf, x, y, w, h, c.Dc, 0, 0, Gdi.SRCCOPY);
            else Gdi.StretchBlt(buf, x, y, w, h, c.Dc, 0, 0, c.W, c.H, Gdi.SRCCOPY);
            return;
        }
        int a = (int)Math.Round(alpha * 255f);
        Gdi.AlphaBlend(buf, x, y, w, h, c.Dc, 0, 0, c.W, c.H, a << 16);
    }

    // ---- Turn（队列里换人：翻页）----

    // 底下已经换好的新画面，整块截下来当底。浮层不进截图时直接截屏幕（十来毫秒）；
    // 进截图的（老系统、慢放调试）让系统把宿主窗口渲染一份，慢一些。
    GdiImage GrabNext()
    {
        try
        {
            if (_hiddenFromCapture) return GdiImage.FromScreen(Bounds);
            using (Bitmap shot = PrintHost(_hostHwnd, _area)) return shot == null ? null : new GdiImage(shot);
        }
        catch { return null; }
    }

    // 翻到 u（0→1）时的一帧。底是新画面；旧画面里的那一页（page）绕着自己左边那条边朝外翻：
    //   - 竖着切成一条条贴：越靠右的条压得越窄，也拉得越高一点（朝你翻过来的那边近大远小）；
    //   - 翻起来的那一边压一层暗（越靠边越暗），翻开的地方落一道影子，不然像一张图在被横着压扁；
    //   - 页以外的地方（侧栏、页边、索引签）头 baseFade 这一段从旧画面淡到新画面，不然开翻的那一帧会跳一下。
    // 页还剩多宽 = cos θ。θ 不匀速：开头慢一点（看得出是掀起来的），越往后越快，最后一下翻过去。
    //
    // 开销（2026-10-12 本机，2560×1460，机器闲的时候）：整屏拷一次 1.3～1.9ms；一页切成条拉伸 4～6ms
    //（跟像素数走，另外每条有 0.025ms 的固定开销，所以别切太细）；半透明叠加（AlphaBlend）最贵 —— 整页压一次 11ms，
    // 每调一次还有 0.3ms 上下的固定开销，拿纯色一条条各叠一次（五十来次）一帧要 15～40ms。所以：
    //   - 底只拷这一帧露在页外面的部分（页盖着的地方反正要被条带重画）；
    //   - 压暗只压靠边的一小段，用一张自带渐变的小图一次叠完；影子同样一次。一帧最多叠 4 次。
    static void DrawTurn(IntPtr buf, GdiImage old, GdiImage next, GdiImage rise, GdiImage fall, Rectangle page, float u, float baseFade)
    {
        int W = page.Width, H = page.Height;
        if (W <= 0 || H <= 0 || u >= 1f) { next.CopyTo(buf); return; }
        double cos = 1.0 - Math.Pow(u, 1.7);
        double sin = Math.Sqrt(Math.Max(0.0, 1.0 - cos * cos));
        double eye = W * 7.0;                       // 眼睛离纸面多远：越大透视越弱（7 倍页宽时右边最多高出 16%）
        // 这一帧页边到哪了（跟下面最后一条算出来的一样）；往左让两个像素，条带画上去正好盖住
        int edge = Math.Max(page.X, page.X + (int)Math.Round(W * cos * eye / (eye - W * sin)) - 2);
        // 底：页的左边、上下两条缝、页边右边，从新画面拷过来（上一帧画过界的条带、影子也就一并擦掉了）
        Copy(buf, next, 0, 0, page.X, next.H);
        Copy(buf, next, page.X, 0, W, page.Y);
        Copy(buf, next, page.X, page.Bottom, W, next.H - page.Bottom);
        Copy(buf, next, edge, 0, next.W - edge, next.H);
        if (baseFade < 1f)
        {
            // 页的左右两边（侧栏；页边和索引签）从旧画面叠回去一层。上下那两条缝只有十来个像素，不管
            int a = (int)Math.Round((1f - baseFade) * 255f) << 16;
            Blend(buf, old, new Rectangle(0, 0, page.X, old.H), a);
            Blend(buf, old, new Rectangle(page.Right, 0, old.W - page.Right, old.H), a);
        }
        int strips = Math.Max(16, Math.Min(64, W / 40));
        int shadeFrom = (int)(W * 0.84);            // 从页宽的这里往右开始压暗
        int xPrev = page.X, sPrev = 0, xShade = -1, yEdge = page.Y, hEdge = H;
        for (int i = 1; i <= strips; i++)
        {
            int s = (int)((long)W * i / strips);
            int x = page.X + (int)Math.Round(s * cos * eye / (eye - s * sin));
            if (x <= xPrev) continue;               // 压得不到一个像素：并到下一条里
            hEdge = (int)Math.Round(H * eye / (eye - (sPrev + s) / 2.0 * sin));
            yEdge = page.Y - (hEdge - H) / 2;
            Gdi.StretchBlt(buf, xPrev, yEdge, x - xPrev, hEdge, old.Dc, page.X + sPrev, page.Y, s - sPrev, H, Gdi.SRCCOPY);
            if (xShade < 0 && s > shadeFrom) xShade = xPrev;
            xPrev = x;
            sPrev = s;
        }
        // 翻起来的那一边：从 shadeFrom 起由浅到深压暗，到页边最深。按页边那一条的高度叠 ——
        // 靠左那头会多盖出页外一点点，可那里渐变正好接近全透明，看不出来
        int shade = (int)Math.Round(150 * sin);
        if (shade > 5 && xShade >= 0 && xPrev > xShade)
            Gdi.AlphaBlend(buf, xShade, yEdge, xPrev - xShade, hEdge, rise.Dc, 0, 0, rise.W, 1, (shade << 16) | Gdi.AC_SRC_ALPHA);
        // 翻开的地方落一道影子：紧挨着页边最深，往右淡掉
        int reach = (int)Math.Round(Math.Min(W - (xPrev - page.X), H * 0.05 + W * 0.05 * sin));
        int cast = (int)Math.Round(90 * sin);
        if (cast > 3 && reach > 4)
            Gdi.AlphaBlend(buf, xPrev, page.Y, reach, H, fall.Dc, 0, 0, fall.W, 1, (cast << 16) | Gdi.AC_SRC_ALPHA);
    }

    static void Copy(IntPtr buf, GdiImage src, int x, int y, int w, int h)
    {
        if (w > 0 && h > 0) Gdi.BitBlt(buf, x, y, w, h, src.Dc, x, y, Gdi.SRCCOPY);
    }
    static void Blend(IntPtr buf, GdiImage src, Rectangle r, int blend)
    {
        r.Intersect(new Rectangle(0, 0, src.W, src.H));
        if (r.Width > 0 && r.Height > 0)
            Gdi.AlphaBlend(buf, r.X, r.Y, r.Width, r.Height, src.Dc, r.X, r.Y, r.Width, r.Height, blend);
    }

    // 测试用：不开窗、不截屏，拿两张图离屏画出翻到 u 时的那一帧（tools\PageBookTests.cs 拿它出图、掐每帧多久）。
    // 返回的位图由调用方释放。
    internal static Bitmap TurnFrameForTest(Bitmap oldShot, Bitmap newShot, Rectangle page, float u, float baseFade, int repeat, out double msPerFrame, out double msFullCopy)
    {
        // 真跑的时候两张图都是截屏截进「跟屏幕兼容的位图」里的；测试也照这样放，掐出来的时间才对得上
        //（直接拿 GDI+ 位图转出来的那种去贴，慢好几倍）
        using (GdiImage o = GdiImage.LikeScreen(oldShot))
        using (GdiImage n = GdiImage.LikeScreen(newShot))
        using (GdiImage rise = GdiImage.Ramp(true))
        using (GdiImage fall = GdiImage.Ramp(false))
        {
            IntPtr sdc = Gdi.GetDC(IntPtr.Zero);
            IntPtr buf = Gdi.CreateCompatibleDC(sdc);
            IntPtr bmp = Gdi.CreateCompatibleBitmap(sdc, o.W, o.H);
            IntPtr was = Gdi.SelectObject(buf, bmp);
            try
            {
                Gdi.SetStretchBltMode(buf, Gdi.COLORONCOLOR);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                for (int i = 0; i < Math.Max(1, repeat); i++) DrawTurn(buf, o, n, rise, fall, page, u, baseFade);
                msPerFrame = sw.Elapsed.TotalMilliseconds / Math.Max(1, repeat);
                // 同一时刻整屏拷一次要多久：机器忙的时候两个数一起涨，拿它当尺子
                using (GdiImage scratch = GdiImage.LikeScreen(newShot))
                {
                    sw.Restart();
                    for (int i = 0; i < Math.Max(1, repeat); i++) n.CopyTo(scratch.Dc);
                    msFullCopy = sw.Elapsed.TotalMilliseconds / Math.Max(1, repeat);
                }
                // 最后再画一遍：上面掐完表缓冲区里就是这一帧
                DrawTurn(buf, o, n, rise, fall, page, u, baseFade);
                Gdi.SelectObject(buf, was);
                was = IntPtr.Zero;
                return Image.FromHbitmap(bmp);
            }
            finally
            {
                if (was != IntPtr.Zero) Gdi.SelectObject(buf, was);
                Gdi.DeleteObject(bmp);
                Gdi.DeleteDC(buf);
                Gdi.ReleaseDC(IntPtr.Zero, sdc);
            }
        }
    }

    // 第二次截屏（整块截图 + 切卡片 + 转 GDI 位图）要几十毫秒，放后台做，渲染线程照常出帧。
    Task<Frame> GrabNew()
    {
        IntPtr host = _hostHwnd;
        Rectangle area = _area;
        Rectangle screen = Bounds;
        bool fast = _hiddenFromCapture;
        Rectangle[] to = _to;
        bool[] plain = _toPlain;
        Color bg = _bg;
        return Task.Run(() =>
        {
            Bitmap shot = fast ? Grab(screen) : PrintHost(host, area);
            if (shot == null) return null;
            DumpForDebug(shot);
            using (shot) return Frame.From(shot, to, bg, plain);
        });
    }

    // 一张图常驻在自己的内存 DC 里，每帧直接拿来贴。
    sealed class GdiImage : IDisposable
    {
        public readonly IntPtr Dc;
        public readonly int W, H;
        readonly IntPtr _bmp, _old;

        public GdiImage(Bitmap src) : this(src, false) { }

        // alpha：留着每个像素的透明度（预乘过的），给 AlphaBlend 的 AC_SRC_ALPHA 用；不留就按不透明的转
        GdiImage(Bitmap src, bool alpha)
        {
            W = src.Width;
            H = src.Height;
            _bmp = alpha ? src.GetHbitmap(Color.FromArgb(0)) : src.GetHbitmap();
            Dc = Gdi.CreateCompatibleDC(IntPtr.Zero);
            _old = Gdi.SelectObject(Dc, _bmp);
        }

        GdiImage(int w, int h, IntPtr like)
        {
            W = w;
            H = h;
            _bmp = Gdi.CreateCompatibleBitmap(like, w, h);
            Dc = Gdi.CreateCompatibleDC(like);
            _old = Gdi.SelectObject(Dc, _bmp);
        }

        // 屏幕直接 BitBlt 进内存位图：比 CopyFromScreen 截成 GDI+ 位图再 GetHbitmap 一道省一半以上
        // （队列里换人那一下，截图 40~80ms + 转格式 15~28ms，是点下去到浮层盖上的大头）。
        public static GdiImage FromScreen(Rectangle screen)
        {
            IntPtr sdc = Gdi.GetDC(IntPtr.Zero);
            if (sdc == IntPtr.Zero) return null;
            try
            {
                var img = new GdiImage(screen.Width, screen.Height, sdc);
                if (!Gdi.BitBlt(img.Dc, 0, 0, screen.Width, screen.Height, sdc, screen.X, screen.Y, Gdi.SRCCOPY))
                {
                    img.Dispose();
                    return null;
                }
                return img;
            }
            finally { Gdi.ReleaseDC(IntPtr.Zero, sdc); }
        }

        // 把一张 GDI+ 位图放进跟屏幕兼容的位图里（跟 FromScreen 截出来的是同一种）
        public static GdiImage LikeScreen(Bitmap src)
        {
            IntPtr sdc = Gdi.GetDC(IntPtr.Zero);
            try
            {
                var img = new GdiImage(src.Width, src.Height, sdc);
                using (var from = new GdiImage(src)) from.CopyTo(img.Dc);
                return img;
            }
            finally { Gdi.ReleaseDC(IntPtr.Zero, sdc); }
        }

        // 一条横着的黑色渐变，每个像素带自己的透明度（rising：左边全透明、右边全黑）。拉伸着叠上去就是一道
        // 由浅到深的影子，一次 AlphaBlend 叠完。
        public static GdiImage Ramp(bool rising)
        {
            const int N = 64;
            using (var b = new Bitmap(N, 1, PixelFormat.Format32bppPArgb))
            {
                for (int i = 0; i < N; i++)
                    b.SetPixel(i, 0, Color.FromArgb((rising ? i : N - 1 - i) * 255 / (N - 1), 0, 0, 0));
                return new GdiImage(b, true);
            }
        }

        public void CopyTo(IntPtr dst) { Gdi.BitBlt(dst, 0, 0, W, H, Dc, 0, 0, Gdi.SRCCOPY); }

        public void Dispose()
        {
            Gdi.SelectObject(Dc, _old);
            Gdi.DeleteDC(Dc);
            Gdi.DeleteObject(_bmp);
        }
    }

    static class Gdi
    {
        public const int SRCCOPY = 0x00CC0020;
        public const int COLORONCOLOR = 3;
        [System.Runtime.InteropServices.DllImport("gdi32.dll")] public static extern int SetStretchBltMode(IntPtr hdc, int mode);
        public const uint LWA_ALPHA = 0x2;
        public const int AC_SRC_ALPHA = 1 << 24;   // BLENDFUNCTION.AlphaFormat：源图每个像素带自己的透明度
        public const uint PW_RENDERFULLCONTENT = 0x2;
        public const int DWMWA_TRANSITIONS_FORCEDISABLED = 3;
        public const uint WDA_EXCLUDEFROMCAPTURE = 0x11;   // Win10 2004 起；老系统调用失败，退回 PrintWindow
        [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
        [System.Runtime.InteropServices.DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        public struct RECT { public int L, T, R, B; }
        [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
        [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
        [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr hwnd, ref Point p);
        [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint key, byte alpha, uint flags);
        [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hwnd);
        [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
        [System.Runtime.InteropServices.DllImport("dwmapi.dll")] public static extern int DwmFlush();
        [System.Runtime.InteropServices.DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [System.Runtime.InteropServices.DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);
        [System.Runtime.InteropServices.DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
        [System.Runtime.InteropServices.DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr obj);
        [System.Runtime.InteropServices.DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr hdc);
        [System.Runtime.InteropServices.DllImport("gdi32.dll")] public static extern bool StretchBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int sw, int sh, int rop);
        [System.Runtime.InteropServices.DllImport("gdi32.dll")] public static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int rop);
        // BLENDFUNCTION 按值传 4 字节：BlendOp | Flags<<8 | SourceConstantAlpha<<16 | AlphaFormat<<24
        [System.Runtime.InteropServices.DllImport("msimg32.dll")] public static extern bool AlphaBlend(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int sw, int sh, int blend);
    }

    static float Clamp(float v) { return v < 0f ? 0f : (v > 1f ? 1f : v); }

    static Rectangle[] ToLocal(Rectangle[] panes, Rectangle area)
    {
        var r = new Rectangle[panes.Length];
        for (int i = 0; i < panes.Length; i++)
            r[i] = panes[i].Width <= 0 ? Rectangle.Empty
                : new Rectangle(panes[i].X - area.X, panes[i].Y - area.Y, panes[i].Width, panes[i].Height);
        return r;
    }

    // 让系统把宿主窗口（连同嵌在里面的客户端）渲染一份，切出客户区里 area 那块。
    // PW_RENDERFULLCONTENT 走 DWM，拿得到 Electron 的 GPU 合成内容，而且只看这一个窗口。
    static Bitmap PrintHost(IntPtr host, Rectangle area)
    {
        Gdi.RECT wr;
        if (!Gdi.GetWindowRect(host, out wr)) return null;
        var origin = new Point(0, 0);
        if (!Gdi.ClientToScreen(host, ref origin)) return null;
        int ww = wr.R - wr.L, wh = wr.B - wr.T;
        if (ww <= 0 || wh <= 0 || area.Width <= 0 || area.Height <= 0) return null;
        try
        {
            using (var whole = new Bitmap(ww, wh, PixelFormat.Format32bppRgb))
            {
                using (Graphics g = Graphics.FromImage(whole))
                {
                    IntPtr hdc = g.GetHdc();
                    bool ok;
                    try { ok = Gdi.PrintWindow(host, hdc, Gdi.PW_RENDERFULLCONTENT); }
                    finally { g.ReleaseHdc(hdc); }
                    if (!ok) return null;
                }
                var src = new Rectangle(origin.X - wr.L + area.X, origin.Y - wr.T + area.Y, area.Width, area.Height);
                src.Intersect(new Rectangle(0, 0, ww, wh));
                if (src.Width != area.Width || src.Height != area.Height) return null;
                return whole.Clone(src, PixelFormat.Format32bppRgb);
            }
        }
        catch { return null; }
    }

    static Bitmap Grab(Rectangle screen)
    {
        if (screen.Width <= 0 || screen.Height <= 0) return null;
        Bitmap bmp = null;
        try
        {
            bmp = new Bitmap(screen.Width, screen.Height, PixelFormat.Format32bppRgb);
            using (Graphics g = Graphics.FromImage(bmp))
                g.CopyFromScreen(screen.Location, Point.Empty, screen.Size);
            return bmp;
        }
        catch
        {
            if (bmp != null) bmp.Dispose();
            return null;
        }
    }

    // 卡片收走后露出来的底色：Mica 截不到「没有卡片」的样子，取格子之间缝里的颜色平均一下。
    static Color GapColor(Bitmap shot, Rectangle[] cards)
    {
        long r = 0, g = 0, b = 0, n = 0;
        int step = Math.Max(1, shot.Width / 64);
        for (int y = 1; y < shot.Height; y += Math.Max(1, shot.Height / 48))
            for (int x = 1; x < shot.Width; x += step)
            {
                bool inCard = false;
                for (int i = 0; i < cards.Length && !inCard; i++)
                {
                    Rectangle c = cards[i];
                    if (c.Width > 0 && x >= c.X - 4 && x < c.Right + 4 && y >= c.Y - 4 && y < c.Bottom + 4) inCard = true;
                }
                if (inCard) continue;
                Color px = shot.GetPixel(x, y);
                r += px.R; g += px.G; b += px.B; n++;
            }
        if (n == 0) return shot.GetPixel(0, 0);
        return Color.FromArgb((int)(r / n), (int)(g / n), (int)(b / n));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // 先停渲染线程再放 GDI 对象：它可能正拿着卡片在画。
            _stop = true;
            if (_render != null && _render.IsAlive) _render.Join(200);
            _old.Dispose();
            if (_new != null) _new.Dispose();
            foreach (GdiImage g in _plain.Values) g.Dispose();
        }
        base.Dispose(disposing);
    }
}
