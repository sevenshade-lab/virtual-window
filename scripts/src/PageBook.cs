// 书页：队列视图里排在中间那张后面等你的会话，每家一页。
// 右边露出一条页边，页边上贴一个索引签（图标 + 状态灯，停下来等你的还写一小句它要你做什么）：悬停出提示，点一下翻到那一页。
// 2026-10-12 之前右边是最多两张空白卡片，只表示「后面还有」—— 认不出是哪一家，也点不了。
//
// 这里只管几何、命中、画页边和索引签的底，不碰窗体的状态：宿主拿去排版和画（HostForm.Queue.cs），
// 测试也能不开窗、离屏画出来看（tools\PageBookTests.cs）。

using System;
using System.Drawing;
using System.Drawing.Drawing2D;

internal static class PageBook
{
    // 都按 96dpi 写，乘上 scale 才是像素
    const float StackW = 42;    // 给页边留的总宽：页少时每页露 14，页多了平摊
    const float MaxStep = 14;
    const float TabW = 42;      // 索引签伸出页边的那一段
    const float TabH = 40;
    const float TabGap = 6;
    const float IconSize = 26;  // 跟侧栏方块里的图标一样大
    const float Radius = 12;    // 页的圆角，跟卡片一样
    // 签上写的那一小句在图标下面：从签顶往下 34 开始（图标底下留 3），一行 14，最后一行下面留 5
    const float CapTop = 34, CapLine = 14, CapPad = 5;

    static int D(float v, float scale) { return (int)Math.Round(v * scale); }

    internal sealed class Geometry
    {
        public Rectangle Front;     // 中间那张
        public Rectangle[] Pages;   // 每一页：跟中间那张一样宽的卡片，往右错开一点，只有右边那一条露着
        public Rectangle[] Tabs;    // 索引签露在外面的那一块（悬停、点击都认它）
        public Rectangle[] Icons;   // 签上图标的位置
        public Rectangle[] Texts;   // 签上写字的位置（图标下面）；这一个签不写字是空矩形，都不写是 null
        public int Count { get { return Pages.Length; } }
        // 页边和索引签一共占的地方（重画时拿它跟脏区比）
        public Rectangle Bounds;
    }

    // 中间那张右边要留出多宽。这个宽度是固定的，不跟着页数变：所有客户端的内容区都跟中间那张
    // 一样大（轮到谁只是换哪个显示），它一变就得全体重排 —— 所以页多了是每页露得少，不是把中间那张挤窄。
    public static int Reserve(float scale) { return D(StackW, scale) + D(TabW, scale); }

    // 写了几行字的签多高（0 行就是平常的 40）
    public static int TabHeight(int lines, float scale)
    {
        return lines <= 0 ? D(TabH, scale) : D(CapTop, scale) + lines * D(CapLine, scale) + D(CapPad, scale);
    }

    // lines：每一页的签上要写几行字（它停下来要你做什么，见 TipNotes.Short；0 = 这个不写）；null = 都不写。
    // 字写在图标下面：有话要说的签高一截，宽度不变，中间那张不用让地方。
    // （签上写字先是试用开关。第一版是把每个签都加长 76、字写在图标左边：没字的签成了一长条空白，中间那张还窄了 76 ——
    //   用户 2026-10-12 在真宿主上看了说「右边书签是不是有点太长了」，改成现在这样；用过说可以，定下来。）
    public static Geometry Layout(Rectangle front, int n, float scale, int headerH, int[] lines = null)
    {
        if (n < 0) n = 0;
        var geo = new Geometry { Front = front, Pages = new Rectangle[n], Tabs = new Rectangle[n], Icons = new Rectangle[n] };
        if (n == 0 || front.Width <= 0 || front.Height <= 0) { geo.Bounds = Rectangle.Empty; return geo; }
        int stack = D(StackW, scale), tabW = D(TabW, scale), tabH = D(TabH, scale), icon = D(IconSize, scale);
        float step = Math.Min(D(MaxStep, scale), stack / (float)n);
        int right = front.Right + stack + tabW;
        // 签从表头下面排起，一个挨一个；排不下（十来页、窗口又矮）就压着叠，前面的压住后面的
        int top = front.Y + headerH + D(6, scale), gap = D(TabGap, scale);
        int last = front.Bottom - stack / 2 - D(14, scale) - tabH;   // 最后一个签最低放到哪：别伸出它自己那一页
        float pitch = tabH + gap;
        if (n > 1 && top + pitch * (n - 1) > last) pitch = Math.Max(D(10, scale), (last - top) / (float)(n - 1));
        // 要写字：每个签按自己的高矮往下排。写了字排不下（页多、窗口矮）这一回就都不写，照上面的排法
        bool captions = lines != null && lines.Length >= n;
        if (captions)
        {
            int bottom = top - gap;
            for (int k = 0; k < n; k++) bottom += gap + TabHeight(lines[k], scale);
            if (bottom > last + tabH) captions = false;
        }
        if (captions) geo.Texts = new Rectangle[n];
        int y = top;
        for (int k = 0; k < n; k++)
        {
            int dx = Math.Max(k + 1, (int)Math.Round(step * (k + 1)));
            int dy = dx / 2;
            geo.Pages[k] = new Rectangle(front.X + dx, front.Y + dy, front.Width, Math.Max(1, front.Height - 2 * dy));
            int ty = captions ? y : top + (int)Math.Round(pitch * k);
            int th = captions ? TabHeight(lines[k], scale) : tabH;
            int tx = geo.Pages[k].Right;
            geo.Tabs[k] = new Rectangle(tx, ty, right - tx, th);
            // 图标往左上偏一点：右下角还压着一颗灯，连灯一起看才是居中的，灯也不会顶到签的边
            geo.Icons[k] = new Rectangle(right - tabW + (tabW - icon) / 2 - D(2, scale), ty + (tabH - icon) / 2 - D(2, scale), icon, icon);
            if (captions)
            {
                // 字在图标正下方，占签伸出页边的那一段的整个宽度（居中写）。两边不能再收：GDI+ 排字时两头各留一点空，
                // 三个汉字（「做完了」）连这点空要 39，英文最长的「Allow?」要 41；左右各收 2（剩 38）的话就被截成「做…」。
                // 工具名长了（WebFetch、write_file）照样截，全名在悬停提示里
                if (lines[k] > 0)
                    geo.Texts[k] = new Rectangle(right - tabW, ty + D(CapTop, scale), tabW, lines[k] * D(CapLine, scale));
                y += th + gap;
            }
        }
        int rad = D(Radius, scale);
        geo.Bounds = Rectangle.FromLTRB(front.Right - rad - 2, front.Y - 2, right + 2, front.Bottom + 2);
        return geo;
    }

    // 这一点落在第几页上：先认索引签，再认露出来的那条页边。都不是返回 -1
    public static int At(Geometry geo, Point p)
    {
        if (geo == null) return -1;
        for (int k = 0; k < geo.Count; k++)
            if (geo.Tabs[k].Contains(p)) return k;
        for (int k = 0; k < geo.Count; k++)
        {
            int left = k == 0 ? geo.Front.Right : geo.Pages[k - 1].Right;
            Rectangle pg = geo.Pages[k];
            if (p.X >= left && p.X < pg.Right && p.Y >= pg.Y && p.Y < pg.Bottom) return k;
        }
        return -1;
    }

    // 画页边和索引签的底（图标和灯由宿主画在上面）。hot：鼠标停在第几页上，-1 = 没有。
    // 从最后一页画到第一页，每页先画它的签、再画它自己露出来的那一条：签压在后面几页上面，跟真的索引签一样。
    // 每一页只画露着的那一条 —— 整页大卡片不值得画，更不能进 Theme.Card 的缓存（一张十几兆）；
    // 也免得半透明的底一层层叠上去，越靠前越实（深色主题下卡片只有六七成不透明）。
    //
    // 要读得出是一叠纸（2026-10-12 设计评审：浅色下页是 #FEFEFE 压在 #F3F3F3 的窗口底上，只靠一条浅线分层，
    // 「看不出后面叠着纸，只看到右上角飘着两个白色小方块」）：
    //   - 越往后的页边越暗一档，最多三档；
    //   - 前一页在后一页上落一道贴边的影子（中间那张落在第一页上的那道是卡片自己带的）；
    //   - 悬停：签和它那一页的页边一起变（以前只有签变灰，看不出它们是一张）。
    // 签不跟着页变暗，都是一样的白：试过让签跟它那一页同色，页一多后面几个签成了一排灰块，像是点不了的。
    // 分档和影子只在浅色下做。深色是用户定的「半透明黑 + 一圈淡亮边」，层次本来就靠那圈边，黑上再压黑也看不出来。
    public static void Paint(Graphics g, Geometry geo, int hot, float scale, Rectangle clip)
    {
        if (geo == null || geo.Count == 0 || !clip.IntersectsWith(geo.Bounds)) return;
        float rad = Radius * scale, tabRad = 9 * scale;
        int pad = (int)Math.Ceiling(rad) + 1;
        bool light = Theme.Light;
        Color edge = Color.FromArgb(light ? EdgeLight : 46, light ? Color.Black : Color.White);
        SmoothingMode sm = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var fill = new SolidBrush(Theme.Pane))
        using (var hotFill = new SolidBrush(light ? Color.FromArgb(HotLight, Color.Black) : Color.FromArgb(HotDark, Color.White)))
        using (var pen = new Pen(edge))
            for (int k = geo.Count - 1; k >= 0; k--)
            {
                Rectangle page = geo.Pages[k], tab = geo.Tabs[k];
                int depth = DepthAlpha(k);
                using (var shade = new SolidBrush(Color.FromArgb(depth, Color.Black)))
                {
                    // 签：左端从自己那页的边上伸出来（再往左的部分被那一页挡着，不画）
                    var tabArea = new Rectangle(page.Right - 1, tab.Y - 1, tab.Width + 3, tab.Height + 3);
                    if (clip.IntersectsWith(tabArea))
                    {
                        GraphicsState st = g.Save();
                        g.SetClip(tabArea, CombineMode.Intersect);
                        using (GraphicsPath tp = Theme.Round(new RectangleF(page.Right - rad, tab.Y + 0.5f, tab.Width + rad - 0.5f, tab.Height - 1f), tabRad))
                        {
                            g.FillPath(fill, tp);
                            if (k == hot) g.FillPath(hotFill, tp);
                            g.DrawPath(pen, tp);
                        }
                        g.Restore(st);
                    }
                    // 页边：只画前一页（或中间那张）右边露出来的那一条，连同前一页圆角后面的那一小块
                    int left = (k == 0 ? geo.Front.Right : geo.Pages[k - 1].Right) - pad;
                    var strip = Rectangle.FromLTRB(left, page.Y - 1, page.Right + 2, page.Bottom + 2);
                    if (clip.IntersectsWith(strip))
                    {
                        GraphicsState st = g.Save();
                        g.SetClip(strip, CombineMode.Intersect);
                        using (GraphicsPath pp = Theme.Round(new RectangleF(page.X + 0.5f, page.Y + 0.5f, page.Width - 1f, page.Height - 1f), rad))
                        {
                            g.FillPath(fill, pp);
                            if (depth > 0) g.FillPath(shade, pp);
                            if (k == hot) g.FillPath(hotFill, pp);
                            if (light && k > 0) CastShadow(g, geo.Pages[k - 1], pp, rad, scale, page.Right - geo.Pages[k - 1].Right);
                            g.DrawPath(pen, pp);
                        }
                        g.Restore(st);
                    }
                }
            }
        g.SmoothingMode = sm;
    }

    // 浅色下的几个数（0～255 的不透明度，都是往白纸上压黑）
    const int EdgeLight = 48;   // 页边和签的描边。以前 34，在 #F3F3F3 的窗口底上只比底深一成
    const int DepthStep = 8;    // 每往后一页暗这么多：白纸上大约是 #FE → #F6 → #EE → #E6
    const int HotLight = 16, HotDark = 30;   // 悬停时再压一层（深色是提亮）

    // 第 k 页（0 = 紧跟在中间那张后面）比中间那张暗多少。测试也拿它核对颜色。
    public static int DepthAlpha(int k) { return Theme.Light ? DepthStep * Math.Min(k + 1, 3) : 0; }

    // 前一页（sheet）落在这一页上的影子：照卡片影子的画法，几圈逐渐放大的圆角矩形叠起来，只留落在这一页里的部分。
    // gap = 这一页露出来多宽；页多了露得窄，影子也跟着收（不然十来页时整条都是灰的）。
    static void CastShadow(Graphics g, Rectangle sheet, GraphicsPath onto, float rad, float scale, int gap)
    {
        int layers = Math.Min(4, (int)Math.Round(gap / (3.2f * scale)));
        if (layers <= 0) return;
        GraphicsState st = g.Save();
        g.SetClip(onto, CombineMode.Intersect);
        using (var sb = new SolidBrush(Color.FromArgb(5, Theme.Shadow)))
            for (int i = layers; i >= 1; i--)
            {
                float grow = i * 1.6f * scale;
                using (GraphicsPath sp = Theme.Round(new RectangleF(sheet.X - grow, sheet.Y - grow + scale, sheet.Width + grow * 2, sheet.Height + grow * 2), rad + grow))
                    g.FillPath(sb, sp);
            }
        g.Restore(st);
    }

    // ---- 左边的签：在干活的那几家 ----
    // 从中间那张的左边伸出来，高矮、间距、从多高排起都跟右边的索引签一样 —— 左边是翻过去、自己在干活的，右边是等你翻的。
    // （2026-10-12 之前它们是左边侧栏里一个个没有底的方块。做成签先是试用开关，用户在真宿主上用过说可以，定下来。）
    // 左边只留签伸出来的这一段（LeftReserve），不留侧栏那 60：签贴着窗口左边。

    // 左边有签时，中间那张左边要留出多宽。跟右边的 Reserve 一样是固定的，不跟着有几个签变
    public static int LeftReserve(float scale) { return D(TabW, scale); }

    public static Rectangle[] LeftTabs(Rectangle front, int n, float scale, int headerH)
    {
        var tabs = new Rectangle[Math.Max(0, n)];
        if (n <= 0 || front.Width <= 0 || front.Height <= 0) return tabs;
        int tabW = D(TabW, scale), tabH = D(TabH, scale);
        int top = front.Y + headerH + D(6, scale);
        int last = front.Bottom - D(14, scale) - tabH;
        float pitch = tabH + D(TabGap, scale);
        if (n > 1 && top + pitch * (n - 1) > last) pitch = Math.Max(D(10, scale), (last - top) / (float)(n - 1));
        for (int k = 0; k < n; k++)
            tabs[k] = new Rectangle(front.X - tabW, top + (int)Math.Round(pitch * k), tabW, tabH);
        return tabs;
    }

    // 左边签上的图标：左右居中，往上偏一点（右下角压着灯，跟右边的签同一个道理）
    public static Rectangle LeftIcon(Rectangle tab, float scale)
    {
        int icon = D(IconSize, scale);
        return new Rectangle(tab.X + (tab.Width - icon) / 2, tab.Y + (tab.Height - icon) / 2 - D(2, scale), icon, icon);
    }

    // 画左边那几个签的底。右端伸到中间那张底下（中间那张后画，盖住它），所以看着是从卡片边上长出来的
    public static void PaintLeft(Graphics g, Rectangle front, Rectangle[] tabs, int hot, float scale, Rectangle clip)
    {
        if (tabs == null || tabs.Length == 0) return;
        float rad = Radius * scale, tabRad = 9 * scale;
        bool light = Theme.Light;
        SmoothingMode sm = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var fill = new SolidBrush(Theme.Pane))
        using (var hotFill = new SolidBrush(light ? Color.FromArgb(HotLight, Color.Black) : Color.FromArgb(HotDark, Color.White)))
        using (var pen = new Pen(Color.FromArgb(light ? EdgeLight : 46, light ? Color.Black : Color.White)))
            for (int k = tabs.Length - 1; k >= 0; k--)
            {
                Rectangle tab = tabs[k];
                var area = Rectangle.FromLTRB(tab.X - 2, tab.Y - 1, front.X + 1, tab.Bottom + 2);
                if (!clip.IntersectsWith(area)) continue;
                GraphicsState st = g.Save();
                g.SetClip(area, CombineMode.Intersect);
                using (GraphicsPath tp = Theme.Round(new RectangleF(tab.X + 0.5f, tab.Y + 0.5f, tab.Width + rad - 0.5f, tab.Height - 1f), tabRad))
                {
                    g.FillPath(fill, tp);
                    if (k == hot) g.FillPath(hotFill, tp);
                    g.DrawPath(pen, tp);
                }
                g.Restore(st);
            }
        g.SmoothingMode = sm;
    }

    // 签上的灯圆心在哪：正压在图标的右下角上。侧栏方块里的灯是再往外 2 —— 方块四周空得多；
    // 签比方块窄，照那个摆法灯离签的右边、下边只剩 3（2026-10-12 评审：「快贴到圆角上」），收回来之后是 5～6。
    public static PointF DotCenter(Rectangle icon) { return new PointF(icon.Right, icon.Bottom); }

    // 状态灯，画上去的那种（不呼吸）。平常签上的灯是那一家自己的灯控件；只有侧栏也列着它时
    //（queue.railwaiting），控件留在侧栏，签上这一颗才用画的。颜色跟 GlassLabel 的灯一致：
    //   0 空心灰圈 = 不知道；2 绿 = 在干活；3 灰 = 看过了；4 / 5 黄 = 做完了没看 / 在问你
    public static void Dot(Graphics g, int tone, PointF center, float d)
    {
        SmoothingMode sm = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Color gray = Color.FromArgb(255, 176, 176, 176);
        if (tone == 0)
            using (var pen = new Pen(gray, Math.Max(1.2f, d * 0.16f)))
                g.DrawEllipse(pen, center.X - d / 2f + 0.5f, center.Y - d / 2f + 0.5f, d - 1f, d - 1f);
        else
            using (var br = new SolidBrush(tone == 3 ? gray : tone == 4 || tone == 5 ? Color.FromArgb(255, 249, 171, 0) : Color.FromArgb(255, 52, 168, 83)))
                g.FillEllipse(br, center.X - d / 2f, center.Y - d / 2f, d, d);
        g.SmoothingMode = sm;
    }
}
