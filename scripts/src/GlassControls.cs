// 【拆分】自绘控件：GlassButton / GlassLabel / SitePanel。（从原 AgentHost.cs 原样搬出，逻辑未改）

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

// 代替 Button：自己用 GDI+ 画，alpha 才对。
internal sealed class GlassButton : Control
{
    bool _hot, _down;
    public bool Primary;
    // 关闭键：悬停变红底白字，跟系统一致
    public bool Danger;
    // 次要按钮（工具栏那排）平时不画底和边，悬停才出浅底。
    public bool Quiet;
    // Let the root window advertise its maximize area to Windows Snap Layouts.
    public bool NativeMaximize;

    public void CaptionHover(bool hot)
    {
        if (_hot == hot) return;
        _hot = hot;
        Invalidate();
    }

    protected override void WndProc(ref Message m)
    {
        if (NativeMaximize && m.Msg == Native.WM_NCHITTEST)
        {
            m.Result = (IntPtr)(-1); // HTTRANSPARENT: hit-test the same-thread parent.
            return;
        }
        base.WndProc(ref m);
    }
    public float Radius = 8f;

    public GlassButton()
    {
        Theme.Glassy(this);
        BackColor = Color.Transparent;
        TabStop = false;
        Cursor = Cursors.Hand;
    }

    protected override void OnMouseEnter(EventArgs e) { _hot = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hot = false; _down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { _down = true; Invalidate(); } base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }
    protected override void OnPaintBackground(PaintEventArgs e) { Theme.PaintUnder(this, e); }

    // 画失败不往外抛（WinForms 会把控件永久画成红叉），见 PaintGuard
    protected override void OnPaint(PaintEventArgs e)
    {
        try { PaintBody(e); }
        catch (Exception ex) { PaintGuard.Failed(this, ex); }
    }

    void PaintBody(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        RectangleF r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        Color fill = Primary
            ? (_down ? Theme.Brand : (_hot ? Theme.BrandHot : Theme.Brand))
            : (!Enabled ? Theme.Btn : (_down ? Theme.BtnDown : (_hot ? Theme.BtnHot : Theme.Btn)));
        bool bare = Quiet && !_hot && !_down;
        if (Danger && (_hot || _down))
        {
            using (var br = new SolidBrush(Color.FromArgb(255, 196, 43, 28)))
                g.FillRectangle(br, 0, 0, Width, Height);
            Theme.Text(g, L.T(Text), Font, Color.White, new RectangleF(0, 0, Width, Height), StringAlignment.Center);
            return;
        }
        using (GraphicsPath gp = Theme.Round(r, Radius))
        using (var br = new SolidBrush(Quiet ? Color.FromArgb(_down ? 22 : 12, Theme.Light ? Color.Black : Color.White) : fill))
        using (var pen = new Pen(Primary ? Theme.Brand : Theme.BtnLine))
        {
            if (!bare && br.Color.A > 0) g.FillPath(br, gp);
            if (!Quiet && pen.Color.A > 0) g.DrawPath(pen, gp);
        }
        Color ink = Primary ? Theme.OnBrand : (Enabled ? Theme.Ink : Theme.Dim);
        Theme.Text(g, L.T(Text), Font, ink, new RectangleF(0, 0, Width, Height), StringAlignment.Center);
    }
}

// 代替 Label。AutoSize 时按文字宽度撑开，外面靠 Right 排版。
internal sealed class GlassLabel : Control
{
    bool _auto;
    // 胶囊：状态小标签。正常只点一个绿点，异常和「在等你」才出文字。
    public bool Chip;
    public int PadX;
    // 0=不知道（空心灰圈） 2=在跑（绿，慢呼吸） 3=没在跑（灰） 4=做完了你还没看（黄）
    // 5=在问你 / 等你批准（黄，快呼吸）
    // 用户 2026-09-20 定：表头只点一个点，不写字；文字留给嵌入本身的毛病。
    // 2026-09-26 用户要呼吸灯：在干活的慢慢一明一暗，在问你的呼吸快一点、更抢眼。
    int _tone;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int Tone
    {
        get { return _tone; }
        set
        {
            if (_tone == value) return;
            _tone = value;
            if (Breathes(value)) Breath.Add(this); else Breath.Remove(this);
        }
    }
    // 只点一个点：宽度也得跟着缩成一个点（以前只改了画法没重量宽度，留着上一段状态文字的宽度 ——
    // 英文状态字长，侧栏卡片的名字被挤成「Open…」）
    bool _dotOnly;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool DotOnly
    {
        get { return _dotOnly; }
        set { if (_dotOnly == value) return; _dotOnly = value; Fit(); Invalidate(); }
    }

    static bool Breathes(int tone) { return tone == 2 || tone == 5; }

    // 所有呼吸的灯共用一个计时器，没有灯要呼吸就停掉。
    static class Breath
    {
        static readonly System.Collections.Generic.HashSet<GlassLabel> _on = new System.Collections.Generic.HashSet<GlassLabel>();
        static Timer _timer;

        public static void Add(GlassLabel l)
        {
            _on.Add(l);
            if (_timer == null)
            {
                _timer = new Timer { Interval = 50 };
                _timer.Tick += delegate
                {
                    foreach (GlassLabel x in new System.Collections.Generic.List<GlassLabel>(_on))
                    {
                        if (x.IsDisposed) { _on.Remove(x); continue; }
                        if (x.Visible && x.DotOnly && x.IsHandleCreated) x.Invalidate();
                    }
                    if (_on.Count == 0) _timer.Stop();
                };
            }
            if (!_timer.Enabled) _timer.Start();
        }

        public static void Remove(GlassLabel l)
        {
            _on.Remove(l);
            if (_on.Count == 0 && _timer != null) _timer.Stop();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) Breath.Remove(this);
        base.Dispose(disposing);
    }

    public GlassLabel()
    {
        Theme.Glassy(this);
        BackColor = Color.Transparent;
        TabStop = false;
    }

    public override bool AutoSize
    {
        get { return _auto; }
        set { _auto = value; Fit(); }
    }

    void Fit()
    {
        if (!_auto) return;
        if (DotOnly) { Size = new Size(Math.Max(10, Height / 2), Height); return; }
        using (Graphics g = CreateGraphics())
        {
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            SizeF sz = g.MeasureString(L.T(Text ?? ""), Font);
            Size = new Size((int)Math.Ceiling(sz.Width) + 4 + PadX * 2, Math.Max(Height, (int)Math.Ceiling(sz.Height)));
        }
    }

    protected override void OnTextChanged(EventArgs e) { Fit(); Invalidate(); base.OnTextChanged(e); }
    public void Refit() { Fit(); Invalidate(); }   // 切了界面语言：显示的字变了，Text 没变
    protected override void OnFontChanged(EventArgs e) { Fit(); Invalidate(); base.OnFontChanged(e); }
    protected override void OnForeColorChanged(EventArgs e) { Invalidate(); base.OnForeColorChanged(e); }
    protected override void OnPaintBackground(PaintEventArgs e) { Theme.PaintUnder(this, e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        try { PaintBody(e); }
        catch (Exception ex) { PaintGuard.Failed(this, ex); }
    }

    void PaintBody(PaintEventArgs e)
    {
        if (!Chip)
        {
            Theme.Text(e.Graphics, L.T(Text), Font, ForeColor, new RectangleF(0, 0, Width, Height), StringAlignment.Near);
            return;
        }
        bool ok = Text == "已嵌入" || DotOnly;
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        if (ok)
        {
            // 只点一个点，文字仍在 Text 里（读屏/调试照样能看到）：
            //   2 绿 = 在干活；4 黄 = 做完了 / 在问你、你还没看；3 实心灰 = 闲着 / 看过了；
            //   0 空心灰圈 = 不知道（钩子还没上报）。以前 0 落到默认分支画成绿色，
            //   「不知道」看着就是「在干活」（用户 2026-09-25：三个明明没在干活却亮绿灯）。
            float d = Height * 0.34f;
            float y = (Height - d) / 2f;
            if (Tone == 0)
            {
                using (var pen = new Pen(Color.FromArgb(255, 176, 176, 176), Math.Max(1.2f, d * 0.16f)))
                    g.DrawEllipse(pen, Width / 2f - d / 2f + 0.5f, y + 0.5f, d - 1f, d - 1f);
                return;
            }
            Color dot = Tone == 3 ? Color.FromArgb(255, 176, 176, 176)
                      : Tone == 4 || Tone == 5 ? Color.FromArgb(255, 249, 171, 0)
                      : Color.FromArgb(255, 52, 168, 83);
            float cx = Width / 2f, cy = y + d / 2f;
            if (Breathes(Tone))
            {
                // b：0→1→0 的正弦呼吸。点本身 55%~100% 亮度、略缩放；外面一圈光晕随呼气扩散变淡。
                int period = Tone == 5 ? 1000 : 2000;
                double ph = (Environment.TickCount & int.MaxValue) % period / (double)period;
                float b = (float)(0.5 - 0.5 * Math.Cos(ph * 2 * Math.PI));
                float haloR = d / 2f * (1f + 0.9f * b);
                float maxR = Math.Min(Width, Height) / 2f - 0.5f;
                if (haloR > maxR) haloR = maxR;
                using (var hb = new SolidBrush(Color.FromArgb((int)(70 * (1f - b) + 20), dot)))
                    g.FillEllipse(hb, cx - haloR, cy - haloR, haloR * 2, haloR * 2);
                float r = d / 2f * (0.88f + 0.12f * b);
                using (var br = new SolidBrush(Color.FromArgb((int)(140 + 115 * b), dot)))
                    g.FillEllipse(br, cx - r, cy - r, r * 2, r * 2);
                return;
            }
            using (var br = new SolidBrush(dot))
                g.FillEllipse(br, cx - d / 2f, y, d, d);
            return;
        }
        using (GraphicsPath gp = Theme.Round(new RectangleF(0, 0, Width - 1, Height - 1), (Height - 1) / 2f))
        using (var br = new SolidBrush(ok ? Theme.OkBg : Theme.ChipBg))
            g.FillPath(br, gp);
        Theme.Text(g, L.T(Text), Font, ok ? Theme.OkInk : Theme.Dim, new RectangleF(0, 0, Width, Height), StringAlignment.Center);
    }
}

internal sealed class SitePanel : Panel
{
    public Slot OwnerSlot;

    // 格子底透明：Codex 这类自己带半透明侧栏的客户端，透明处就能透出宿主的 Mica，
    // 不再是黑的（以前底色是深灰，侧栏叠上去发黑）。
    public SitePanel()
    {
        Theme.Glassy(this);
        BackColor = Color.Transparent;
    }

    protected override void OnPaintBackground(PaintEventArgs e) { Theme.PaintUnder(this, e); }

    // 刷底时绕开里面嵌的客户端窗口。不设的话面板每重画一次（心跳里改表头、状态灯都会连带），
    // PaintUnder 画的卡片灰底（243）就直接刷在客户端上面，等它自己重画才盖回来 ——
    // 2026-09-26 高帧率录到 Codex 白底一闪变灰 0~23ms，45 秒 29 次（用户：Codex 在里面经常闪）。
    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.Style |= 0x02000000;   // WS_CLIPCHILDREN
            return cp;
        }
    }

    // 面板 HWND 一销毁，还挂在下面的 Electron 子窗口会被系统连带毁掉。
    // 关宿主、移出、布局重建都可能走到这里，最后一道保险是先还成顶层。
    protected override void DestroyHandle()
    {
        try
        {
            if (OwnerSlot != null && OwnerSlot.Hwnd != IntPtr.Zero && Native.IsWindow(OwnerSlot.Hwnd))
                Embed.RestoreTopLevel(OwnerSlot.Hwnd);
        }
        catch { }
        base.DestroyHandle();
    }
}
