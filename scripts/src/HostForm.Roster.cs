// 「Agent 名单」页：跟设置页、委托页同一种弹出面板（自绘、圆角卡片、同一套按钮）。
//   每家一张小卡：图标、名字，右边几个按钮（启动 / 新建 CLI；没装的是「去官网」），最右一个「⋯」：
//   装 / 卸钩子、卸载软件、从名单删掉（自定义的）或藏起来（内置的）。藏起来的在最底下「藏起来的」，能放回。
//   装了的排前面，没装的在「没装的」下面、卡片不填底色。不写「桌面端已装 / CLI 状态：官方钩子」这类技术说明
//  （用户 2026-10-15：信息太多、跟别处不统一）；钩子装没装放进「⋯」里。底部「＋ 添加自定义 agent」。
// 失去焦点就关；Esc 关闭。放不下时中间那段可以滚。
// 点过「启动 / 新建 CLI」之后面板不关、失去焦点也不关（用户 2026-10-24：启动一家就得重新点设置再开名单，
// 不方便一次开几家），右上角 ✕ 或 Esc 关。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

internal sealed partial class HostForm
{
    RosterPanel _rosterPanel;

    void ShowRoster()
    {
        if (_rosterPanel != null && !_rosterPanel.IsDisposed) { _rosterPanel.Close(); return; }
        _menuOpen = true;
        IntPtr giveBack = _lastFocused;
        Catalog.ForgetInstallCache();   // 刚装好的能认出来
        var p = new RosterPanel(this);
        _rosterPanel = p;
        p.FormClosed += delegate
        {
            _menuOpen = false;
            _rosterPanel = null;
            MethodInvoker act = p.Action;   // 失去焦点和点击都会关，FormClosed 可能来两遍：动作只取一次
            p.Action = null;
            if (act != null) BeginInvoke(act);
            else GiveBackAfterPopup(giveBack);
        };
        p.Location = _roster.PointToScreen(new Point(0, _roster.Height + D(6)));
        p.Reflow();
        p.Show(this);
        p.Activate();
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

    internal sealed class RosterPanel : Form, IHostPopup
    {
        readonly HostForm _host;
        public MethodInvoker Action;

        // 卡片和按钮的位置都按「整段列表」算（没滚过的坐标）；底部那一行是窗口坐标
        sealed class Card { public Rectangle R; public AgentKind K; public bool Installed; public string Sub; public Bitmap Icon; }
        sealed class Item { public Rectangle R; public string What; public AgentKind K; public bool Fixed; }
        readonly List<Card> _cards = new List<Card>();
        readonly List<Item> _items = new List<Item>();
        Item _hot;
        string _armed;      // 在「⋯」里点了「删除 / 卸载」的那一家：卡上出一个「确定删除 / 确定卸载」，再点才做
        string _armedWhat;  // "remove" / "uninstall"
        string _note;       // 上一次操作的结果（画在底下）
        bool _pinned;       // 点过启动：失去焦点也不关
        ContextMenuStrip _more;   // 「⋯」开着的那份菜单
        int _notInstalledY = -1;   // 「没装的」那一节标题的位置（列表坐标）
        int _hiddenY = -1;         // 「藏起来的」
        int _listTop, _listH, _listFull, _scroll;

        int D(int v) { return _host.D(v); }
        int Pad { get { return D(16); } }
        int W { get { return D(760); } }   // 680 → 760：每张卡多了一个「⋯」，三个按钮时名字要留得下
        int CardH { get { return D(56); } }
        int Gap { get { return D(10); } }

        public RosterPanel(HostForm host)
        {
            _host = host;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            KeyPreview = true;
            DoubleBuffered = true;
            BackColor = ShellStyles.Surface;
            Font = host.Font;
            Reflow();
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

        // 跟设置页一样：激活时把焦点收回自己，Esc 按下、松开都认（输入队列是跟嵌进来的客户端共用的）
        protected override void OnActivated(EventArgs e) { base.OnActivated(e); Native.SetFocus(Handle); }
        bool _closing, _busy, _escDown;
        protected override void OnFormClosing(FormClosingEventArgs e) { _closing = true; base.OnFormClosing(e); }
        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            if (!_busy && !_pinned && !_closing && !IsDisposed) Close();
        }
        // 点进了某一格（HostForm.Popups.cs）：「⋯」菜单开着就连它一起关，钉住的也关；
        // 选文件夹 / 小表单开着时是模态的，点不到格子，到不了这里
        public void DismissForSlotClick()
        {
            if (_closing || IsDisposed) return;
            if (_more != null && _more.Visible) _more.Close(ToolStripDropDownCloseReason.AppFocusChange);
            else if (_busy) return;
            _pinned = false;
            Close();
        }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape) { _escDown = true; e.Handled = true; Close(); return; }
            base.OnKeyDown(e);
        }
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

        // 按钮宽度按字量：两个字的、四个字的都留一样的边距，最窄跟委托页的「撤销」一样
        int ButtonW(string label) { return Math.Max(D(64), TextRenderer.MeasureText(L.T(label), Font).Width + D(24)); }

        static string Label(Item it, string armed)
        {
            switch (it.What)
            {
                case "start": return "启动";
                case "cli": return "新建 CLI";
                case "web": return "去官网";
                case "unhide": return "放回";
                case "more": return "⋯";
                case "close": return "✕";
                case "remove": return "确定删除";
                case "uninstall": return "确定卸载";
                default: return "＋ 添加自定义 agent";
            }
        }

        // 重新排一遍：卡片、按钮、窗口大小（放不下就让中间那段滚）
        public void Reflow()
        {
            _cards.Clear();
            _items.Clear();
            _hot = null;
            int inner = W - Pad * 2, colW = (inner - Gap) / 2;
            var on = new List<AgentKind>();
            var off = new List<AgentKind>();
            var hidden = new List<AgentKind>();
            foreach (AgentKind k in Catalog.All)
                (Catalog.IsHidden(k) ? hidden : Catalog.HasDesktop(k) || Catalog.HasCli(k) ? on : off).Add(k);

            int y = 0;
            System.Action<List<AgentKind>, bool, bool> lay = delegate(List<AgentKind> kinds, bool installed, bool isHidden)
            {
                for (int i = 0; i < kinds.Count; i++)
                {
                    AgentKind k = kinds[i];
                    var r = new Rectangle(Pad + (i % 2) * (colW + Gap), y + (i / 2) * (CardH + Gap), colW, CardH);
                    Slot slot = _host.SlotOf(k);
                    bool inSlot = slot != null;
                    string sub = !inSlot ? (k.Custom ? "自定义" : null)
                        : slot.Hwnd == IntPtr.Zero ? "正在启动…" : "已在格子里";
                    _cards.Add(new Card { R = r, K = k, Installed = installed, Sub = sub, Icon = AgentIcons.Get(k, D(28)) });
                    // 按钮从右往左摆
                    var whats = new List<string>();
                    if (isHidden) whats.Add("unhide");
                    else if (_armed == k.Id) whats.Add(_armedWhat);
                    else
                    {
                        bool desk = k.Processes != null && k.Processes.Length > 0 && Catalog.HasDesktop(k);
                        if (desk && !inSlot) whats.Add("start");
                        if (Catalog.HasCli(k)) whats.Add("cli");
                        if (!installed && !string.IsNullOrEmpty(k.Home)) whats.Add("web");
                        whats.Add("more");
                    }
                    int x = r.Right - D(12);
                    for (int j = whats.Count - 1; j >= 0; j--)
                    {
                        var it = new Item { What = whats[j], K = k };
                        int bw = it.What == "more" ? D(34) : ButtonW(Label(it, null));
                        x -= bw;
                        it.R = new Rectangle(x, r.Y + (r.Height - D(30)) / 2, bw, D(30));
                        _items.Add(it);
                        x -= D(8);
                    }
                }
                if (kinds.Count > 0) y += ((kinds.Count + 1) / 2) * (CardH + Gap) - Gap;
            };
            lay(on, true, false);
            _notInstalledY = -1;
            if (off.Count > 0)
            {
                if (on.Count > 0) y += D(14);
                _notInstalledY = y;
                y += D(26);
                lay(off, false, false);
            }
            _hiddenY = -1;
            if (hidden.Count > 0)
            {
                if (on.Count + off.Count > 0) y += D(14);
                _hiddenY = y;
                y += D(26);
                lay(hidden, false, true);
            }
            _listFull = y;

            // 窗口：标题 46，列表，底下一条线 + 一行入口（+ 一行结果）
            int head = D(46), foot = D(12) + D(34) + D(10) + (_note != null ? D(22) : 0);
            Rectangle work = Screen.FromPoint(Location).WorkingArea;
            int maxList = Math.Max(CardH * 2, work.Height - D(24) - head - foot);
            _listTop = head;
            _listH = Math.Min(_listFull, maxList);
            _scroll = Math.Max(0, Math.Min(_scroll, _listFull - _listH));
            int fy = head + _listH + D(12);
            _items.Add(new Item { R = new Rectangle(Pad - D(6), fy, ButtonW(Label(new Item(), null)) + D(12), D(34)), What = "add", Fixed = true });
            _items.Add(new Item { R = new Rectangle(W - Pad - D(32) + D(6), D(7), D(32), D(32)), What = "close", Fixed = true });

            ClientSize = new Size(W, head + _listH + foot);
            if (Bottom > work.Bottom) Top = Math.Max(work.Top, work.Bottom - Height);
            if (Right > work.Right) Left = Math.Max(work.Left, work.Right - Width);
            Invalidate();
        }

        // 列表坐标 → 窗口坐标
        Rectangle OnScreen(Rectangle r) { return new Rectangle(r.X, r.Y + _listTop - _scroll, r.Width, r.Height); }
        Rectangle ListArea { get { return new Rectangle(0, _listTop, Width, _listH); } }

        Item Hit(Point p)
        {
            foreach (Item it in _items)
            {
                if (it.Fixed) { if (it.R.Contains(p)) return it; continue; }
                if (ListArea.Contains(p) && OnScreen(it.R).Contains(p)) return it;
            }
            return null;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            Item h = Hit(e.Location);
            if (h != _hot) { _hot = h; Cursor = h != null ? Cursors.Hand : Cursors.Default; Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e) { if (_hot != null) { _hot = null; Invalidate(); } base.OnMouseLeave(e); }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            int max = _listFull - _listH;
            if (max <= 0) return;
            int s = Math.Max(0, Math.Min(max, _scroll - e.Delta * (CardH + Gap) / 120));
            if (s == _scroll) return;
            _scroll = s;
            _hot = Hit(PointToClient(MousePosition));
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left) return;
            Item it = Hit(e.Location);
            if (it == null) { if (_armed != null) { _armed = null; Reflow(); } return; }
            HostForm host = _host;
            AgentKind k = it.K;
            if (it.What != "remove" && it.What != "uninstall" && _armed != null) { _armed = null; Reflow(); }
            switch (it.What)
            {
                case "close":
                    Close();
                    break;
                case "start":
                    Pin();
                    host.AddOrLaunch(k);
                    _note = L.F("正在启动 {0}，可以接着开别的", k.Name);
                    Reflow();
                    break;
                case "cli":
                    Pin();
                    _busy = true;   // 选文件夹的窗会让面板失去焦点
                    try { host.AddCli(k); }
                    finally { _busy = false; }
                    Reflow();
                    break;
                case "web":
                    try { Process.Start(new ProcessStartInfo(k.Home) { UseShellExecute = true }); } catch { }
                    break;
                case "unhide":
                    Catalog.SetHidden(k, false);
                    _note = L.F("{0} 放回名单了", k.Name);
                    Reflow();
                    break;
                case "more":
                    ShowMore(it);
                    break;
                case "remove":
                    _armed = null;
                    if (host.SlotOf(k) != null) { _note = L.T("它还在格子里，先移出再删。"); Reflow(); break; }
                    Catalog.RemoveCustom(k);
                    _note = L.F("已从名单删掉 {0}", k.Name);
                    Reflow();
                    break;
                case "uninstall":
                    _armed = null;
                    RunUninstall(k);
                    Reflow();
                    break;
                case "add":
                    {
                        AgentKind added;
                        _busy = true;   // 弹出表单会让面板失去焦点：这时别关
                        try { added = host.AskCustomAgent(this); }
                        finally { _busy = false; }
                        Activate();
                        Native.SetFocus(Handle);
                        if (added != null) { _note = L.F("已加入名单：{0}", added.Name); Reflow(); }
                    }
                    break;
            }
        }

        // 点过启动：面板留着；宿主别再当「菜单开着」，新开的格子要能照常接焦点、嵌进来
        void Pin()
        {
            _pinned = true;
            _host._menuOpen = false;
        }

        // 「⋯」：钩子、卸载、删掉 / 藏起来。弹菜单会让面板失去焦点，这段时间别关
        void ShowMore(Item it)
        {
            AgentKind k = it.K;
            var menu = new ContextMenuStrip();
            bool inSlot = _host.SlotOf(k) != null;

            bool? hooked = HookSetup.Installed(k.Id);
            if (hooked == true)
            {
                menu.Items.Add(new ToolStripMenuItem(L.T("状态钩子已装")) { Enabled = false });
                menu.Items.Add(L.T("卸掉钩子"), null, delegate { _note = HookSetup.ApplyOne(false, k.Id); Reflow(); });
            }
            else if (hooked == false)
                menu.Items.Add(L.T("装上状态钩子"), null, delegate { _note = HookSetup.ApplyOne(true, k.Id); Reflow(); });
            else
                menu.Items.Add(new ToolStripMenuItem(L.T(k.DesktopState == "hooks" ? "还没装，装好后才能装钩子" : "这家没有官方钩子，状态看它忙不忙"))
                               { Enabled = false });
            menu.Items.Add(new ToolStripSeparator());

            if (Catalog.HasDesktop(k))
            {
                string[] un = Catalog.Uninstaller(k);
                if (un == null)
                    menu.Items.Add(new ToolStripMenuItem(L.T(!string.IsNullOrEmpty(k.AppsFolder) ? "卸载软件：去系统设置里的「应用」" : "卸载软件：没找到它的卸载程序"))
                                   { Enabled = false });
                else if (inSlot)
                    menu.Items.Add(new ToolStripMenuItem(L.T("卸载软件：先把它从格子里移出")) { Enabled = false });
                else
                    menu.Items.Add(L.T("卸载软件…"), null, delegate { _armed = k.Id; _armedWhat = "uninstall"; Reflow(); });
            }
            if (k.Custom)
                menu.Items.Add(L.T("从名单删掉…"), null, delegate { _armed = k.Id; _armedWhat = "remove"; Reflow(); });
            else
                menu.Items.Add(L.T("从名单藏起来"), null, delegate
                {
                    Catalog.SetHidden(k, true);
                    _note = L.F("{0} 藏到最底下了，随时能放回", k.Name);
                    Reflow();
                });
            if (!string.IsNullOrEmpty(k.Home))
                menu.Items.Add(L.T("打开官网"), null, delegate
                {
                    try { Process.Start(new ProcessStartInfo(k.Home) { UseShellExecute = true }); } catch { }
                });

            _busy = true;
            _more = menu;
            menu.Closed += delegate
            {
                if (_more == menu) _more = null;
                BeginInvoke((MethodInvoker)delegate
                {
                    _busy = false;
                    if (IsDisposed || _closing) return;
                    Activate();
                    Native.SetFocus(Handle);
                });
            };
            ShellStyles.Menu(menu, Font, _host._scale);
            Rectangle r = OnScreen(it.R);
            menu.Show(this, new Point(r.Left, r.Bottom + D(2)));
        }

        // 跑这家自己的卸载程序（卸载界面是它自己的，我们不替用户点任何东西）。卸完名单页下次打开就挪到「没装的」
        void RunUninstall(AgentKind k)
        {
            string[] un = Catalog.Uninstaller(k);
            if (un == null) { _note = L.T("没找到它的卸载程序"); return; }
            string cmd = un[1].Trim(), exe, args;
            if (cmd.StartsWith("\""))
            {
                int q = cmd.IndexOf('"', 1);
                exe = q > 0 ? cmd.Substring(1, q - 1) : cmd.Trim('"');
                args = q > 0 ? cmd.Substring(q + 1).Trim() : "";
            }
            else
            {
                int e = cmd.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
                exe = e > 0 ? cmd.Substring(0, e + 4) : cmd;
                args = e > 0 ? cmd.Substring(e + 4).Trim() : "";
            }
            try
            {
                Log.W("roster: 卸载 " + k.Name + " → " + cmd);
                Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = true });
                _pinned = true;   // 卸载程序的窗口会抢焦点
                _note = L.F("已打开 {0} 的卸载程序，按它的提示走", un[0]);
                Catalog.ForgetInstallCache();
            }
            catch (Exception ex) { _note = L.F("卸载程序打不开：{0}", ex.Message); }
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
            using (var bold = Fonts.Medium(Font.Size))
            using (var small = new Font(Font.FontFamily, Font.Size * 0.86f))
            {
                Theme.Text(g, L.T("Agent 名单"), title, Theme.Ink, new RectangleF(Pad, 0, Width - Pad * 2, D(44)), StringAlignment.Near);

                GraphicsState st = g.Save();
                g.SetClip(ListArea);
                g.TranslateTransform(0, _listTop - _scroll);
                if (_notInstalledY >= 0)
                    Theme.Text(g, L.T("没装的"), small, Theme.Dim, new RectangleF(Pad, _notInstalledY, Width - Pad * 2, D(22)), StringAlignment.Near);
                if (_hiddenY >= 0)
                    Theme.Text(g, L.T("藏起来的"), small, Theme.Dim, new RectangleF(Pad, _hiddenY, Width - Pad * 2, D(22)), StringAlignment.Near);
                foreach (Card c in _cards) PaintCard(g, c, bold, small);
                foreach (Item it in _items) if (!it.Fixed) PaintButton(g, it);
                g.Restore(st);

                // 放不下时右边一条细滚动条
                if (_listFull > _listH)
                {
                    float h = Math.Max(D(24), _listH * (float)_listH / _listFull);
                    float y = _listTop + (_listH - h) * _scroll / (float)(_listFull - _listH);
                    using (GraphicsPath gp = Theme.Round(new RectangleF(Width - D(8), y, D(4), h), D(2)))
                    using (var br = new SolidBrush(Color.FromArgb(Theme.Light ? 60 : 90, Theme.Light ? Color.Black : Color.White)))
                        g.FillPath(br, gp);
                }

                // 底部：一条线 + 「＋ 添加自定义 agent」，跟设置页的「↻ 重新安装钩子」一个写法
                int ly = _listTop + _listH + D(6);
                using (var pen = new Pen(ShellStyles.Border)) g.DrawLine(pen, Pad, ly, Width - Pad, ly);
                foreach (Item it in _items)
                {
                    if (!it.Fixed) continue;
                    if (it == _hot)
                        using (GraphicsPath gp = Theme.Round(it.R, D(8)))
                        using (var br = new SolidBrush(ShellStyles.Hover))
                            g.FillPath(br, gp);
                    if (it.What == "close") { Theme.Text(g, Label(it, null), Font, Theme.Dim, it.R, StringAlignment.Center); continue; }
                    Theme.Text(g, L.T(Label(it, null)), Font, Theme.Brand, new RectangleF(it.R.X + D(6), it.R.Y, it.R.Width - D(6), it.R.Height), StringAlignment.Near);
                    if (_note != null)
                        Theme.Text(g, _note, small, Theme.Dim, new RectangleF(Pad, it.R.Bottom, Width - Pad * 2, D(22)), StringAlignment.Near);
                }
            }
        }

        void PaintCard(Graphics g, Card c, Font bold, Font small)
        {
            Rectangle r = c.R;
            // 装了的填一层淡底（跟委托页没了结的那种一样），没装的只描边
            using (GraphicsPath gp = Theme.Round(r, D(8)))
            using (var br = new SolidBrush(Color.FromArgb(c.Installed ? (Theme.Light ? 8 : 20) : 0, Theme.Light ? Color.Black : Color.White)))
            using (var pen = new Pen(ShellStyles.Border))
            { g.FillPath(br, gp); g.DrawPath(pen, gp); }
            int isz = D(28);
            var ir = new Rectangle(r.X + D(12), r.Y + (r.Height - isz) / 2, isz, isz);
            Theme.Icon(g, c.Icon, ir, D(3), D(5));
            int tx = ir.Right + D(10), right = r.Right - D(12);
            foreach (Item it in _items) if (it.K == c.K && !it.Fixed) right = Math.Min(right, it.R.X - D(8));
            using (var sf = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap, LineAlignment = StringAlignment.Center })
            {
                Color ink = c.Installed ? Theme.Ink : Theme.Dim;
                if (c.Sub == null)
                    using (var b = new SolidBrush(ink)) g.DrawString(c.K.Name, bold, b, new RectangleF(tx, r.Y, right - tx, r.Height), sf);
                else
                {
                    using (var b = new SolidBrush(ink)) g.DrawString(c.K.Name, bold, b, new RectangleF(tx, r.Y + D(8), right - tx, D(22)), sf);
                    using (var b = new SolidBrush(Theme.Dim)) g.DrawString(L.T(c.Sub), small, b, new RectangleF(tx, r.Y + D(28), right - tx, D(20)), sf);
                }
            }
        }

        // 跟委托页、签字小卡一样的按钮：主按钮实心品牌色，别的描边、悬停出淡底
        void PaintButton(Graphics g, Item it)
        {
            bool hot = it == _hot, primary = it.What == "start", danger = it.What == "remove" || it.What == "uninstall";
            using (GraphicsPath gp = Theme.Round(it.R, D(6)))
            {
                if (primary)
                    using (var br = new SolidBrush(hot ? ControlPaint.Light(Theme.Brand, 0.1f) : Theme.Brand)) g.FillPath(br, gp);
                else
                    using (var br = new SolidBrush(hot ? ShellStyles.Hover : Color.Transparent))
                    using (var pen = new Pen(danger ? Color.FromArgb(255, 217, 48, 37) : ShellStyles.Border))
                    { g.FillPath(br, gp); g.DrawPath(pen, gp); }
            }
            Color ink = primary ? Theme.OnBrand : danger ? Color.FromArgb(255, 217, 48, 37) : Theme.Ink;
            Theme.Text(g, it.What == "more" ? Label(it, _armed) : L.T(Label(it, _armed)), Font, ink, it.R, StringAlignment.Center);
        }
    }
}
