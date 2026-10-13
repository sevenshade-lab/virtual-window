// 设置页（替换原来那一长串「设置」菜单）：跟「＋ 新会话」同一种弹出面板。
//   外观：浅色 / 深色 / 跟随系统，三段选择；
//   行为：四个开关（悬停切换焦点、空格组合键、完成时闪任务栏、关闭时一并关桌面端），每个带一行说明；
//   状态灯：各家的状态钩子通没通 —— 装了没、最近一次收到是什么时候、没通的话卡在哪一步（Codex 要信任、OpenCode 要重启）；
//           「重新安装钩子」一键重装；
//   底部：Agent 名单、添加其他窗口、组合、日志。后两个是一串选项，点了关面板、在原处弹小菜单。
// 失去焦点就关；Esc 关闭。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;

internal sealed partial class HostForm
{
    SettingsPanel _settingsPanel;

    void ShowSettings(object sender, EventArgs e)
    {
        if (_settingsPanel != null && !_settingsPanel.IsDisposed) { _settingsPanel.Close(); return; }
        _menuOpen = true;
        IntPtr giveBack = _lastFocused;
        HookStates.Refresh();
        var p = new SettingsPanel(this);
        _settingsPanel = p;
        p.FormClosed += delegate
        {
            _menuOpen = false;
            _settingsPanel = null;
            MethodInvoker act = p.Action;   // 失去焦点和点击都会关，FormClosed 可能来两遍：动作只取一次
            p.Action = null;
            if (act != null) BeginInvoke(act);
            else if (giveBack != IntPtr.Zero && Native.IsWindow(giveBack) && SlotWithHwnd(giveBack) != null)
                BeginInvoke((MethodInvoker)delegate { if (!_menuOpen) GiveFocus(giveBack, true); });
        };
        p.Location = _settings.PointToScreen(new Point(0, _settings.Height + D(6)));
        p.Show(this);
        p.Activate();
    }

    // 底部「添加其他窗口」「组合」：面板关掉后在「设置」按钮下面弹原来那份小菜单
    void ShowToolsMenu(string which)
    {
        var menu = new ContextMenuStrip();
        if (which == "adopt")
        {
            ToolStripMenuItem adopt = BuildAdoptMenu();
            if (!adopt.Enabled) { Toast(adopt.Text); return; }
            var items = new ToolStripItem[adopt.DropDownItems.Count];
            adopt.DropDownItems.CopyTo(items, 0);
            menu.Items.AddRange(items);
        }
        else AddProfileMenu(menu);
        _menuOpen = true;
        IntPtr giveBack = _lastFocused;
        try { Native.SetForegroundWindow(Handle); } catch { }
        Native.SetFocus(Handle);
        menu.Closed += delegate
        {
            _menuOpen = false;
            if (giveBack != IntPtr.Zero && Native.IsWindow(giveBack) && SlotWithHwnd(giveBack) != null)
                BeginInvoke((MethodInvoker)delegate { if (!_menuOpen) GiveFocus(giveBack, true); });
        };
        ShellStyles.Menu(menu, Font, _scale);
        menu.Show(_settings, new Point(0, _settings.Height));
    }

    void OpenLog()
    {
        try
        {
            string f = Log.Path_();
            if (f != null && File.Exists(f)) Process.Start(new ProcessStartInfo(f) { UseShellExecute = true });
            else Toast(L.T("还没有日志"));
        }
        catch { Toast(L.T("打不开日志")); }
    }

    internal sealed class SettingsPanel : Form
    {
        readonly HostForm _host;
        public MethodInvoker Action;

        sealed class Item { public Rectangle R; public string What; public string Arg; }
        readonly List<Item> _items = new List<Item>();
        Item _hot;
        string _hookNote;   // 重装之后的一行结果

        // 开关：键、默认值、标题、说明
        static readonly string[][] Toggles =
        {
            // 第 0 个画在「外观」那一节，其余在「行为」
            new[] { "theme.syncwindows", "1", "连 Windows 一起切", "跟个性化里选深色 / 浅色一样：各家 agent、任务栏、资源管理器一起变" },
            new[] { "hoverfocus", "1", "悬停切换焦点", "鼠标在格子上停 0.25 秒，键盘焦点就跟过去" },
            new[] { "spaceswitch", "0", "空格组合键切换", "按住空格再按 QWE / ASD 跳格；单独轻敲还是空格" },
            new[] { "notify", "1", "做完时闪烁任务栏", "某一家做完或在问你、而你不在虚拟窗口里时提醒一下" },
            new[] { "openagents", "1", "打开时一并启动桌面端", "打开虚拟窗口时把上次那几家没开着的桌面端一起启动" },
            new[] { "closeagents", "1", "关闭时一并关闭桌面端", "关掉虚拟窗口时把嵌着的桌面端一起关；终端会话留成独立窗口" },
        };

        // 状态灯那一节列哪几家（有钩子的）
        static readonly string[] HookKinds = { "claude", "codex", "cursor", "opencode", "antigravity" };

        int D(int v) { return _host.D(v); }
        int Pad { get { return D(16); } }
        int W { get { return D(440); } }

        public SettingsPanel(HostForm host)
        {
            _host = host;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            KeyPreview = true;
            DoubleBuffered = true;
            BackColor = ShellStyles.Surface;
            Font = host.Font;
            Relayout();
        }

        protected override CreateParams CreateParams
        {
            get { CreateParams cp = base.CreateParams; cp.ClassStyle |= 0x00020000; /* CS_DROPSHADOW */ return cp; }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            int round = 2;
            try { Native.DwmSetInt(Handle, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref round, 4); } catch { }
        }

        // 宿主跟嵌进来的客户端共用输入队列：面板成了前台窗口，键盘焦点却可能还留在那一格里
        //（宿主在同一刻把焦点还了回去），Esc 就按进了客户端（2026-09-26 实测）。激活时把焦点收回自己
        protected override void OnActivated(EventArgs e)
        {
            base.OnActivated(e);
            Native.SetFocus(Handle);
        }

        bool _closing, _busy;
        protected override void OnFormClosing(FormClosingEventArgs e) { _closing = true; base.OnFormClosing(e); }
        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            if (!_busy && !_closing && !IsDisposed) Close();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape) { _escDown = true; e.Handled = true; Close(); return; }
            base.OnKeyDown(e);
        }

        // 嵌入的客户端跟宿主共用输入队列，Esc 的「按下」有时根本到不了面板（只到了「松开」，2026-09-26 实测），
        // 所以松开时也认；按下已经处理过的就别再来一遍
        bool _escDown;
        protected override void OnKeyUp(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape)
            {
                bool seen = _escDown;
                _escDown = false;
                if (!seen && !_closing) { e.Handled = true; Close(); return; }
            }
            base.OnKeyUp(e);
        }

        // 每节标题的位置（画的时候用）
        readonly List<KeyValuePair<int, string>> _sections = new List<KeyValuePair<int, string>>();

        void Relayout()
        {
            _items.Clear();
            _sections.Clear();
            _hot = null;
            int inner = W - Pad * 2;
            int y = D(46);

            _sections.Add(new KeyValuePair<int, string>(y, "外观"));
            y += D(26);
            int seg = inner / 3;
            for (int i = 0; i < ThemeIds.Length; i++)
                _items.Add(new Item { R = new Rectangle(Pad + i * seg, y, i == 2 ? inner - 2 * seg : seg, D(34)), What = "theme", Arg = ThemeIds[i] });
            y += D(34) + D(8);
            int half = inner / 2;
            for (int i = 0; i < PaletteIds.Length; i++)
                _items.Add(new Item { R = new Rectangle(Pad + i * half, y, i == 1 ? inner - half : half, D(34)), What = "palette", Arg = PaletteIds[i] });
            y += D(34) + D(6);
            _items.Add(new Item { R = new Rectangle(Pad - D(6), y, inner + D(12), D(50)), What = "toggle", Arg = Toggles[0][0] });
            y += D(50) + D(12);

            _sections.Add(new KeyValuePair<int, string>(y, "行为"));
            y += D(26);
            for (int i = 1; i < Toggles.Length; i++)
            {
                _items.Add(new Item { R = new Rectangle(Pad - D(6), y, inner + D(12), D(50)), What = "toggle", Arg = Toggles[i][0] });
                y += D(52);
            }
            y += D(12);

            _sections.Add(new KeyValuePair<int, string>(y, "状态灯"));
            y += D(26);
            foreach (string k in HookKinds)
            {
                if (HookSetup.Installed(k) == null) continue;
                _items.Add(new Item { R = new Rectangle(Pad, y, inner, D(30)), What = "hook", Arg = k });
                y += D(30);
            }
            y += D(4);
            _items.Add(new Item { R = new Rectangle(Pad - D(6), y, D(150), D(32)), What = "reinstall" });
            y += D(32);
            if (_hookNote != null) y += D(22);
            y += D(14);

            // 底部一排入口
            string[][] foot = { new[] { "roster", "Agent 名单" }, new[] { "adopt", "添加其他窗口" }, new[] { "profile", "组合" }, new[] { "log", "日志" } };
            int fw = inner / foot.Length;
            for (int i = 0; i < foot.Length; i++)
                _items.Add(new Item { R = new Rectangle(Pad + i * fw, y + D(8), fw, D(34)), What = foot[i][0], Arg = foot[i][1] });
            y += D(8) + D(34) + D(12);

            ClientSize = new Size(W, y);
            Rectangle work = Screen.FromPoint(Location).WorkingArea;
            if (Bottom > work.Bottom) Top = Math.Max(work.Top, work.Bottom - Height);
            if (Right > work.Right) Left = Math.Max(work.Left, work.Right - Width);
            Invalidate();
        }

        Item Hit(Point p)
        {
            foreach (Item it in _items) if (it.R.Contains(p) && it.What != "hook") return it;
            return null;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            Item h = Hit(e.Location);
            if (h != _hot) { _hot = h; Cursor = h != null ? Cursors.Hand : Cursors.Default; Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e) { if (_hot != null) { _hot = null; Invalidate(); } base.OnMouseLeave(e); }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left) return;
            Item it = Hit(e.Location);
            if (it == null) return;
            HostForm host = _host;
            switch (it.What)
            {
                case "theme":
                    _busy = true;   // 换色时宿主盖一层淡出，别让它把面板的焦点抢走就关了
                    try { host.SetThemePreference(it.Arg); }
                    finally { _busy = false; }
                    BackColor = ShellStyles.Surface;
                    Activate();
                    Native.SetFocus(Handle);
                    Invalidate();
                    break;
                case "palette":
                    _busy = true;
                    try { host.SetPalette(it.Arg); }
                    finally { _busy = false; }
                    BackColor = ShellStyles.Surface;
                    Activate();
                    Native.SetFocus(Handle);
                    Invalidate();
                    break;
                case "toggle":
                    {
                        string[] t = Array.Find(Toggles, x => x[0] == it.Arg);
                        bool on = !Prefs.Get(t[0], t[1] == "1");
                        Prefs.Set(t[0], on);
                        if (t[0] == "spaceswitch") host.SyncSpaceHook();
                        if (t[0] == "theme.syncwindows" && on) host.SyncWindowsAppsTheme();
                        Invalidate();
                    }
                    break;
                case "reinstall":
                    {
                        Cursor = Cursors.WaitCursor;
                        List<string> rep = HookSetup.ApplyToFile(true);
                        Cursor = Cursors.Default;
                        string bad = rep.Find(l => l.Contains("失败") || l.Contains("没找到") || l.Contains("不敢动"));
                        _hookNote = bad == null ? L.T("已重装。新装的要重启那一家才生效；Codex 改过命令要重新信任") : (L.En ? L.T("有钩子没装上，详情见 hooks-report.txt") : bad);
                        HookStates.Refresh();
                        Relayout();
                    }
                    break;
                case "roster":
                    Action = delegate { host.ShowRoster(); };
                    Close();
                    break;
                case "adopt":
                case "profile":
                    {
                        string which = it.What;
                        Action = delegate { host.ShowToolsMenu(which); };
                        Close();
                    }
                    break;
                case "log":
                    Action = delegate { host.OpenLog(); };
                    Close();
                    break;
            }
        }

        // 钩子这一行的状态：灯的颜色 + 一句话
        static void HookStatus(string kind, out Color dot, out string text)
        {
            bool installed = HookSetup.Installed(kind) == true;
            long at = HookStates.LastAt(kind);
            if (!installed)
            {
                dot = Color.Empty;
                text = L.T("没装钩子（点下面「重新安装钩子」）");
                return;
            }
            if (at <= 0)
            {
                dot = Color.FromArgb(255, 249, 171, 0);
                text = L.T(kind == "codex" ? "已装，还要在 Codex 的钩子管理页信任一次"
                     : kind == "opencode" ? "已装，重启 OpenCode 后生效"
                     : kind == "cursor" ? "已装，重启 Cursor 后生效"
                     : "已装，还没收到过消息");
                return;
            }
            dot = Color.FromArgb(255, 52, 168, 83);
            text = L.F("正常 · 最近一次 {0}", Ago(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - at));
        }

        static string Ago(long ms)
        {
            long s = Math.Max(0, ms / 1000);
            if (s < 60) return L.T("刚刚");
            if (s < 3600) return L.F("{0} 分钟前", s / 60);
            if (s < 86400) return L.F("{0} 小时前", s / 3600);
            return L.F("{0} 天前", s / 86400);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            try { PaintBody(e); }
            catch (Exception ex) { PaintGuard.Failed(this, ex); }
        }

        void PaintBody(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            using (var pen = new Pen(ShellStyles.Border))
                g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
            using (var title = Fonts.Medium(Font.Size * 1.08f))
            using (var small = new Font(Font.FontFamily, Font.Size * 0.86f))
            {
                Theme.Text(g, L.T("设置"), title, Theme.Ink, new RectangleF(Pad, 0, Width - Pad * 2, D(44)), StringAlignment.Near);
                foreach (var sec in _sections)
                    Theme.Text(g, L.T(sec.Value), small, Theme.Dim, new RectangleF(Pad, sec.Key, Width - Pad * 2, D(22)), StringAlignment.Near);

                // 外观三段：一个整体的底，选中那段是实心块
                foreach (Item first in new[] { _items.Find(i => i.What == "theme"), _items.Find(i => i.What == "palette") })
                {
                    if (first == null) continue;
                    var all = new Rectangle(Pad, first.R.Y, Width - Pad * 2, first.R.Height);
                    using (GraphicsPath gp = Theme.Round(all, D(8)))
                    using (var br = new SolidBrush(Color.FromArgb(Theme.Light ? 10 : 22, Theme.Light ? Color.Black : Color.White)))
                    using (var pen = new Pen(ShellStyles.Border))
                    { g.FillPath(br, gp); g.DrawPath(pen, gp); }
                }

                foreach (Item it in _items)
                {
                    bool hot = it == _hot;
                    switch (it.What)
                    {
                        case "theme":
                            {
                                bool sel = Theme.Preference == it.Arg;
                                Rectangle r = Rectangle.Inflate(it.R, -D(3), -D(3));
                                if (sel || hot)
                                    using (GraphicsPath gp = Theme.Round(r, D(6)))
                                    using (var br = new SolidBrush(sel ? (Theme.Light ? Color.White : Color.FromArgb(70, 70, 70)) : ShellStyles.Hover))
                                    {
                                        g.FillPath(br, gp);
                                        if (sel) using (var pen = new Pen(ShellStyles.Border)) g.DrawPath(pen, gp);
                                    }
                                string label = L.T(ThemeNames[Array.IndexOf(ThemeIds, it.Arg)]);
                                Font icon = IconFont(Font.Size);
                                SizeF ls = g.MeasureString(label, Font);
                                float total = D(16) + D(6) + ls.Width;
                                float x = r.X + (r.Width - total) / 2f;
                                Theme.Text(g, ThemeGlyph(it.Arg), icon, sel ? Theme.Ink : Theme.Dim, new RectangleF(x, r.Y, D(16), r.Height), StringAlignment.Center);
                                Theme.Text(g, label, Font, sel ? Theme.Ink : Theme.Dim, new RectangleF(x + D(22), r.Y, ls.Width + D(4), r.Height), StringAlignment.Near);
                            }
                            break;
                        case "palette":
                            {
                                bool sel = Theme.Palette == it.Arg;
                                Rectangle r = Rectangle.Inflate(it.R, -D(3), -D(3));
                                if (sel || hot)
                                    using (GraphicsPath gp = Theme.Round(r, D(6)))
                                    using (var br = new SolidBrush(sel ? (Theme.Light ? Color.White : Color.FromArgb(70, 70, 70)) : ShellStyles.Hover))
                                    {
                                        g.FillPath(br, gp);
                                        if (sel) using (var pen = new Pen(ShellStyles.Border)) g.DrawPath(pen, gp);
                                    }
                                string label = L.T(PaletteNames[Array.IndexOf(PaletteIds, it.Arg)]);
                                Color swatch = it.Arg == "classic" ? Color.FromArgb(255, 201, 100, 66)
                                             : (Theme.Light ? Color.FromArgb(255, 11, 87, 208) : Color.FromArgb(255, 168, 199, 250));
                                SizeF ls = g.MeasureString(label, Font);
                                float dd = D(10), total = dd + D(8) + ls.Width;
                                float x = r.X + (r.Width - total) / 2f;
                                using (var br = new SolidBrush(swatch)) g.FillEllipse(br, x, r.Y + (r.Height - dd) / 2f, dd, dd);
                                Theme.Text(g, label, Font, sel ? Theme.Ink : Theme.Dim, new RectangleF(x + dd + D(8), r.Y, ls.Width + D(4), r.Height), StringAlignment.Near);
                            }
                            break;
                        case "toggle":
                            {
                                string[] t = Array.Find(Toggles, x => x[0] == it.Arg);
                                bool on = Prefs.Get(t[0], t[1] == "1");
                                if (hot)
                                    using (GraphicsPath gp = Theme.Round(it.R, D(8)))
                                    using (var br = new SolidBrush(ShellStyles.Hover))
                                        g.FillPath(br, gp);
                                int tx = it.R.X + D(6), tw = it.R.Width - D(6) - D(60);
                                Theme.Text(g, L.T(t[2]), Font, Theme.Ink, new RectangleF(tx, it.R.Y + D(5), tw, D(22)), StringAlignment.Near);
                                Theme.Text(g, L.T(t[3]), small, Theme.Dim, new RectangleF(tx, it.R.Y + D(26), tw, D(20)), StringAlignment.Near);
                                Switch(g, new Rectangle(it.R.Right - D(6) - D(40), it.R.Y + (it.R.Height - D(22)) / 2, D(40), D(22)), on);
                            }
                            break;
                        case "hook":
                            {
                                AgentKind k = Catalog.ById(it.Arg);
                                int icon = D(18);
                                var ir = new Rectangle(it.R.X, it.R.Y + (it.R.Height - icon) / 2, icon, icon);
                                if (k != null)
                                {
                                    Theme.Icon(g, AgentIcons.Get(k, icon), ir, D(3), D(5));
                                }
                                Theme.Text(g, k != null ? k.Name : it.Arg, Font, Theme.Ink, new RectangleF(ir.Right + D(10), it.R.Y, D(110), it.R.Height), StringAlignment.Near);
                                Color dot; string text;
                                HookStatus(it.Arg, out dot, out text);
                                SizeF ts = g.MeasureString(text, small);
                                float tx = it.R.Right - ts.Width;
                                float dx = tx - D(12), d = D(7), dy = it.R.Y + (it.R.Height - d) / 2f;
                                if (dot.IsEmpty)
                                    using (var pen = new Pen(Theme.Dim, Math.Max(1f, D(1)))) g.DrawEllipse(pen, dx, dy, d, d);
                                else
                                    using (var br = new SolidBrush(dot)) g.FillEllipse(br, dx, dy, d, d);
                                Theme.Text(g, text, small, Theme.Dim, new RectangleF(tx, it.R.Y, ts.Width + D(2), it.R.Height), StringAlignment.Near);
                            }
                            break;
                        case "reinstall":
                            if (hot)
                                using (GraphicsPath gp = Theme.Round(it.R, D(8)))
                                using (var br = new SolidBrush(ShellStyles.Hover))
                                    g.FillPath(br, gp);
                            Theme.Text(g, L.T("↻ 重新安装钩子"), Font, Theme.Brand, new RectangleF(it.R.X + D(6), it.R.Y, it.R.Width - D(6), it.R.Height), StringAlignment.Near);
                            if (_hookNote != null)
                                Theme.Text(g, _hookNote, small, Theme.Dim, new RectangleF(Pad, it.R.Bottom, Width - Pad * 2, D(22)), StringAlignment.Near);
                            break;
                        default:   // 底部入口
                            if (it.What == "roster")
                                using (var pen = new Pen(ShellStyles.Border))
                                    g.DrawLine(pen, Pad, it.R.Y - D(8), Width - Pad, it.R.Y - D(8));
                            if (hot)
                                using (GraphicsPath gp = Theme.Round(Rectangle.Inflate(it.R, -D(2), 0), D(8)))
                                using (var br = new SolidBrush(ShellStyles.Hover))
                                    g.FillPath(br, gp);
                            string arrow = it.What == "adopt" || it.What == "profile" ? " ▾" : "";
                            Theme.Text(g, L.T(it.Arg) + arrow, Font, hot ? Theme.Ink : Theme.Dim, it.R, StringAlignment.Center);
                            break;
                    }
                }
            }
        }

        void Switch(Graphics g, Rectangle r, bool on)
        {
            using (GraphicsPath gp = Theme.Round(r, r.Height / 2f))
            {
                if (on)
                    using (var br = new SolidBrush(Theme.Brand)) g.FillPath(br, gp);
                else
                    using (var br = new SolidBrush(Color.FromArgb(Theme.Light ? 18 : 40, Theme.Light ? Color.Black : Color.White)))
                    using (var pen = new Pen(ShellStyles.Border))
                    { g.FillPath(br, gp); g.DrawPath(pen, gp); }
            }
            int k = r.Height - D(6);
            int kx = on ? r.Right - D(3) - k : r.X + D(3);
            using (var br = new SolidBrush(on ? Theme.OnBrand : (Theme.Light ? Color.FromArgb(120, 120, 120) : Color.FromArgb(170, 170, 170))))
                g.FillEllipse(br, kx, r.Y + D(3), k, k);
        }
    }
}
