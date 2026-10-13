// 「＋ 新会话」面板（替换原来的「启动 ▾」菜单，照 Que 的思路）：
//   第一步：只列装了的 agent，每家一张图标卡片。点卡片 = 开桌面端（已经开着就跳过去）；
//           卡片右下角「终端」= 开这家的 CLI 会话（只有 CLI 的，整张卡片就是终端）。
//   第二步（终端）：最近用过的文件夹一键开，或者「选择其他文件夹…」。
//   底部「更多 agent…」进 Agent 名单（看没装的、加自定义）。
// 失去焦点就关；Esc 关闭 / 在第二步返回第一步。

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;

internal sealed partial class HostForm
{
    NewSessionPanel _newSession;

    void ShowNewSession(object sender, EventArgs e)
    {
        if (_newSession != null && !_newSession.IsDisposed) { _newSession.Close(); return; }
        var kinds = new List<AgentKind>();
        foreach (AgentKind k in Catalog.All)
            if (Catalog.HasDesktop(k) || Catalog.HasCli(k) || SlotOf(k) != null) kinds.Add(k);
        _menuOpen = true;
        IntPtr giveBack = _lastFocused;
        Log.W("new-session panel open");
        var p = new NewSessionPanel(this, kinds);
        _newSession = p;
        p.FormClosed += delegate
        {
            _menuOpen = false;
            _newSession = null;
            // 动作只取一次：面板关闭时失去焦点会再触发一次关闭，FormClosed 可能来两遍
            // （2026-09-26 实测：点一次文件夹开出两个 Codex 终端）
            MethodInvoker act = p.Action;
            p.Action = null;
            Log.W("new-session panel closed, action=" + (act != null));
            if (act != null) BeginInvoke(act);
            else if (giveBack != IntPtr.Zero && Native.IsWindow(giveBack) && SlotWithHwnd(giveBack) != null)
                BeginInvoke((MethodInvoker)delegate { if (!_menuOpen) GiveFocus(giveBack, true); });
        };
        Point at = _launch.PointToScreen(new Point(0, _launch.Height + D(6)));
        p.Location = at;
        p.Show(this);
        p.Activate();
    }

    // 点卡片：开着就跳过去，没开就启动桌面端
    void OpenDesktop(AgentKind k)
    {
        Slot open = SlotOf(k);
        if (open != null)
        {
            if (_queueMode) PinFront(open);
            else if (open.Hwnd != IntPtr.Zero) { _focusByUser = true; GiveFocus(open.Hwnd, true); }
            return;
        }
        AddOrLaunch(k);
        Slot ns = SlotOf(k);
        if (_queueMode && ns != null) PinFront(ns);
    }

    // 最近用过的 CLI 工作文件夹（Prefs "cli.recent"，| 分隔，新的在前，最多 8 个，只留还存在的）
    static List<string> RecentFolders()
    {
        var list = new List<string>();
        foreach (string s in Prefs.GetStr("cli.recent", "").Split('|'))
            if (s.Length > 0 && Directory.Exists(s) && !list.Exists(x => string.Equals(x, s, StringComparison.OrdinalIgnoreCase))) list.Add(s);
        return list;
    }

    // 候选文件夹：面板里开过的（cli.recent）在前，再补上各家会话实际用过的工作目录
    // （钩子写的 sessions\*.json 里的 cwd，按最近一次事件排），最后是配置里这家的默认工作区。最多 7 个。
    static List<string> SuggestFolders(AgentKind k)
    {
        var list = RecentFolders();
        var seen = new List<KeyValuePair<long, string>>();
        try
        {
            foreach (string f in Directory.GetFiles(HookSetup.SessionsDir, "*.json"))
            {
                string json;
                try { json = File.ReadAllText(f); } catch { continue; }
                string cwd = JsonField(json, "cwd");
                long at;
                long.TryParse(JsonField(json, "at"), out at);
                if (string.IsNullOrEmpty(cwd)) continue;
                // Cursor 写的是 /D:/Cursor content/... 这种
                if (cwd.Length > 3 && cwd[0] == '/' && cwd[2] == ':') cwd = cwd.Substring(1);
                cwd = cwd.Replace('/', '\\').TrimEnd('\\');
                if (cwd.Length == 2 && cwd[1] == ':') cwd += "\\";
                if (cwd.IndexOf("\\Temp", StringComparison.OrdinalIgnoreCase) >= 0 || !Directory.Exists(cwd)) continue;
                seen.Add(new KeyValuePair<long, string>(at, cwd));
            }
        }
        catch { }
        seen.Sort((a, b) => b.Key.CompareTo(a.Key));
        foreach (var kv in seen)
            if (!list.Exists(x => string.Equals(x, kv.Value, StringComparison.OrdinalIgnoreCase))) list.Add(kv.Value);
        string dflt = Cfg.WorkspaceFor(k);
        if (!string.IsNullOrEmpty(dflt) && Directory.Exists(dflt) && !list.Exists(x => string.Equals(x, dflt, StringComparison.OrdinalIgnoreCase)))
            list.Add(dflt);
        if (list.Count > 7) list.RemoveRange(7, list.Count - 7);
        return list;
    }

    // 会话文件是钩子写的扁平 JSON，只取一个字段，不值得拉 JSON 库
    static string JsonField(string json, string name)
    {
        try
        {
            using (var doc = System.Text.Json.JsonDocument.Parse(json))
            {
                System.Text.Json.JsonElement v;
                if (!doc.RootElement.TryGetProperty(name, out v)) return null;
                return v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() : v.ToString();
            }
        }
        catch { return null; }
    }

    static void RememberFolder(string path)
    {
        var list = RecentFolders();
        list.RemoveAll(x => string.Equals(x, path, StringComparison.OrdinalIgnoreCase));
        list.Insert(0, path);
        if (list.Count > 8) list.RemoveRange(8, list.Count - 8);
        Prefs.SetStr("cli.recent", string.Join("|", list));
    }

    internal sealed class NewSessionPanel : Form
    {
        readonly HostForm _host;
        readonly List<AgentKind> _kinds;
        AgentKind _cliFor;              // 第二步：给哪家开终端；null = 第一步
        public MethodInvoker Action;    // 关掉后要宿主做的事

        sealed class Item { public Rectangle R; public string What; public AgentKind K; public string Path; }
        readonly List<Item> _items = new List<Item>();
        Item _hot;

        int D(int v) { return _host.D(v); }

        public NewSessionPanel(HostForm host, List<AgentKind> kinds)
        {
            _host = host;
            _kinds = kinds;
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
            int round = 2;   // DWMWCP_ROUND：Win11 圆角
            try { Native.DwmSetInt(Handle, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref round, 4); } catch { }
        }

        // 宿主跟嵌进来的客户端共用输入队列：面板成了前台窗口，键盘焦点却可能还留在那一格里
        //（宿主在同一刻把焦点还了回去），Esc 就按进了客户端（2026-09-26 实测）。激活时把焦点收回自己
        protected override void OnActivated(EventArgs e)
        {
            base.OnActivated(e);
            Native.SetFocus(Handle);
        }

        bool _closing;
        protected override void OnFormClosing(FormClosingEventArgs e) { _closing = true; base.OnFormClosing(e); }
        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            if (!_picking && !_closing && !IsDisposed) Close();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape || (e.KeyCode == Keys.Back && _cliFor != null))
            {
                e.Handled = true;
                if (e.KeyCode == Keys.Escape) _escDown = true;
                if (_cliFor != null) { _cliFor = null; Relayout(); } else Close();
                return;
            }
            base.OnKeyDown(e);
        }

        // Esc 的「按下」有时到不了面板、只到「松开」（共用输入队列，见设置页同一段）：松开时也认，按下处理过的不重复
        bool _escDown;
        protected override void OnKeyUp(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape)
            {
                bool seen = _escDown;
                _escDown = false;
                if (!seen && !_closing)
                {
                    e.Handled = true;
                    if (_cliFor != null) { _cliFor = null; Relayout(); } else Close();
                    return;
                }
            }
            base.OnKeyUp(e);
        }

        const int Cols = 4;
        int TileW { get { return D(128); } }
        int TileH { get { return D(104); } }
        int Pad { get { return D(14); } }
        int HeadH { get { return D(40); } }
        int FootH { get { return D(40); } }
        int RowH { get { return D(48); } }

        void Relayout()
        {
            _items.Clear();
            _hot = null;
            int w = Pad * 2 + Cols * TileW + (Cols - 1) * D(8);
            int y = HeadH;
            if (_cliFor == null)
            {
                for (int i = 0; i < _kinds.Count; i++)
                {
                    var r = new Rectangle(Pad + (i % Cols) * (TileW + D(8)), y + (i / Cols) * (TileH + D(8)), TileW, TileH);
                    AgentKind k = _kinds[i];
                    bool desk = Catalog.HasDesktop(k) || _host.SlotOf(k) != null, cli = Catalog.HasCli(k);
                    _items.Add(new Item { R = r, What = desk ? "desk" : "cli", K = k });
                    if (desk && cli)
                        _items.Add(new Item { R = new Rectangle(r.Right - D(60), r.Bottom - D(32), D(54), D(26)), What = "cli", K = k });
                }
                int rows = Math.Max(1, (_kinds.Count + Cols - 1) / Cols);
                y += rows * (TileH + D(8)) - D(8) + Pad + D(4);
                _items.Add(new Item { R = new Rectangle(Pad, y, w - Pad * 2, FootH - D(8)), What = "more" });
                y += FootH;
            }
            else
            {
                _items.Add(new Item { R = new Rectangle(Pad - D(4), D(8), D(64), D(26)), What = "back" });
                foreach (string f in SuggestFolders(_cliFor))
                {
                    _items.Add(new Item { R = new Rectangle(Pad, y, w - Pad * 2, RowH), What = "folder", K = _cliFor, Path = f });
                    y += RowH + D(4);
                }
                _items.Add(new Item { R = new Rectangle(Pad, y, w - Pad * 2, RowH), What = "browse", K = _cliFor });
                y += RowH + Pad;
            }
            ClientSize = new Size(w, y);
            // 别出屏幕
            Rectangle work = Screen.FromPoint(Location).WorkingArea;
            if (Bottom > work.Bottom) Top = Math.Max(work.Top, work.Bottom - Height);
            if (Right > work.Right) Left = Math.Max(work.Left, work.Right - Width);
            Invalidate();
        }

        Item Hit(Point p)
        {
            // 小的「终端」胶囊叠在卡片上，先认它
            for (int i = _items.Count - 1; i >= 0; i--) if (_items[i].R.Contains(p)) return _items[i];
            return null;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            Item h = Hit(e.Location);
            if (h != _hot) { _hot = h; Cursor = h != null ? Cursors.Hand : Cursors.Default; Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e) { if (_hot != null) { _hot = null; Invalidate(); } base.OnMouseLeave(e); }

        bool _picking;

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left) return;
            Item it = Hit(e.Location);
            Log.W("new-session click at " + e.Location + " -> " + (it == null ? "none" : it.What + " " + it.R + " " + (it.K != null ? it.K.Id : "")));
            if (it == null) return;
            HostForm host = _host;
            AgentKind k = it.K;
            switch (it.What)
            {
                case "desk":
                    Action = delegate { host.OpenDesktop(k); };
                    Close();
                    break;
                case "cli":
                    _cliFor = k;
                    Relayout();
                    break;
                case "back":
                    _cliFor = null;
                    Relayout();
                    break;
                case "folder":
                    {
                        string path = it.Path;
                        Action = delegate { RememberFolder(path); host.AddCli(k, path); };
                        Close();
                    }
                    break;
                case "browse":
                    {
                        string picked = null;
                        _picking = true;   // 选文件夹的对话框会让面板失去焦点，别趁机关掉
                        try
                        {
                            using (var dialog = new FolderBrowserDialog())
                            {
                                dialog.Description = L.F("选择 {0} 终端的工作文件夹", k.Name);
                                dialog.UseDescriptionForTitle = true;
                                dialog.SelectedPath = Cfg.WorkspaceFor(k);
                                if (dialog.ShowDialog(this) == DialogResult.OK) picked = dialog.SelectedPath;
                            }
                        }
                        finally { _picking = false; }
                        if (picked == null) { Activate(); return; }
                        Action = delegate { RememberFolder(picked); host.AddCli(k, picked); };
                        Close();
                    }
                    break;
                case "more":
                    Action = delegate { host.ShowRoster(); };
                    Close();
                    break;
            }
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
                string head = _cliFor == null ? L.T("新会话") : L.F("在哪个文件夹开 {0} 终端？", _cliFor.Name);
                int hx = _cliFor == null ? Pad : Pad + D(66);
                Theme.Text(g, head, title, Theme.Ink, new RectangleF(hx, 0, Width - hx - Pad, HeadH), StringAlignment.Near);
                foreach (Item it in _items)
                {
                    bool hot = it == _hot;
                    switch (it.What)
                    {
                        case "desk":
                        case "cli" when it.R.Width == TileW:
                            PaintTile(g, it, hot, small);
                            break;
                        case "cli":
                            {
                                // 卡片右下角的「终端」胶囊
                                using (GraphicsPath gp = Theme.Round(it.R, it.R.Height / 2f))
                                using (var br = new SolidBrush(hot ? Theme.Brand : Color.FromArgb(Theme.Light ? 18 : 40, Theme.Light ? Color.Black : Color.White)))
                                    g.FillPath(br, gp);
                                Theme.Text(g, L.T("终端"), small, hot ? Theme.OnBrand : Theme.Ink, it.R, StringAlignment.Center);
                            }
                            break;
                        case "more":
                            using (var pen = new Pen(ShellStyles.Border))
                                g.DrawLine(pen, Pad, it.R.Y - D(6), Width - Pad, it.R.Y - D(6));
                            Row(g, it, hot);
                            Theme.Text(g, L.T("更多 agent　没装的、添加自定义"), Font, hot ? Theme.Ink : Theme.Dim, new RectangleF(it.R.X + D(10), it.R.Y, it.R.Width - D(40), it.R.Height), StringAlignment.Near);
                            Theme.Text(g, "›", title, Theme.Dim, new RectangleF(it.R.Right - D(30), it.R.Y, D(20), it.R.Height), StringAlignment.Center);
                            break;
                        case "back":
                            Row(g, it, hot);
                            Theme.Text(g, L.T("‹ 返回"), Font, Theme.Ink, it.R, StringAlignment.Center);
                            break;
                        case "folder":
                            {
                                Row(g, it, hot);
                                string name = Path.GetFileName(it.Path.TrimEnd('\\', '/'));
                                if (string.IsNullOrEmpty(name)) name = it.Path;
                                Theme.Text(g, name, title, Theme.Ink, new RectangleF(it.R.X + D(12), it.R.Y + D(3), it.R.Width - D(24), it.R.Height / 2f), StringAlignment.Near);
                                Theme.Text(g, it.Path, small, Theme.Dim, new RectangleF(it.R.X + D(12), it.R.Y + it.R.Height / 2f - D(1), it.R.Width - D(24), it.R.Height / 2f), StringAlignment.Near);
                            }
                            break;
                        case "browse":
                            Row(g, it, hot);
                            Theme.Text(g, L.T("＋ 选择其他文件夹…"), Font, Theme.Brand, new RectangleF(it.R.X + D(12), it.R.Y, it.R.Width - D(24), it.R.Height), StringAlignment.Near);
                            break;
                    }
                }
            }
        }

        void Row(Graphics g, Item it, bool hot)
        {
            if (!hot) return;
            using (GraphicsPath gp = Theme.Round(it.R, D(8)))
            using (var br = new SolidBrush(ShellStyles.Hover))
                g.FillPath(br, gp);
        }

        void PaintTile(Graphics g, Item it, bool hot, Font small)
        {
            Rectangle r = it.R;
            bool cliHot = _hot != null && _hot.What == "cli" && _hot.K == it.K && _hot != it;
            using (GraphicsPath gp = Theme.Round(r, D(10)))
            {
                using (var br = new SolidBrush(hot || cliHot ? ShellStyles.Hover : Color.FromArgb(Theme.Light ? 8 : 18, Theme.Light ? Color.Black : Color.White)))
                    g.FillPath(br, gp);
                using (var pen = new Pen(ShellStyles.Border)) g.DrawPath(pen, gp);
            }
            int icon = D(34);
            Bitmap bmp = AgentIcons.Get(it.K, icon);
            var ir = new Rectangle(r.X + (r.Width - icon) / 2, r.Y + D(14), icon, icon);
            Theme.Icon(g, bmp, ir, D(5), D(9));
            Theme.Text(g, it.K.Name, Font, Theme.Ink, new RectangleF(r.X + D(6), r.Y + D(52), r.Width - D(12), D(22)), StringAlignment.Center);
            // 底行：已打开 = 小绿点 + 灰字（跟状态灯一个语言；以前用橙红色，看着像报错）；
            // 只有终端的写「只有终端」；只有桌面端的什么都不写（写「桌面端」是废话）
            Slot open = _host.SlotOf(it.K);
            bool pill = Catalog.HasCli(it.K) && it.What == "desk";
            int rowY = r.Bottom - D(32), rowH = D(26);
            if (open != null)
            {
                float d = D(7);
                float cx = pill ? r.X + D(12) : r.X + r.Width / 2f - D(26);
                using (var br = new SolidBrush(Color.FromArgb(255, 52, 168, 83)))
                    g.FillEllipse(br, cx, rowY + (rowH - d) / 2f, d, d);
                Theme.Text(g, L.T("已打开"), small, Theme.Dim, new RectangleF(cx + d + D(5), rowY, D(60), rowH), StringAlignment.Near);
            }
            else if (it.What == "cli")
                Theme.Text(g, L.T("只有终端"), small, Theme.Dim, new RectangleF(r.X + D(8), rowY, r.Width - D(16), rowH), StringAlignment.Center);
        }
    }
}
