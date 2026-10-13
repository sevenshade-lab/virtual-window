// 【拆分】菜单 / 对话框的统一样式。（从原 AgentHost.cs 原样搬出，逻辑未改）

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

// Shared shell palette for menus and owned dialogs. Client windows keep their
// own themes; rendering here never alters their HWND styles or input handling.
internal static class ShellStyles
{
    // Google 的菜单 / 弹层：浅色白底 + #E9EEF6 悬停 + #DADCE0 边；深色 #282A2C + #333537 悬停 + #444746 边
    // 经典橙沿用原来的暖白 / 橙色悬停
    public static Color Surface { get { return Theme.Classic ? (Theme.Light ? Color.FromArgb(252, 251, 250) : Color.FromArgb(36, 36, 36)) : (Theme.Light ? Color.FromArgb(255, 255, 255) : Color.FromArgb(40, 42, 44)); } }
    public static Color Hover { get { return Theme.Classic ? (Theme.Light ? Color.FromArgb(249, 233, 225) : Color.FromArgb(52, 52, 52)) : (Theme.Light ? Color.FromArgb(233, 238, 246) : Color.FromArgb(51, 53, 55)); } }
    public static Color Border { get { return Theme.Classic ? (Theme.Light ? Color.FromArgb(226, 221, 217) : Color.FromArgb(69, 69, 69)) : (Theme.Light ? Color.FromArgb(218, 220, 224) : Color.FromArgb(68, 71, 70)); } }
    public static void Menu(ToolStripDropDown menu, Font font, float scale)
    {
        menu.Font = font;
        menu.BackColor = Surface;
        menu.ForeColor = Theme.Ink;
        menu.Renderer = new ShellMenuRenderer(scale);
        menu.Padding = new Padding(5, (int)(6 * scale), 5, (int)(6 * scale));
        menu.ShowItemToolTips = true;
        var strip = menu as ToolStripDropDownMenu;
        if (strip != null) { strip.ShowImageMargin = false; strip.ShowCheckMargin = true; }
        EventHandler shape = delegate
        {
            if (menu.Width < 2 || menu.Height < 2) return;
            Region previous = menu.Region;
            using (GraphicsPath path = Theme.Round(new RectangleF(0, 0, menu.Width, menu.Height), 9 * scale))
                menu.Region = new Region(path);
            if (previous != null) previous.Dispose();
        };
        menu.SizeChanged += shape;
        menu.Opened += shape;
        foreach (ToolStripItem item in menu.Items)
        {
            item.ForeColor = Theme.Ink;
            item.Padding = new Padding((int)(7 * scale), (int)(5 * scale), (int)(10 * scale), (int)(5 * scale));
            var child = item as ToolStripMenuItem;
            if (child != null && child.HasDropDownItems) Menu(child.DropDown, font, scale);
        }
    }
}

internal sealed class ShellMenuRenderer : ToolStripProfessionalRenderer
{
    readonly float scale;
    public ShellMenuRenderer(float value) { scale = value; RoundedEdges = false; }
    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    { e.Graphics.Clear(ShellStyles.Surface); }
    protected override void OnRenderImageMargin(ToolStripRenderEventArgs e) { }
    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using (var path = Theme.Round(new RectangleF(.5f, .5f, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1), 9 * scale))
        using (var pen = new Pen(ShellStyles.Border)) e.Graphics.DrawPath(pen, path);
    }
    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Enabled || (!e.Item.Selected && !e.Item.Pressed)) return;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using (var path = Theme.Round(new RectangleF(3, 1, e.Item.Width - 6, e.Item.Height - 2), 6 * scale))
        using (var brush = new SolidBrush(ShellStyles.Hover)) e.Graphics.FillPath(brush, path);
    }
    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? Theme.Ink : Theme.Dim;
        base.OnRenderItemText(e);
    }
    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = e.Item.Enabled ? Theme.Ink : Theme.Dim;
        base.OnRenderArrow(e);
    }
    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        using (var pen = new Pen(ShellStyles.Border))
            e.Graphics.DrawLine(pen, (int)(12 * scale), e.Item.Height / 2, e.Item.Width - (int)(12 * scale), e.Item.Height / 2);
    }
    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        Rectangle r = e.ImageRectangle;
        int size = Math.Min((int)(14 * scale), Math.Min(r.Width, r.Height));
        r = new Rectangle(r.X + (r.Width - size) / 2, r.Y + (r.Height - size) / 2, size, size);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using (var path = Theme.Round(r, 3 * scale))
        using (var brush = new SolidBrush(Theme.Brand)) e.Graphics.FillPath(brush, path);
        using (var pen = new Pen(Theme.OnBrand, Math.Max(1.5f, 1.5f * scale)))
            e.Graphics.DrawLines(pen, new PointF[] {
                new PointF(r.X + size * .23f, r.Y + size * .52f),
                new PointF(r.X + size * .43f, r.Y + size * .72f),
                new PointF(r.X + size * .78f, r.Y + size * .3f) });
    }
}

// Button retains native keyboard/accessibility behavior (AcceptButton/Escape),
// with the same colors and rounded geometry as the host toolbar.
internal sealed class ShellDialogButton : Button
{
    public bool Primary;
    bool hot, down;
    public ShellDialogButton()
    { FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0; Cursor = Cursors.Hand; }
    protected override void OnMouseEnter(EventArgs e) { hot = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hot = down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { down = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnPaint(PaintEventArgs e)
    {
        try { PaintBody(e); }
        catch (Exception ex) { PaintGuard.Failed(this, ex); }
    }

    void PaintBody(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent == null ? ShellStyles.Surface : Parent.BackColor);
        float scale = e.Graphics.DpiX / 96f;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using (var path = Theme.Round(new RectangleF(1, 1, Width - 2, Height - 2), 8 * scale))
        using (var brush = new SolidBrush(Primary ? (hot ? Theme.BrandHot : Theme.Brand) : (hot || down ? ShellStyles.Hover : ShellStyles.Surface)))
        using (var pen = new Pen(Focused ? Theme.Brand : ShellStyles.Border, Focused ? 2 : 1))
        { e.Graphics.FillPath(brush, path); if (!Primary || Focused) e.Graphics.DrawPath(pen, path); }
        TextRenderer.DrawText(e.Graphics, L.T(Text), Font, ClientRectangle, Primary ? Theme.OnBrand : Theme.Ink,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }
}
