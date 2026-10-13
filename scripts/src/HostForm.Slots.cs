// 【拆分】窗口嵌入与格子管理：加格/撤格、纳入、排版、拖动换位、嵌入。（从原 AgentHost.cs 原样搬出，逻辑未改）

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

internal sealed partial class HostForm
{
    string _savedProfileKey;
    bool _loadingSlots;
    bool _restoreOnFirstClaim;
    readonly Dictionary<string, DateTime> _launching = new Dictionary<string, DateTime>();
    Slot _dragSlot, _dragTarget;
    Point _dragStart;
    bool _dragging;
    Cursor _dragCursor;

    List<WinInfo> UnusedWindows(List<WinInfo> all)
    {
        var unused = new List<WinInfo>();
        foreach (WinInfo w in all) if (SlotWithHwnd(w.Handle) == null) unused.Add(w);
        return unused;
    }
    string Surface()
    {
        bool cli = false, desktop = false;
        foreach (Slot s in _slots) { if (s.Kind.Cli) cli = true; else desktop = true; }
        return cli && desktop ? "mixed" : cli ? "cli" : "desktop";
    }
    void AddCli(AgentKind family) { AddCli(family, null); }

    // workspace 给了就直接开（「＋ 新会话」面板里点了最近的文件夹）；没给就弹选文件夹窗
    void AddCli(AgentKind family, string workspace)
    {
        if (_slots.Count >= MaxSlots) { Toast(L.F("格子已满（最多 {0} 格）", MaxSlots)); return; }
        var k = AgentKind.Session(family, "cli-" + Guid.NewGuid().ToString("N"), null);
        if (Launch.CliExe(k) == null) { Toast(L.F("没有找到 {0} CLI，请先安装或加入 PATH", family.Name)); return; }
        if (!string.IsNullOrEmpty(workspace))
        {
            Log.W("AddCli " + family.Id + " @ " + workspace + " from " + new System.Diagnostics.StackTrace(1, false).GetFrame(0)?.GetMethod()?.Name);
            k.Workspace = workspace;
            AddOrLaunch(k);
            // 从「＋ 新会话」开的：开它就是要用它，队列视图里直接放到中间
            Slot ns = SlotOf(k);
            if (_queueMode && ns != null) PinFront(ns);
            return;
        }
        _menuOpen = true;
        try
        {
            using (var dialog = new FolderBrowserDialog())
            {
                dialog.Description = L.F("选择 {0} CLI 的工作目录（每次新建一个独立会话）", family.Name);
                // .NET 里这个对话框默认换成了 Vista 式的选文件夹窗，Description 不再显示在窗里，
                // 要显示只能当标题用。不设这一句，提示文字就丢了。
                dialog.UseDescriptionForTitle = true;
                dialog.SelectedPath = Cfg.WorkspaceFor(family);
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                k.Workspace = dialog.SelectedPath;
            }
        }
        finally { _menuOpen = false; }
        AddOrLaunch(k);
    }

    void WireDrag(Control control)
    {
        control.MouseDown += SlotDragDown;
        control.MouseMove += SlotDragMove;
        control.MouseUp += delegate(object sender, MouseEventArgs e) { if (e.Button == MouseButtons.Left) EndSlotDrag(true); };
    }
    Slot HeaderAt(Point p)
    {
        foreach (Slot s in _slots)
            if (s.Pane.Width > 0 && new Rectangle(s.Pane.X, s.Pane.Y, s.Pane.Width,
                    _queueMode && s != QueueFront ? s.Pane.Height : HeaderH).Contains(p)
                && !(s.DropBtn.Visible && s.DropBtn.Bounds.Contains(p))
                && !(s.ZoomBtn.Visible && s.ZoomBtn.Bounds.Contains(p))) return s;
        return null;
    }
    void SlotDragDown(object sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || e.Clicks > 1) return;
        Point p = PointToClient(((Control)sender).PointToScreen(e.Location));
        // 队列视图：不拖动换位，点左边的签、右边的索引签或者露出来的页边 = 拉到中间来看
        if (_queueMode) { Slot hit = HeaderAt(p) ?? PageAt(p); if (hit != null && hit != QueueFront) PinFront(hit); return; }
        if (_solo != null || _slots.Count < 2) return;
        Slot s = HeaderAt(p); if (s == null) return;
        _dragSlot = s; _dragStart = p; _dragging = false; _dragCursor = Cursor;
        Capture = true;
    }
    void SlotDragMove(object sender, MouseEventArgs e)
    {
        if (_dragSlot == null) return;
        if ((Control.MouseButtons & MouseButtons.Left) == 0 || (Native.GetAsyncKeyState(0x1B) & 0x8000) != 0)
        { EndSlotDrag(false); return; }
        Point p = PointToClient(((Control)sender).PointToScreen(e.Location));
        Size threshold = SystemInformation.DragSize;
        if (!_dragging && Math.Abs(p.X - _dragStart.X) < threshold.Width && Math.Abs(p.Y - _dragStart.Y) < threshold.Height) return;
        _dragging = true; Cursor = Cursors.SizeAll;
        Slot target = null;
        foreach (Slot s in _slots) if (s.Pane.Contains(p) && s != _dragSlot) target = s;
        if (_dragTarget != target)
        {
            Slot old = _dragTarget; _dragTarget = target;
            if (old != null) Invalidate(new Rectangle(old.Pane.X, old.Pane.Y, old.Pane.Width, HeaderH));
            if (target != null) Invalidate(new Rectangle(target.Pane.X, target.Pane.Y, target.Pane.Width, HeaderH));
        }
    }
    void EndSlotDrag(bool commit)
    {
        if (_dragSlot == null) return;
        Slot source = _dragSlot, target = _dragTarget;
        bool swap = commit && _dragging && target != null && (Native.GetAsyncKeyState(0x1B) & 0x8000) == 0;
        _dragSlot = null; _dragTarget = null; _dragging = false;
        Cursor = _dragCursor ?? Cursors.Default; Capture = false;
        if (swap && SlotProfiles.Swap(_slots, source, target))
        { SaveSlots(); Relayout(false); }
        else if (target != null) Invalidate(new Rectangle(target.Pane.X, target.Pane.Y, target.Pane.Width, HeaderH));
    }

    Slot SlotWithHwnd(IntPtr h)
    {
        for (int i = 0; i < _slots.Count; i++)
            if (_slots[i].Hwnd == h) return _slots[i];
        return null;
    }

    void AdoptWindow(WinInfo w)
    {
        if (w == null || !Native.IsWindow(w.Handle)) return;
        if (SlotWithHwnd(w.Handle) != null) return;
        string name = (w.Title ?? "").Trim();
        if (name.Length > 18) name = name.Substring(0, 17) + "…";
        if (name.Length == 0) name = w.ProcessName;
        // id 带上 HWND：同一个程序开两个窗口也能各占一格
        var k = new AgentKind("win-" + w.Handle.ToInt64().ToString("X"), name,
                              new string[] { w.ProcessName }, new string[] { w.ProcessName });
        Slot s = AddSlot(k);
        if (s == null) return;
        s.Custom = true;
        s.Hwnd = w.Handle;
        Relayout(true);
    }

    void CycleLayout()
    {
        if (_queueMode) { SkipFront(); return; }
        int n = _slots.Count == 0 ? _startBtns.Count : _slots.Count;
        if (n <= 0) return;
        RunTransition(delegate
        {
            _layoutIx++;
            SaveLayout(n);
            SaveSlots();
            Relayout(true);
        }, false);
    }


    // 排版按格子数各记各的：4 格时挑的「左主右三」，下次还是 4 格就还该是它。
    void UseLayoutFor(int n)
    {
        if (_layoutForN == n) return;
        _layoutForN = n;
        _layoutIx = Prefs.GetInt("layout." + n, 0);
    }

    void SaveLayout(int n)
    {
        int total = Math.Max(1, Layouts.VariantCount(n));
        int i = _layoutIx % total;
        if (i < 0) i += total;
        Prefs.SetInt("layout." + n, i);
    }

    // 上次放大的是哪一格。开机那一刻它还没被认进格子，所以先记在这儿，
    // 等它出现再应用（见 Relayout）；一直没出现就作罢 —— 不能等到半小时后
    // 有人手动启动了它，界面突然自己放大。
    string _wantSolo;

    void SaveSolo() { Prefs.SetStr("solo", _solo != null ? _solo.Kind.Id : ""); }

    void SaveBounds()
    {
        if (_closed) return;
        Prefs.Hold();
        Prefs.Set("maximized", WindowState == FormWindowState.Maximized);
        Rectangle r = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        if (r.Width >= 400 && r.Height >= 300) Prefs.SetRect("bounds", r);
        Prefs.Release();
    }

    Slot SlotOf(AgentKind k)
    {
        for (int i = 0; i < _slots.Count; i++)
            if (_slots[i].Kind.Id == k.Id) return _slots[i];
        return null;
    }

    // 最多几格。Layouts 对 5 / 6 格各有几套排版，热键也补到了 Ctrl+Alt+6；
    // 再多就不是「一眼看全」了。
    // 网格最多 6 格（排版只做到 6）；队列视图一次只看一个，放宽到 12 家
    int MaxSlots { get { return _queueMode ? 12 : 6; } }
    public const int MaxSlotsAny = 12;   // 保存的组合最多几格（两种视图里大的那个）

    Slot AddSlot(AgentKind k)
    {
        Slot s = SlotOf(k);
        if (s != null) return s;
        if (_slots.Count >= MaxSlots) return null;
        s = new Slot();
        s.Kind = k;
        s.Hwnd = IntPtr.Zero;
        string label = k.Name;
        if (k.Cli && !string.IsNullOrEmpty(k.Workspace))
        {
            string project = Path.GetFileName(k.Workspace.TrimEnd(Path.DirectorySeparatorChar));
            if (project.Length > 14) project = project.Substring(0, 13) + "…";
            label += " · " + project;
        }
        s.TitleLbl = MkLabel(label, 0, 0, 160, 22);
        s.TitleLbl.Font = Fonts.Medium(11f);
        s.TitleLbl.AutoSize = true;
        s.StateLbl = MkLabel("未接入", 0, 0, 120, D(24));
        s.StateLbl.ForeColor = Dim;
        s.StateLbl.Chip = true;
        s.StateLbl.PadX = D(8);
        s.StateLbl.Font = new Font(Font.FontFamily, 8.5f, FontStyle.Regular);
        s.StateLbl.AutoSize = true;
        var site = new SitePanel();
        site.OwnerSlot = s;
        s.Site = site;
        s.Site.Resize += delegate { Embed.Fit(s); };
        s.LaunchBtn = MkBtn("启动 " + k.Name, 0, 0, D(150), D(38));
        AgentKind captured = k;
        s.LaunchBtn.Click += delegate { AddOrLaunch(captured); };
        s.DropBtn = MkChip();
        s.DropBtn.Click += delegate { Drop(captured); };
        s.ZoomBtn = MkChip();
        s.ZoomBtn.Click += delegate { ToggleZoom(s); };
        s.TitleLbl.DoubleClick += delegate { ToggleZoom(s); };
        s.StateLbl.DoubleClick += delegate { ToggleZoom(s); };
        s.Site.Controls.Add(s.LaunchBtn);
        Controls.Add(s.TitleLbl);
        Controls.Add(s.StateLbl);
        Controls.Add(s.Site);
        Controls.Add(s.ZoomBtn);
        Controls.Add(s.DropBtn);
        _slots.Add(s);
        WireDrag(s.TitleLbl);
        WireDrag(s.StateLbl);
        Tip(s.TitleLbl, k.Cli ? "拖动换位置 · " + Cfg.WorkspaceFor(k) : "拖动换位置 · 双击放大");
        SaveSlots();
        return s;
    }

    void ToggleSolo(Slot s)
    {
        if (s == null || _queueMode) return;
        if (_slots.Count <= 1)
        {
            _solo = null;
            return;
        }
        _solo = (_solo == s) ? null : s;
        SaveSolo();
        Relayout(true);
    }

    void OnHeaderDoubleClick(object sender, MouseEventArgs e)
    {
        for (int i = 0; i < _slots.Count; i++)
        {
            Slot slot = _slots[i];
            if (slot.Pane.Width <= 0) continue;
            Rectangle hdr = new Rectangle(slot.Pane.X, slot.Pane.Y, slot.Pane.Width, HeaderH);
            if (hdr.Contains(e.Location))
            {
                ToggleZoom(slot);
                return;
            }
        }
    }

    void Drop(AgentKind k)
    {
        Slot s = SlotOf(k);
        if (s == null) return;
        if (_solo == s) _solo = null;
        IntPtr h = s.Hwnd;
        Embed.Detach(s);
        if (h != IntPtr.Zero && Native.IsWindow(h) && !Embed.IsTopLevel(h))
        {
            s.Hwnd = h;
            s.Embedded = true;
            Watch.Note(h);
            s.StateLbl.Text = "移出失败";
            return;
        }
        RemoveSlot(s);
        Relayout(true);
    }

    // 只撤格子，不管窗口（窗口已经没了，或者调用方自己处理过了）。
    void RemoveSlot(Slot s)
    {
        if (_solo == s) _solo = null;
        Controls.Remove(s.TitleLbl);
        Controls.Remove(s.StateLbl);
        Controls.Remove(s.Site);
        Controls.Remove(s.ZoomBtn);
        Controls.Remove(s.DropBtn);
        s.TitleLbl.Dispose();
        s.StateLbl.Dispose();
        s.LaunchBtn.Dispose();
        s.Site.Dispose();
        s.ZoomBtn.Dispose();
        s.DropBtn.Dispose();
        _slots.Remove(s);
        SaveSlots();
    }

    void AddOrLaunch(AgentKind k)
    {
        Slot s = AddSlot(k);
        if (s == null) return;
        if (s.Hwnd != IntPtr.Zero && Native.IsWindow(s.Hwnd)) { Relayout(true); return; }
        if (_launching.ContainsKey(k.Id) && DateTime.Now < _launching[k.Id]) return;
        List<WinInfo> all = UnusedWindows(HostWins.Visible());
        WinInfo hit = HostWins.Find(k, all);
        if (hit != null)
        {
            s.Hwnd = hit.Handle;
            Relayout(true);
            return;
        }
        Relayout(false);
        if (!Launch.Start(k))
        {
            s.StateLbl.Text = "没找到启动入口";
            Toast(Launch.LastWhy ?? L.F("没找到 {0} 的启动入口", k.Name));
            return;
        }
        _launching[k.Id] = DateTime.Now.AddSeconds(30);
        s.StateLbl.Text = "正在启动…";
        _lockUntil = DateTime.Now.AddSeconds(k.Id == "claude" || k.Id == "codex" ? 25 : 15);
    }

    // 打开虚拟窗口时，把上次组合里没开着的桌面端一起启动 —— 跟「关闭时一并关闭桌面端」是一对。
    // 用户 2026-10-04：关的时候几家一起关了，下次第一次打开就是一屏「找不到窗口」，得一家一家点启动。
    // 只管桌面端：终端会话每开一次是一个新会话，自己挑进来的窗口也不是我们起的。
    // 按「桌面上有没有它的窗口」判，不按进程名 —— 后台常有同名的命令行进程（claude、codex）在跑。
    void LaunchClosedOnOpen()
    {
        if (_closed || !Prefs.Get("openagents", true)) return;
        int n = 0;
        foreach (Slot s in _slots.ToArray())
        {
            if (s.Custom || s.Kind.Cli) continue;
            if (s.Hwnd != IntPtr.Zero && Native.IsWindow(s.Hwnd)) continue;
            AddOrLaunch(s.Kind);
            n++;
        }
        if (n > 0) Log.W("open: 启动了 " + n + " 家没开着的桌面端");
    }

    void ClaimRunning(bool place)
    {
        List<WinInfo> all = HostWins.Visible();
        bool added = false;
        for (int i = 0; i < Catalog.All.Length; i++)
        {
            AgentKind k = Catalog.All[i];
            if (SlotOf(k) != null) continue;
            if (HostWins.Find(k, all) == null) continue;
            if (AddSlot(k) == null) break;   // 格子满了
            added = true;
        }
        // 点了按钮界面一点变化都没有，看着像坏了 —— 说一声到底有没有东西可纳。
        int before = 0;
        for (int i = 0; i < _slots.Count; i++) if (_slots[i].Hwnd != IntPtr.Zero) before++;
        Attach(all);
        int after = 0;
        for (int i = 0; i < _slots.Count; i++) if (_slots[i].Hwnd != IntPtr.Zero) after++;
        if (!added && after == before)
            Toast(_slots.Count >= MaxSlots
                ? L.F("格子已满（最多 {0} 格）", MaxSlots)
                : L.T("桌面上没有别的客户端可以纳入"));
        Relayout(place || added);
    }

    void Attach(List<WinInfo> all)
    {
        var used = new List<long>();
        for (int i = 0; i < _slots.Count; i++)
        {
            Slot s = _slots[i];
            if (s.Hwnd != IntPtr.Zero)
            {
                // 只看窗口是否还活着。最小化过的也算在，嵌入时 Restore 会拉起来；
                // 之前把 IsIconic 当「没了」，导致有过最小化的窗口永远纳不进去。
                if (!Native.IsWindow(s.Hwnd))
                {
                    s.Hwnd = IntPtr.Zero;
                    s.Embedded = false;
                }
                else
                    used.Add(s.Hwnd.ToInt64());
            }
        }
        for (int i = 0; i < _slots.Count; i++)
        {
            Slot s = _slots[i];
            if (s.Hwnd != IntPtr.Zero) continue;
            if (s.Custom) continue;   // 挑进来的那个窗口没了就是没了，不按进程名另找一个
            WinInfo hit = HostWins.Find(s.Kind, UnusedWindows(all));
            if (hit == null) continue;
            if (used.Contains(hit.Handle.ToInt64())) continue;
            s.Hwnd = hit.Handle;
            _launching.Remove(s.Kind.Id);
            used.Add(hit.Handle.ToInt64());
        }
    }

    static void EqualGrid(int n, Rectangle box, int gap, out int rows, out int cols, out Size cell)
    {
        rows = 1;
        cols = 1;
        cell = new Size(box.Width, box.Height);
        if (n <= 0 || box.Width < 40 || box.Height < 40) return;

        double best = double.MaxValue;
        int bestR = 1, bestC = n;
        for (int c = 1; c <= n; c++)
        {
            int r = (n + c - 1) / c;
            int cw = (box.Width - gap * (c + 1)) / c;
            int ch = (box.Height - gap * (r + 1)) / r;
            if (cw < 80 || ch < 80) continue;
            double aspect = (double)cw / (double)ch;
            double score = Math.Abs(Math.Log(aspect));
            score += 0.08 * (r * c - n);
            if (score < best)
            {
                best = score;
                bestC = c;
                bestR = r;
            }
        }
        cols = bestC;
        rows = bestR;
        cell = new Size(
            Math.Max(80, (box.Width - gap * (cols + 1)) / cols),
            Math.Max(80, (box.Height - gap * (rows + 1)) / rows));
    }

    void Relayout(bool place)
    {
        if (_placing) return;
        LayoutToolbar();
        Rectangle box = new Rectangle(0, ToolbarH, ClientSize.Width, Math.Max(80, ClientSize.Height - ToolbarH));
        int n = _slots.Count;
        LayoutStartCards(box, n == 0);

        if (n == 0)
        {
            _solo = null;
            ClearBook();
            _queueEmpty = Rectangle.Empty;
            Invalidate();
            return;
        }
        if (_queueMode) { LayoutQueue(box, place); return; }
        ClearBook();
        _queueEmpty = Rectangle.Empty;
        if (n <= 1) _solo = null;
        else if (_solo != null && !_slots.Contains(_solo)) _solo = null;
        if (_wantSolo != null)
        {
            if (n > 1)
                for (int i = 0; i < _slots.Count; i++)
                    if (_slots[i].Kind.Id == _wantSolo) { _solo = _slots[i]; break; }
            if (n > 0) _wantSolo = null;   // 只试到第一次有格子为止
        }

        UseLayoutFor(n);
        Rectangle[] panes = Layouts.Compute(n, _layoutIx, box, GapPx);
        if (panes.Length != n)
        {
            int rows, cols;
            Size cell;
            EqualGrid(n, box, GapPx, out rows, out cols, out cell);
            panes = new Rectangle[n];
            int i = 0;
            for (int r = 0; r < rows && i < n; r++)
            {
                int inRow = n - i;
                if (inRow > cols) inRow = cols;
                int rowW = inRow * cell.Width + (inRow + 1) * GapPx;
                int x0 = box.X + (box.Width - rowW) / 2 + GapPx;
                int y = box.Y + GapPx + r * (cell.Height + GapPx);
                for (int c = 0; c < inRow; c++)
                    panes[i++] = new Rectangle(x0 + c * (cell.Width + GapPx), y, cell.Width, cell.Height);
            }
        }
        Rectangle full = new Rectangle(box.X + GapPx, box.Y + GapPx,
            Math.Max(80, box.Width - 2 * GapPx), Math.Max(80, box.Height - 2 * GapPx));
        for (int i = 0; i < n; i++)
        {
            Slot s = _slots[i];
            if (_solo != null && s != _solo)
            {
                s.Pane = Rectangle.Empty;
                HideChrome(s);
                continue;
            }
            s.Pane = (_solo == s) ? full : panes[i];
            // 客户端窗口是方角的，四周各留一圈卡片边，圆角才露得出来。
            int inset = D(8);
            s.Content = new Rectangle(s.Pane.X + inset, s.Pane.Y + HeaderH,
                Math.Max(40, s.Pane.Width - inset * 2), Math.Max(40, s.Pane.Height - HeaderH - inset));
            PlaceChrome(s);
        }
        Invalidate();
        if (place) PlaceAll();
        SyncStack();
        if (!_closed) EnsureFocusVisible();
        if (!_closed) WriteStatus(true);
        SaveSlots();
    }

    void PlaceAll()
    {
        _placing = true;
        try
        {
            for (int i = 0; i < _slots.Count; i++)
            {
                Slot s = _slots[i];
                if (s.Hwnd == IntPtr.Zero || !Native.IsWindow(s.Hwnd) || s.Site == null) continue;
                // 队列视图里不在中间的格子也得嵌进去（垫在底下或藏着嵌），
                // 不然它的窗口就一直飘在宿主外面。网格视图的「放大」照旧只嵌看得见的（含垫在底下的）。
                if (!s.Site.Visible && !_queueMode) continue;
                if (!s.Site.IsHandleCreated) s.Site.CreateControl();
                if (!Embed.IsChildOf(s.Hwnd, s.Site.Handle))
                {
                    if (!Embed.Attach(s, s.Site.Handle))
                        s.StateLbl.Text = "嵌入失败";
                }
                else
                    Embed.Fit(s);
                // 网格放大时垫在底下的格子不显示表头（PlaceChrome 会把它当成一格正常显示）
                if (_queueMode || SlotShowing(s)) PlaceChrome(s);
            }
        }
        finally { _placing = false; }
    }
}
