// 「Agent 名单」页（参考 CC Switch 的本地环境检查）：每家一张卡片，写着桌面端 / CLI 装没装、
// 状态从哪来，能直接启动、新建 CLI 会话、去官网装；底部可以加自定义 agent，保存后立刻进名单。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

internal sealed partial class HostForm
{
    void ShowRoster()
    {
        _menuOpen = true;
        try
        {
            AgentKind pickDesk = null, pickCli = null;
            using (var dlg = new Form())
            {
                dlg.Text = L.T("Agent 名单");
                dlg.Font = Font;
                dlg.AutoScaleMode = AutoScaleMode.None;
                dlg.ClientSize = new Size(D(860), D(660));
                dlg.MinimumSize = new Size(D(640), D(480));
                dlg.BackColor = ShellStyles.Surface;
                dlg.ForeColor = Theme.Ink;
                dlg.StartPosition = FormStartPosition.CenterParent;
                dlg.MinimizeBox = false;
                dlg.ShowIcon = false;
                dlg.ShowInTaskbar = false;

                var head = new Label { AutoSize = false, ForeColor = Theme.Dim };
                var add = new ShellDialogButton { Text = "＋ 添加自定义 agent", Primary = true };
                var list = new Panel { AutoScroll = true, BackColor = ShellStyles.Surface };
                dlg.Controls.AddRange(new Control[] { head, add, list });

                Action rebuild = null;
                rebuild = delegate
                {
                    list.SuspendLayout();
                    foreach (Control c in list.Controls) c.Dispose();
                    list.Controls.Clear();
                    int preset = 0, installed = 0, custom = 0;
                    foreach (AgentKind k in Catalog.All)
                    {
                        if (k.Custom) custom++; else preset++;
                        if (Catalog.HasDesktop(k) || Catalog.HasCli(k)) installed++;
                    }
                    head.Text = L.F("预设 {0} 家 · 已装 {1} 家", preset, installed) + (custom > 0 ? L.F(" · 自定义 {0} 家", custom) : "")
                              + L.T("　　没装的照样列着，装好后自动认出来");
                    // 竖向滚动条的宽度先扣掉，不然卡片宽出来一截、底下多一条横向滚动条
                    int gap = D(14), colW = (list.Width - SystemInformation.VerticalScrollBarWidth - gap * 3) / 2, cardH = D(128);
                    int i = 0;
                    foreach (AgentKind k in Catalog.All)
                    {
                        AgentKind kind = k;
                        var card = new RosterCard(this, kind, colW, cardH);
                        card.Location = new Point(gap + (i % 2) * (colW + gap), gap + (i / 2) * (cardH + gap) + list.AutoScrollPosition.Y);
                        card.StartDesktop += delegate { pickDesk = kind; dlg.DialogResult = DialogResult.OK; };
                        card.StartCli += delegate { pickCli = kind; dlg.DialogResult = DialogResult.OK; };
                        card.Remove += delegate
                        {
                            if (SlotOf(kind) != null) { MessageBox.Show(dlg, L.T("它还在格子里，先移出再删。"), L.T("Agent 名单")); return; }
                            if (MessageBox.Show(dlg, L.F("从名单里删掉「{0}」？（只删名单，不卸载软件）", kind.Name), L.T("Agent 名单"),
                                    MessageBoxButtons.OKCancel) != DialogResult.OK) return;
                            Catalog.RemoveCustom(kind);
                            rebuild();
                        };
                        list.Controls.Add(card);
                        i++;
                    }
                    list.ResumeLayout();
                };

                Action place = delegate
                {
                    int pad = D(20);
                    head.SetBounds(pad, D(16), dlg.ClientSize.Width - pad * 2 - D(200), D(30));
                    head.TextAlign = ContentAlignment.MiddleLeft;
                    add.SetBounds(dlg.ClientSize.Width - pad - D(190), D(14), D(190), D(34));
                    list.SetBounds(D(6), D(60), dlg.ClientSize.Width - D(12), dlg.ClientSize.Height - D(66));
                };
                Catalog.ForgetInstallCache();
                place();
                rebuild();
                dlg.Resize += delegate { place(); rebuild(); };
                add.Click += delegate
                {
                    AgentKind k = AskCustomAgent(dlg);
                    if (k != null) { rebuild(); Toast(L.F("已加入名单：{0}", k.Name)); }
                };

                dlg.ShowDialog(this);
            }
            // 关掉名单页再动手：启动会弹出别的窗口、改格子
            if (pickDesk != null) AddOrLaunch(pickDesk);
            if (pickCli != null) AddCli(pickCli);
        }
        finally { _menuOpen = false; }
    }

    // 添加自定义 agent 的小表单。返回加进名单的那一家（取消返回 null）。
    AgentKind AskCustomAgent(IWin32Window owner)
    {
        using (var dlg = new Form())
        {
            dlg.Text = L.T("添加自定义 agent");
            dlg.Font = Font;
            dlg.AutoScaleMode = AutoScaleMode.None;
            dlg.ClientSize = new Size(D(520), D(430));
            dlg.BackColor = ShellStyles.Surface;
            dlg.ForeColor = Theme.Ink;
            dlg.StartPosition = FormStartPosition.CenterParent;
            dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
            dlg.MinimizeBox = false; dlg.MaximizeBox = false; dlg.ShowIcon = false; dlg.ShowInTaskbar = false;

            int y = D(18), pad = D(22), w = D(476);
            Func<string, string, TextBox> field = delegate(string label, string hint)
            {
                var l = new Label { Text = L.T(label), AutoSize = false };
                l.SetBounds(pad, y, w, D(22));
                var box = new TextBox { BorderStyle = BorderStyle.FixedSingle, BackColor = ShellStyles.Surface, ForeColor = Theme.Ink };
                box.SetBounds(pad, y + D(24), w, D(28));
                box.PlaceholderText = L.T(hint);
                dlg.Controls.Add(l);
                dlg.Controls.Add(box);
                y += D(62);
                return box;
            };
            TextBox name = field("名字（必填）", "比如 Kimi");
            TextBox proc = field("桌面端的进程名", "任务管理器里看到的名字，不带 .exe，比如 Kimi");
            TextBox exe = field("桌面端程序位置", "可以留空：开着的窗口照样能纳入，只是启动不了");
            var browse = new ShellDialogButton { Text = "浏览…" };
            exe.Width = w - D(92);
            browse.SetBounds(pad + w - D(84), exe.Top - D(2), D(84), D(32));
            dlg.Controls.Add(browse);
            browse.Click += delegate
            {
                using (var ofd = new OpenFileDialog { Filter = L.T("程序 (*.exe)|*.exe"), Title = L.T("选桌面端程序") })
                    if (ofd.ShowDialog(dlg) == DialogResult.OK)
                    {
                        exe.Text = ofd.FileName;
                        if (proc.Text.Trim().Length == 0) proc.Text = System.IO.Path.GetFileNameWithoutExtension(ofd.FileName);
                        if (name.Text.Trim().Length == 0) name.Text = System.IO.Path.GetFileNameWithoutExtension(ofd.FileName);
                    }
            };
            TextBox cli = field("CLI 命令", "命令行里敲的命令，比如 kimi；没有就留空");
            TextBox home = field("官网（可选）", "https://…");

            var err = new Label { ForeColor = Color.FromArgb(200, 70, 50), AutoSize = false };
            err.SetBounds(pad, y, w, D(22));
            var ok = new ShellDialogButton { Text = "加入名单", Primary = true };
            var cancel = new ShellDialogButton { Text = "取消", DialogResult = DialogResult.Cancel };
            ok.SetBounds(pad + w - D(190), dlg.ClientSize.Height - D(52), D(100), D(34));
            cancel.SetBounds(pad + w - D(84), dlg.ClientSize.Height - D(52), D(84), D(34));
            dlg.Controls.AddRange(new Control[] { err, ok, cancel });
            dlg.CancelButton = cancel;

            AgentKind added = null;
            ok.Click += delegate
            {
                string n = name.Text.Trim();
                if (n.Length == 0) { err.Text = L.T("先写个名字"); name.Focus(); return; }
                if (proc.Text.Trim().Length == 0 && exe.Text.Trim().Length == 0 && cli.Text.Trim().Length == 0)
                { err.Text = L.T("进程名、程序位置、CLI 命令至少填一个，不然认不出它"); proc.Focus(); return; }
                string id = Catalog.MakeId(n);
                AgentKind same = Catalog.ById(id);
                if (same != null && !same.Custom) id = id + "-custom";
                try
                {
                    added = Catalog.AddCustom(new Catalog.CustomEntry
                    {
                        Id = id, Name = n, Process = proc.Text, Exe = exe.Text, Cli = cli.Text, Home = home.Text
                    });
                    dlg.DialogResult = DialogResult.OK;
                }
                catch (Exception ex) { err.Text = L.F("保存失败：{0}", ex.Message); }
            };
            return dlg.ShowDialog(owner) == DialogResult.OK ? added : null;
        }
    }

    // 名单里的一张卡片：自己画，按钮用现成的 ShellDialogButton
    sealed class RosterCard : Panel
    {
        readonly HostForm _host;
        readonly AgentKind _k;
        readonly Bitmap _icon;
        readonly bool _desk, _cli;
        public event EventHandler StartDesktop, StartCli, Remove;

        public RosterCard(HostForm host, AgentKind k, int w, int h)
        {
            _host = host;
            _k = k;
            _desk = Catalog.HasDesktop(k);
            _cli = Catalog.HasCli(k);
            _icon = LoadIcon(k);
            Size = new Size(w, h);
            BackColor = ShellStyles.Surface;
            DoubleBuffered = true;

            var buttons = new List<ShellDialogButton>();
            bool hasDeskKind = k.Processes != null && k.Processes.Length > 0;
            if (hasDeskKind && _desk)
            {
                var b = new ShellDialogButton { Text = host.SlotOf(k) != null ? "已在格子里" : "启动", Primary = host.SlotOf(k) == null };
                b.Enabled = host.SlotOf(k) == null;
                b.Click += delegate { if (StartDesktop != null) StartDesktop(this, EventArgs.Empty); };
                buttons.Add(b);
            }
            if (_cli)
            {
                var b = new ShellDialogButton { Text = "新建 CLI" };
                b.Click += delegate { if (StartCli != null) StartCli(this, EventArgs.Empty); };
                buttons.Add(b);
            }
            if (!_desk && !_cli && !string.IsNullOrEmpty(k.Home))
            {
                var b = new ShellDialogButton { Text = "去官网" };
                string url = k.Home;
                b.Click += delegate { try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { } };
                buttons.Add(b);
            }
            if (k.Custom)
            {
                var b = new ShellDialogButton { Text = "删除" };
                b.Click += delegate { if (Remove != null) Remove(this, EventArgs.Empty); };
                buttons.Add(b);
            }
            int x = w - host.D(14);
            for (int i = buttons.Count - 1; i >= 0; i--)
            {
                int bw = host.D(buttons[i].Text.Length > 3 ? 104 : 76);
                x -= bw;
                buttons[i].SetBounds(x, h - host.D(46), bw, host.D(32));
                Controls.Add(buttons[i]);
                x -= host.D(8);
            }
        }

        static Bitmap LoadIcon(AgentKind k)
        {
            try
            {
                string p = Catalog.DesktopExe(k);
                if (p == null && k.Shortcuts != null && k.Shortcuts.Length > 0) p = Launch.FindShortcut(k.Shortcuts);
                if (p == null && Catalog.HasCli(k))
                {
                    string c = Catalog.CliPath(k);
                    if (c != null && c.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) p = c;
                }
                if (p == null) return null;
                using (Icon ic = Icon.ExtractAssociatedIcon(p)) return ic == null ? null : ic.ToBitmap();
            }
            catch { return null; }
        }

        string StateText()
        {
            string desk = _k.Processes == null || _k.Processes.Length == 0 ? null
                        : L.T(_k.DesktopState == "hooks" ? "桌面端状态：官方钩子" : "桌面端状态：看 CPU 兜底");
            string cli = string.IsNullOrEmpty(_k.CliCommand) ? null
                       : L.T(_k.DesktopState == "hooks" ? "CLI 状态：官方钩子" : "CLI 状态：看 CPU 兜底");
            if (desk != null && cli != null) return desk + " · " + cli;
            return desk ?? cli ?? "";
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            try { PaintBody(e); }
            catch (Exception ex) { PaintGuard.Failed(this, ex); }
        }

        void PaintBody(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            float r = _host.D(12);
            using (var path = Theme.Round(new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), r))
            using (var br = new SolidBrush(Theme.Pane))
            using (var pen = new Pen(ShellStyles.Border))
            {
                g.FillPath(br, path);
                g.DrawPath(pen, path);
            }
            int pad = _host.D(16), isz = _host.D(28);
            if (_icon != null) g.DrawImage(_icon, new Rectangle(pad, pad, isz, isz));
            else using (var b = new SolidBrush(ShellStyles.Hover)) g.FillEllipse(b, pad, pad, isz, isz);
            int tx = pad + isz + _host.D(12);
            Theme.Text(g, _k.Name, _host.NameFont, Theme.Ink, new RectangleF(tx, pad - _host.D(2), Width - tx - pad, _host.D(22)), StringAlignment.Near);
            // 标签：桌面端 / CLI 装没装，自定义
            int bx = tx, by = pad + _host.D(22);
            if (_k.Processes != null && _k.Processes.Length > 0) bx = Badge(g, bx, by, L.T(_desk ? "桌面端 已装" : "桌面端 未装"), _desk);
            if (!string.IsNullOrEmpty(_k.CliCommand)) bx = Badge(g, bx, by, _cli ? L.T("CLI 已装") : L.F("CLI 未装（{0}）", _k.CliCommand), _cli);
            if (_k.Custom) Badge(g, bx, by, L.T("自定义"), true);
            Theme.Text(g, StateText(), _host.SubFont, Theme.Dim,
                new RectangleF(pad, pad + isz + _host.D(14), Width - pad * 2, _host.D(20)), StringAlignment.Near);
        }

        int Badge(Graphics g, int x, int y, string text, bool on)
        {
            Size sz = TextRenderer.MeasureText(text, _host.SubFont);
            int w = sz.Width + _host.D(10), h = _host.D(20);
            using (var path = Theme.Round(new RectangleF(x, y, w, h), _host.D(6)))
            using (var br = new SolidBrush(on ? ShellStyles.Hover : Theme.Pane))
            using (var pen = new Pen(ShellStyles.Border))
            {
                g.FillPath(br, path);
                if (!on) g.DrawPath(pen, path);
            }
            Theme.Text(g, text, _host.SubFont, on ? Theme.Ink : Theme.Dim, new RectangleF(x, y, w, h), StringAlignment.Center);
            return x + w + _host.D(6);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _icon != null) _icon.Dispose();
            base.Dispose(disposing);
        }
    }
}
