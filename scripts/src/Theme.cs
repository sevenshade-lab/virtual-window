// 【拆分】颜色、圆角、阴影、PaintUnder。（从原 AgentHost.cs 原样搬出，逻辑未改）

using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

// 宿主整片是 Mica。DWM 只认像素的 alpha：GDI 画的东西 alpha 全是 0，
// 在 Mica 上会被冲成透明（按钮、文字全没了），所以宿主里所有可见的东西
// 都要用 GDI+ 画进双缓冲（带 alpha），不能用系统 Button/Label。
// 颜色跟系统浅色/深色走，半透明的填充让底下的 Mica 透出来。
internal static class Theme
{
    public static bool Light = true;
    // Bar 是以前工具条那张胶囊卡片的底色：2026-10-12 起工具条没有卡片，宿主里没人读它了（tools\CardTests.cs 还拿它当一种底色画）
    public static Color Ink, Dim, Line, Bar, Pane, Btn, BtnHot, BtnDown, BtnLine, Shadow;
    // 强调色照 Google（Material 3 / Gemini）：浅色 #0B57D0 配白字，深色 #A8C7FA 配深蓝字（OnBrand）
    public static Color Brand = Color.FromArgb(255, 11, 87, 208);
    public static Color BrandHot = Color.FromArgb(255, 26, 103, 216);
    public static Color OnBrand = Color.White;
    public static Color Backdrop = Color.Transparent;   // 宿主整片底色（盖在 Mica / Acrylic 上）
    public static Color OkBg, OkInk, ChipBg;

    // 外观：跟随系统 / 浅色 / 深色（照 Que 的三档，Prefs "theme" = auto / light / dark）
    public static string Preference
    {
        get
        {
            string p = Prefs.GetStr("theme", "auto");
            return p == "light" || p == "dark" ? p : "auto";
        }
    }

    // 配色：google（默认）/ classic（原来的橙色，Prefs "palette"）
    public static string Palette { get { return Prefs.GetStr("palette", "google") == "classic" ? "classic" : "google"; } }
    public static bool Classic;

    public static void Load()
    {
        Classic = Palette == "classic";
        string pref = Preference;
        if (pref == "light") Light = true;
        else if (pref == "dark") Light = false;
        else
        {
            try
            {
                object v = Microsoft.Win32.Registry.GetValue(
                    @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                    "AppsUseLightTheme", 1);
                Light = !(v is int) || (int)v != 0;
            }
            catch { Light = true; }
        }
        // 两套配色（用户 2026-09-26：原来的红边框也挺好，要能跟谷歌风格自由切）。字体两套都用 Noto Sans SC。
        //   经典橙：原来那套 —— 白底描边按钮、橙色强调 / 焦点框；
        //   谷歌蓝：Material 3 / Gemini 色板，无描边胶囊按钮、蓝色强调。
        OkBg = Light ? Color.FromArgb(255, 226, 245, 230) : Color.FromArgb(255, 30, 70, 44);
        OkInk = Light ? Color.FromArgb(255, 30, 122, 58) : Color.FromArgb(255, 150, 220, 170);
        ChipBg = Light ? Color.FromArgb(18, 0, 0, 0) : Color.FromArgb(30, 255, 255, 255);
        if (Classic)
        {
            Brand = Color.FromArgb(255, 201, 100, 66);
            BrandHot = Color.FromArgb(255, 214, 116, 82);
            OnBrand = Color.White;
            if (Light)
            {
                Ink = Color.FromArgb(255, 28, 28, 30);
                Dim = Color.FromArgb(255, 96, 96, 104);
                Line = Color.FromArgb(34, 0, 0, 0);
                Bar = Color.FromArgb(236, 255, 255, 255);
                Pane = Color.FromArgb(236, 255, 255, 255);
                Btn = Color.FromArgb(255, 255, 255, 255);
                BtnHot = Color.FromArgb(255, 244, 244, 245);
                BtnDown = Color.FromArgb(255, 234, 234, 236);
                BtnLine = Color.FromArgb(36, 0, 0, 0);
                Shadow = Color.FromArgb(255, 20, 24, 40);
                Backdrop = Color.Transparent;
            }
            else
            {
                // 深色 = 半透明黑（用户 2026-09-26：「深色能不能采用半透明的那种黑色」）：底下是 Acrylic，
                // 卡片 / 工具栏只盖六成黑，后面的模糊透得上来；分层靠一圈淡亮边（照 Que）。文字 #e8e8e8 / #a4a4a4。
                Ink = Color.FromArgb(255, 232, 232, 232);
                Dim = Color.FromArgb(255, 164, 164, 164);
                Line = Color.FromArgb(30, 255, 255, 255);
                Bar = Color.FromArgb(150, 18, 18, 20);
                Pane = Color.FromArgb(150, 18, 18, 20);
                Btn = Color.FromArgb(34, 255, 255, 255);
                BtnHot = Color.FromArgb(58, 255, 255, 255);
                BtnDown = Color.FromArgb(24, 255, 255, 255);
                BtnLine = Color.FromArgb(44, 255, 255, 255);
                Shadow = Color.FromArgb(255, 0, 0, 0);
                Backdrop = Color.FromArgb(115, 0, 0, 0);
            }
        }
        else if (Light)
        {
            // Google 浅色：蓝灰底 #F0F4F9、白卡片、正文 #1F1F1F、次要字 #5F6368、分隔线 #DADCE0；
            // 按钮是没有描边的胶囊，悬停出 #E9EEF6 的淡蓝灰
            Ink = Color.FromArgb(255, 31, 31, 31);
            Dim = Color.FromArgb(255, 95, 99, 104);
            Line = Color.FromArgb(255, 218, 220, 224);
            Bar = Color.FromArgb(242, 255, 255, 255);
            Pane = Color.FromArgb(242, 255, 255, 255);
            Btn = Color.FromArgb(0, 255, 255, 255);
            BtnHot = Color.FromArgb(255, 233, 238, 246);
            BtnDown = Color.FromArgb(255, 221, 227, 234);
            BtnLine = Color.FromArgb(0, 0, 0, 0);
            Shadow = Color.FromArgb(255, 60, 64, 67);
            Brand = Color.FromArgb(255, 11, 87, 208);
            BrandHot = Color.FromArgb(255, 26, 103, 216);
            OnBrand = Color.White;
            Backdrop = Color.FromArgb(170, 240, 244, 249);
        }
        else
        {
            // Google 深色（Gemini）：中性灰 #131314 / #1E1F20 / #282A2C，正文 #E3E3E3、次要字 #9AA0A6；
            // 仍是半透明的，只是黑里带一点 Gemini 的灰，不死黑
            Ink = Color.FromArgb(255, 227, 227, 227);
            Dim = Color.FromArgb(255, 154, 160, 166);
            Line = Color.FromArgb(255, 60, 64, 67);
            Bar = Color.FromArgb(178, 30, 31, 32);
            Pane = Color.FromArgb(178, 30, 31, 32);
            Btn = Color.FromArgb(0, 0, 0, 0);
            BtnHot = Color.FromArgb(235, 51, 53, 55);
            BtnDown = Color.FromArgb(235, 60, 64, 67);
            BtnLine = Color.FromArgb(0, 0, 0, 0);
            Shadow = Color.FromArgb(255, 0, 0, 0);
            Brand = Color.FromArgb(255, 168, 199, 250);
            BrandHot = Color.FromArgb(255, 185, 211, 251);
            OnBrand = Color.FromArgb(255, 6, 46, 111);
            Backdrop = Color.FromArgb(150, 19, 19, 20);
        }
    }

    // 子控件是独立 HWND，会把父窗口画的半透明底整块挡掉。
    // 先清成全透明，再让父窗口把自己这块画进来，看起来就连成一片。
    // 画失败不往外抛（WinForms 会把这个子控件永久画成红叉），见 PaintGuard
    public static void PaintUnder(Control c, PaintEventArgs e)
    {
        try
        {
            e.Graphics.Clear(Color.Transparent);
            Control p = c.Parent;
            if (p == null) return;
            GraphicsState st = e.Graphics.Save();
            try
            {
                e.Graphics.TranslateTransform(-c.Left, -c.Top);
                Rectangle clip = new Rectangle(c.Left, c.Top, c.Width, c.Height);
                var pe = new PaintEventArgs(e.Graphics, clip);
                InvokePaintBackground(p, pe);
                InvokePaint(p, pe);
            }
            finally { e.Graphics.Restore(st); }
        }
        catch (System.Exception ex) { PaintGuard.Failed(c, ex); }
    }

    static readonly System.Reflection.MethodInfo _pb = typeof(Control).GetMethod("OnPaintBackground",
        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
    static readonly System.Reflection.MethodInfo _p = typeof(Control).GetMethod("OnPaint",
        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
    static void InvokePaintBackground(Control p, PaintEventArgs e) { _pb.Invoke(p, new object[] { e }); }
    static void InvokePaint(Control p, PaintEventArgs e) { _p.Invoke(p, new object[] { e }); }

    public static void Glassy(Control c)
    {
        typeof(Control).GetMethod("SetStyle", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            .Invoke(c, new object[] { ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw
                | ControlStyles.SupportsTransparentBackColor, true });
    }

    public static GraphicsPath Round(RectangleF r, float rad)
    {
        var gp = new GraphicsPath();
        // 空矩形直接给空路径：GDI+ 的 AddArc 遇到 0 尺寸抛 ArgumentException，
        // 而 OnPaint 抛过一次，WinForms 就把整个宿主永久画成红叉。
        // .NET 10 下启动时会提前重画一次：Load 里 HostWins.Visible() 调
        // Process.GetProcessById，它在 STA 线程上等待时会处理消息，那时还没排版，卡片尺寸是 0。
        if (r.Width <= 0 || r.Height <= 0) return gp;
        float d = rad * 2;
        if (d > r.Width) d = r.Width;
        if (d > r.Height) d = r.Height;
        if (d <= 0) { gp.AddRectangle(r); return gp; }
        gp.AddArc(r.X, r.Y, d, d, 180, 90);
        gp.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        gp.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        gp.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        gp.CloseFigure();
        return gp;
    }

    // 卡片 = 软阴影（几圈逐渐变大的半透明圆角矩形叠出来，往下偏一点）+ 圆角底 + 一圈细边。
    // 阴影要叠 8 圈抗锯齿的半透明大面积填充，现画太慢：子控件重画又会让宿主把自己那块再画一遍 —— 队列视图
    // 切一次前台，宿主 OnPaint 21 次共 130~170ms，点其它 agent 手感发粘就是它（2026-09-26 掐表）。所以画一次存起来。
    //
    // 以前是按卡片尺寸存整张图：铺满的那张一张十几兆，浅色深色、几种尺寸各一张，最多留 16 张 ——
    // 宿主一百多兆内存里它占大头（用户 2026-10-12：「作为一个工具占用的内存不能很大」）。
    // 现在只存一小块样片：四个角，加上四条边和中间各两像素。画的时候拼 —— 角原样贴，边拉长，中间是同一个颜色、
    // 直接填。一块样片二三十 KB，跟卡片多大没关系；拼出来跟整张画的逐像素一样（tools\CardTests.cs 比过）。
    sealed class Tile
    {
        public Bitmap Bmp;
        public int Pad, Down;      // 阴影往外伸多少、往下偏多少（跟整张图的留边一样）
        public int Cw, Ct, Cb;     // 角那一块多宽；上面的角多高、下面的角多高（阴影往下偏，上下不一样）
        public Color Mid;          // 卡片中间的颜色：底色压在几圈阴影上面合出来的，不等于 fill
    }

    static readonly System.Collections.Generic.Dictionary<string, Tile> _tiles = new System.Collections.Generic.Dictionary<string, Tile>();

    static Tile TileFor(float rad, Color fill, float scale)
    {
        string key = rad + "|" + fill.ToArgb() + "|" + Light + "|" + scale;
        Tile t;
        if (_tiles.TryGetValue(key, out t)) return t;
        // 换配色、换主题攒下来的旧样片：不多，但也没必要留着
        if (_tiles.Count >= 24) { foreach (Tile old in _tiles.Values) old.Bmp.Dispose(); _tiles.Clear(); }
        t = new Tile();
        t.Pad = (int)System.Math.Ceiling(8 * 1.6f * scale) + 1;
        t.Down = (int)System.Math.Ceiling(3 * scale);
        int corner = (int)System.Math.Ceiling(rad) + 1;
        // 边上「不随位置变」的那一段：卡片自己是圆角以外，阴影整体往下偏了 Down，所以上面的角要多算 Down 这么高
        t.Cw = t.Pad + corner;
        t.Ct = t.Pad + corner + t.Down;
        t.Cb = corner + t.Pad + t.Down;
        int w = t.Cw * 2 + 2, h = t.Ct + t.Cb + 2;
        t.Bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        using (Graphics bg = Graphics.FromImage(t.Bmp))
            CardDirect(bg, new Rectangle(t.Pad, t.Pad, w - t.Pad * 2, h - t.Pad * 2 - t.Down), rad, fill, scale);
        t.Mid = t.Bmp.GetPixel(t.Cw, t.Ct);
        _tiles[key] = t;
        return t;
    }

    public static void Card(Graphics g, Rectangle r, float rad, Color fill, float scale)
    {
        if (r.Width <= 0 || r.Height <= 0) return;
        Tile t = TileFor(rad, fill, scale);
        int w = r.Width + t.Pad * 2, h = r.Height + t.Pad * 2 + t.Down;
        GraphicsState st = g.Save();
        try
        {
            // 小到四个角都放不开（很少见）：就地整张画，不拼
            if (w < t.Cw * 2 + 1 || h < t.Ct + t.Cb + 1) { CardDirect(g, r, rad, fill, scale); return; }
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.CompositingQuality = CompositingQuality.HighSpeed;
            g.PixelOffsetMode = PixelOffsetMode.Half;   // 拉伸时正好取那一列 / 那一行，不往旁边偏半个像素
            g.SmoothingMode = SmoothingMode.None;
            int x0 = r.X - t.Pad, y0 = r.Y - t.Pad, x1 = x0 + w, y1 = y0 + h;
            int tw = t.Bmp.Width, th = t.Bmp.Height;
            int mw = w - t.Cw * 2, mh = h - t.Ct - t.Cb;
            // 四个角
            Part(g, t.Bmp, x0, y0, t.Cw, t.Ct, 0, 0, t.Cw, t.Ct);
            Part(g, t.Bmp, x1 - t.Cw, y0, t.Cw, t.Ct, tw - t.Cw, 0, t.Cw, t.Ct);
            Part(g, t.Bmp, x0, y1 - t.Cb, t.Cw, t.Cb, 0, th - t.Cb, t.Cw, t.Cb);
            Part(g, t.Bmp, x1 - t.Cw, y1 - t.Cb, t.Cw, t.Cb, tw - t.Cw, th - t.Cb, t.Cw, t.Cb);
            // 四条边：取一像素宽的一条拉长
            if (mw > 0)
            {
                Part(g, t.Bmp, x0 + t.Cw, y0, mw, t.Ct, t.Cw, 0, 1, t.Ct);
                Part(g, t.Bmp, x0 + t.Cw, y1 - t.Cb, mw, t.Cb, t.Cw, th - t.Cb, 1, t.Cb);
            }
            if (mh > 0)
            {
                Part(g, t.Bmp, x0, y0 + t.Ct, t.Cw, mh, 0, t.Ct, t.Cw, 1);
                Part(g, t.Bmp, x1 - t.Cw, y0 + t.Ct, t.Cw, mh, tw - t.Cw, t.Ct, t.Cw, 1);
            }
            // 中间：一个颜色
            if (mw > 0 && mh > 0)
                using (var mid = new SolidBrush(t.Mid))
                    g.FillRectangle(mid, x0 + t.Cw, y0 + t.Ct, mw, mh);
        }
        finally { g.Restore(st); }
    }

    static void Part(Graphics g, Bitmap src, int dx, int dy, int dw, int dh, int sx, int sy, int sw, int sh)
    {
        g.DrawImage(src, new Rectangle(dx, dy, dw, dh), sx, sy, sw, sh, GraphicsUnit.Pixel);
    }

    // 整张现画的卡片：样片就是拿它画的；测试也拿它当「以前整张存的那种」来比（tools\CardTests.cs）
    internal static void CardWhole(Graphics g, Rectangle r, float rad, Color fill, float scale)
    {
        if (r.Width <= 0 || r.Height <= 0) return;
        int pad = (int)System.Math.Ceiling(8 * 1.6f * scale) + 1;
        int down = (int)System.Math.Ceiling(3 * scale);
        using (var bmp = new Bitmap(r.Width + pad * 2, r.Height + pad * 2 + down, System.Drawing.Imaging.PixelFormat.Format32bppPArgb))
        {
            using (Graphics bg = Graphics.FromImage(bmp))
                CardDirect(bg, new Rectangle(pad, pad, r.Width, r.Height), rad, fill, scale);
            GraphicsState st = g.Save();
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.CompositingQuality = CompositingQuality.HighSpeed;
            g.DrawImage(bmp, new Rectangle(r.X - pad, r.Y - pad, bmp.Width, bmp.Height), 0, 0, bmp.Width, bmp.Height, GraphicsUnit.Pixel);
            g.Restore(st);
        }
    }
    static void CardDirect(Graphics g, Rectangle r, float rad, Color fill, float scale)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int layers = 8;
        for (int i = layers; i >= 1; i--)
        {
            float grow = i * 1.6f * scale;
            RectangleF sr = new RectangleF(r.X - grow, r.Y - grow + 3 * scale, r.Width + grow * 2, r.Height + grow * 2);
            int a = (int)(Light ? 3 + (layers - i) * 1.2f : 2 + (layers - i) * 1.5f);
            using (GraphicsPath sp = Round(sr, rad + grow))
            using (var sb = new SolidBrush(Color.FromArgb(a, Shadow)))
                g.FillPath(sb, sp);
        }
        using (GraphicsPath gp = Round(new RectangleF(r.X, r.Y, r.Width, r.Height), rad))
        using (var br = new SolidBrush(fill))
        using (var pen = new Pen(Color.FromArgb(Light ? 14 : 30, Light ? Color.Black : Color.White)))
        {
            g.FillPath(br, gp);
            g.DrawPath(pen, gp);
        }
    }

    // 画 agent 图标：统一成同样大小、同样圆角的「应用方块」（用户 2026-09-26：各家图标看着不协调 ——
    // 以前按深浅给一部分垫底，有的有底有的没有、底还比别家自带的方块大一圈）。
    //   自带方块的（Cursor / OpenCode / Claude）：裁掉四周透明边，填满方块、按统一圆角裁；
    //   只是一个标志的（Codex / Antigravity）：放进同尺寸的方块，标志缩到七成居中 ——
    //     亮标志 / 彩色标志配深灰方块，跟 Cursor、OpenCode 的深色方块一个样；暗标志配白方块。
    //   深色主题下每个方块描一圈淡亮边，深方块放在深卡片上才分得出来。
    // pad 参数留着兼容老调用，不再往外扩。
    public static void Icon(Graphics g, Bitmap icon, Rectangle ir, float pad, float rad)
    {
        if (icon == null) return;
        IconInfo info = Info(icon);
        float r = ir.Width * 0.26f;
        var st = g.Save();
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        using (GraphicsPath tile = Round(new RectangleF(ir.X, ir.Y, ir.Width, ir.Height), r))
        {
            if (info.Tile)
            {
                g.SetClip(tile);
                g.DrawImage(icon, ir, info.Box, GraphicsUnit.Pixel);
                g.ResetClip();
                // 白方块（Antigravity）放在浅色白卡片上没轮廓，描一圈细边
                if (info.Bright && Light)
                    using (var pen = new Pen(Color.FromArgb(64, 0, 0, 0))) g.DrawPath(pen, tile);
            }
            else
            {
                Color back = info.Dark ? Color.White : (Light ? Color.FromArgb(255, 60, 64, 67) : Color.FromArgb(255, 48, 49, 52));
                using (var tb = new SolidBrush(back)) g.FillPath(tb, tile);
                if (info.Dark && Light)
                    using (var pen = new Pen(Color.FromArgb(40, 0, 0, 0))) g.DrawPath(pen, tile);
                // 标志按自己的不透明外框缩放到方块的 68%，保持比例居中
                float inner = ir.Width * 0.68f;
                float k = inner / System.Math.Max(info.Box.Width, info.Box.Height);
                float w = info.Box.Width * k, h = info.Box.Height * k;
                g.DrawImage(icon, new RectangleF(ir.X + (ir.Width - w) / 2f, ir.Y + (ir.Height - h) / 2f, w, h), info.Box, GraphicsUnit.Pixel);
            }
            if (!Light)
                using (var pen = new Pen(Color.FromArgb(52, 255, 255, 255))) g.DrawPath(pen, tile);
        }
        g.Restore(st);
    }

    sealed class IconInfo { public bool Tile, Dark, Bright; public RectangleF Box; }
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Bitmap, IconInfo> _iconInfo = new System.Runtime.CompilerServices.ConditionalWeakTable<Bitmap, IconInfo>();

    // 看一遍像素：不透明外框、外框里有多满（满 = 自带方块）、标志本身是亮是暗。图标很小，结果按位图缓存
    static IconInfo Info(Bitmap b)
    {
        IconInfo v;
        if (_iconInfo.TryGetValue(b, out v)) return v;
        v = new IconInfo { Box = new RectangleF(0, 0, b.Width, b.Height) };
        try
        {
            int x0 = b.Width, y0 = b.Height, x1 = -1, y1 = -1;
            for (int y = 0; y < b.Height; y++)
                for (int x = 0; x < b.Width; x++)
                    if (b.GetPixel(x, y).A > 40) { if (x < x0) x0 = x; if (y < y0) y0 = y; if (x > x1) x1 = x; if (y > y1) y1 = y; }
            if (x1 >= x0 && y1 >= y0)
            {
                v.Box = new RectangleF(x0, y0, x1 - x0 + 1, y1 - y0 + 1);
                int opaque = 0, total = 0; double lum = 0; int n = 0;
                for (int y = y0; y <= y1; y++)
                    for (int x = x0; x <= x1; x++)
                    {
                        Color c = b.GetPixel(x, y);
                        total++;
                        if (c.A > 200) opaque++;
                        if (c.A > 100) { lum += (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0; n++; }
                    }
                // 外框里 80% 以上是实心的：自带方块（圆角会吃掉一点角）
                v.Tile = total > 0 && opaque >= total * 0.8;
                v.Dark = n > 0 && lum / n < 0.3;
                v.Bright = n > 0 && lum / n > 0.75;
            }
        }
        catch { }
        _iconInfo.Add(b, v);
        return v;
    }

    // 折行写：一行放不下就换行，只写得下完整的几行，再多的收成省略号。给比位置宽的一句话用（工具条的临时提示）
    public static void TextWrap(Graphics g, string s, Font f, Color c, RectangleF r)
    {
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        using (var br = new SolidBrush(c))
        using (var sf = new StringFormat(StringFormatFlags.LineLimit))
        {
            sf.Alignment = StringAlignment.Near;
            sf.LineAlignment = StringAlignment.Center;
            sf.Trimming = StringTrimming.EllipsisWord;
            g.DrawString(s, f, br, r, sf);
        }
    }

    public static void Text(Graphics g, string s, Font f, Color c, RectangleF r, StringAlignment h)
    {
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        using (var br = new SolidBrush(c))
        using (var sf = new StringFormat(StringFormatFlags.NoWrap))
        {
            sf.Alignment = h;
            sf.LineAlignment = StringAlignment.Center;
            sf.Trimming = StringTrimming.EllipsisCharacter;
            g.DrawString(s ?? "", f, br, r, sf);
        }
    }
}

// 字体（2026-09-26「参考谷歌」）：有 Noto Sans SC（Google 的字体家族，本机装着）就用它，没有退回微软雅黑。
// 标题不用粗体，用 Medium（500）—— Google 的界面就是这个字重，粗体显得笨重。
// GDI+ 把每个字重列成单独的字族（「Noto Sans SC Medium」），所以 Medium 要按字族名取。
internal static class Fonts
{
    static bool Has(string name)
    {
        try { using (var f = new Font(name, 9f)) return string.Equals(f.Name, name, System.StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    static string _base, _medium;
    static bool _probed;
    static void Probe()
    {
        if (_probed) return;
        _probed = true;
        _base = Has("Noto Sans SC") ? "Noto Sans SC" : "Microsoft YaHei UI";
        _medium = _base == "Noto Sans SC" && Has("Noto Sans SC Medium") ? "Noto Sans SC Medium" : null;
    }

    public static string Family { get { Probe(); return _base; } }
    public static Font Regular(float size) { Probe(); return new Font(_base, size, FontStyle.Regular); }
    public static Font Medium(float size) { Probe(); return _medium != null ? new Font(_medium, size, FontStyle.Regular) : new Font(_base, size, FontStyle.Bold); }
}
