// 委托单：一家 agent 请另一家办一件事，用户签了字才算数。存取全在 scripts\hooks\agenthost-tasks.cjs（格式见它开头），
// 宿主只做两件事：
//   1. 心跳里看一眼有没有新的「待批」单子：有就在状态那一行提一句、你不在宿主里时闪任务栏（跟「做完了」一样，不加红点）；
//   2. 「设置 → 委托」那一页：列出没了结的单子，待批的可以「准许 / 不准」，别的可以「撤销」。
//      签字经脚本做，带 AGENTHOST_SIGNER=host —— 签字记录里记成「经虚拟窗口签的」；
//      从命令行签的（谁都能跑那条命令）巡检会单独报出来。同一个 Windows 账号下技术上拦不住伪造，这是对账用的。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;

internal static class TaskList
{
    internal sealed class Rec
    {
        public string Id, From, To, Title, Brief, State, Result, Write, Repo, Branch, Context;
        public bool Shell, Net;
        public int Minutes, Rounds;
        public long Deadline, Updated;
        public string LastWho, LastWhat;   // 单子上最新的一条记录：谁、做了什么（谁在哪个对话里做、交了什么、验收没有）
        public bool Live;                  // 还没了结
    }

    // 还没了结的几种状态（跟脚本里的 LIVE 一样）
    static readonly HashSet<string> Live = new HashSet<string> { "draft", "pending", "approved", "taken", "returned", "delivered" };

    static List<Rec> _live = new List<Rec>();
    static long _stamp;
    static DateTime _readAt = DateTime.MinValue;
    static readonly HashSet<string> _seenPending = new HashSet<string>();
    // 上一次看到的各单状态：变成「交了」「验收过了」时提一句（用户 2026-10-15：子对话确实完成了任务，只差告知这一步我看不见）
    static readonly Dictionary<string, string> _lastState = new Dictionary<string, string>();
    static readonly List<string> _events = new List<string>();
    // 有交付 / 验收的消息你还没回到宿主看过：任务栏跟「做完了」一样闪（回到宿主就停）
    public static bool Announce;
    const double RecentHours = 24;   // 了结了的单子在委托页留多久，方便回看结果

    internal static string Root
    {
        get
        {
            string b = Environment.GetEnvironmentVariable("LOCALAPPDATA");
            if (string.IsNullOrEmpty(b)) b = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(b, "AgentHost", "tasks");
        }
    }

    internal static string ScriptPath { get { return Path.Combine(Path.GetDirectoryName(HookSetup.NotesScriptPath), "agenthost-tasks.cjs"); } }

    // 委托页列的：没了结的在前，最近 24 小时里了结的跟在后面（淡色、不带按钮）
    public static List<Rec> LiveTasks { get { return _live; } }

    // 这一拍新出的消息（交了、验收过了、退回了……），取走就清
    public static List<string> TakeEvents()
    {
        var e = new List<string>(_events);
        _events.Clear();
        return e;
    }

    // 弹过小卡的单子：同一张只弹一次（关掉了、签过了都不再弹；委托页里照样能签）
    static readonly HashSet<string> _carded = new HashSet<string>();
    public static void MarkCarded(string id) { _carded.Add(id); }

    // 下一张要弹的：还在待批、没弹过的，最早的那张
    public static Rec NextCard()
    {
        Rec best = null;
        foreach (Rec r in _live)
            if (r.State == "pending" && !_carded.Contains(r.Id) && (best == null || r.Updated < best.Updated)) best = r;
        return best;
    }

    public static int Pending
    {
        get { int n = 0; foreach (Rec r in _live) if (r.State == "pending") n++; return n; }
    }

    // 巡检（agent-bus 的 patrol.py，只做账不起模型）最近一次发现的事：没人接、没动静、签字对不上……
    // 「等你签字」不在这里重复（单子本身就列着）。一小时前的不算（巡检停了就别挂着旧话）
    public static List<string> Findings()
    {
        var list = new List<string>();
        try
        {
            string f = Path.Combine(Root, "patrol.json");
            if (!File.Exists(f) || (DateTime.UtcNow - File.GetLastWriteTimeUtc(f)).TotalHours > 1) return list;
            using (JsonDocument doc = JsonDocument.Parse(File.ReadAllText(f, Encoding.UTF8)))
            {
                JsonElement arr;
                if (!doc.RootElement.TryGetProperty("findings", out arr) || arr.ValueKind != JsonValueKind.Array) return list;
                foreach (JsonElement x in arr.EnumerateArray())
                {
                    if (Str(x, "kind") == "pending") continue;
                    string t = Str(x, "text");
                    if (t.Length > 0) list.Add(t);
                }
            }
        }
        catch { }
        return list;
    }

    // 心跳里调。目录没动过就不读（看目录的修改时间；刚变过的几秒照读，时间戳精度有限）。
    // 返回这次新冒出来的待批单子（宿主开起来时已经在等的也算一次，让你知道有单子在等）
    public static List<Rec> Refresh()
    {
        var fresh = new List<Rec>();
        try
        {
            // 由宿主先把目录建出来：商店版的桌面端起的进程在 %LOCALAPPDATA% 下新建的目录会被改放到它们私有的地方
            Directory.CreateDirectory(Root);
            long stamp = Directory.GetLastWriteTimeUtc(Root).Ticks;
            bool recent = (DateTime.UtcNow - new DateTime(stamp, DateTimeKind.Utc)).TotalSeconds < 5;
            if (stamp == _stamp && !recent && (DateTime.UtcNow - _readAt).TotalSeconds < 60) return fresh;
            _stamp = stamp;
            _readAt = DateTime.UtcNow;
            var list = new List<Rec>();
            long since = DateTimeOffset.UtcNow.AddHours(-RecentHours).ToUnixTimeMilliseconds();
            bool first = _lastState.Count == 0;
            foreach (string f in Directory.GetFiles(Root, "*.json"))
            {
                Rec r = Read(f);
                if (r == null) continue;
                r.Live = Live.Contains(r.State);
                string before;
                bool known = _lastState.TryGetValue(r.Id, out before);
                if (!first && before != r.State)
                {
                    // 宿主刚开起来时读到的那一批不算「变了」，不提
                    if (r.State == "delivered") _events.Add(L.F("{0} 交了：{1} · 设置 → 委托", HostForm.AgentNameOf(r.To), r.Title));
                    else if (r.State == "done") _events.Add(L.F("验收通过：{0}", r.Title));
                    else if (r.State == "returned") _events.Add(L.F("退回重做：{0}", r.Title));
                    else if (r.State == "expired" && known) _events.Add(L.F("超时了：{0}", r.Title));
                }
                _lastState[r.Id] = r.State;
                if (r.Live || r.Updated >= since) list.Add(r);
            }
            if (_events.Count > 0) Announce = true;
            list.Sort((a, b) => Order(a).CompareTo(Order(b)) != 0 ? Order(a).CompareTo(Order(b)) : b.Updated.CompareTo(a.Updated));
            _live = list;
            foreach (Rec r in list)
            {
                if (r.State != "pending" || _seenPending.Contains(r.Id)) continue;
                _seenPending.Add(r.Id);
                fresh.Add(r);
            }
        }
        catch { }
        return fresh;
    }

    // 待批的排最前，然后是等验收、做着的；了结了的排最后
    static int Order(Rec r)
    {
        switch (r.State)
        {
            case "pending": return 0;
            case "delivered": return 1;
            case "taken": return 2;
            case "returned": return 3;
            case "approved": return 4;
            case "draft": return 5;
            default: return 6;
        }
    }

    static Rec Read(string file)
    {
        string name = Path.GetFileNameWithoutExtension(file);
        if (name.Length < 6 || name.Length > 32) return null;
        try
        {
            using (JsonDocument doc = JsonDocument.Parse(File.ReadAllText(file, Encoding.UTF8)))
            {
                JsonElement o = doc.RootElement;
                var r = new Rec
                {
                    Id = Str(o, "id"), From = Str(o, "from"), To = Str(o, "to"), Title = Str(o, "title"),
                    Brief = Str(o, "brief"), State = Str(o, "state"), Result = Str(o, "result"),
                    Deadline = Num(o, "deadline"), Updated = Num(o, "updated"),
                };
                JsonElement s;
                if (o.TryGetProperty("scope", out s) && s.ValueKind == JsonValueKind.Object)
                {
                    r.Write = Str(s, "write"); r.Repo = Str(s, "repo"); r.Branch = Str(s, "branch");
                    r.Shell = Bool(s, "shell"); r.Net = Bool(s, "net");
                    r.Minutes = (int)Num(s, "minutes"); r.Rounds = (int)Num(s, "rounds");
                    r.Context = Str(s, "context");
                }
                JsonElement log;
                if (o.TryGetProperty("log", out log) && log.ValueKind == JsonValueKind.Array && log.GetArrayLength() > 0)
                {
                    JsonElement last = log[log.GetArrayLength() - 1];
                    r.LastWho = Str(last, "who");
                    r.LastWhat = Str(last, "what");
                }
                return string.IsNullOrEmpty(r.Id) ? null : r;
            }
        }
        catch { return null; }
    }

    static string Str(JsonElement o, string k)
    {
        JsonElement v;
        return o.TryGetProperty(k, out v) && v.ValueKind == JsonValueKind.String ? v.GetString() : "";
    }
    static long Num(JsonElement o, string k)
    {
        JsonElement v; long n;
        return o.TryGetProperty(k, out v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out n) ? n : 0;
    }
    static bool Bool(JsonElement o, string k)
    {
        JsonElement v;
        return o.TryGetProperty(k, out v) && v.ValueKind == JsonValueKind.True;
    }

    // 准许 / 不准 / 撤销：经脚本做（它负责改状态、记签字、给对方留言、叫醒对方）。返回出错的话，成功返回 null
    public static string Act(string verb, string id)
    {
        string node = HookSetup.Node();
        if (node == null) return L.T("没找到 Node.js");
        if (!File.Exists(ScriptPath)) return L.T("委托单的脚本不在，点一次「重新安装钩子」");
        try
        {
            var psi = new ProcessStartInfo(node)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardError = true, RedirectStandardOutput = true,
                StandardErrorEncoding = Encoding.UTF8, StandardOutputEncoding = Encoding.UTF8,
            };
            psi.ArgumentList.Add(ScriptPath);
            psi.ArgumentList.Add(verb);
            psi.ArgumentList.Add(id);
            psi.ArgumentList.Add("--as");
            psi.ArgumentList.Add("user");
            psi.Environment["AGENTHOST_SIGNER"] = "host";
            using (Process p = Process.Start(psi))
            {
                string err = p.StandardError.ReadToEnd();
                p.StandardOutput.ReadToEnd();
                if (!p.WaitForExit(15000)) { try { p.Kill(); } catch { } return L.T("脚本没有回应"); }
                _stamp = 0;   // 下一拍重读
                return p.ExitCode == 0 ? null : (err.Trim().Length > 0 ? err.Trim() : L.T("没改成"));
            }
        }
        catch (Exception ex) { return ex.Message; }
    }

    // 「2026-10-16 18:00」：委托单里的钟点一律按北京时间写（各家说的钟点都是北京时间）
    public static string When(long ms)
    {
        if (ms <= 0) return "";
        DateTime t = DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime.AddHours(8);
        return t.ToString("MM-dd HH:mm");
    }

    public static string ScopeLine(Rec r)
    {
        string w = r.Write == "workdir" ? L.T("只写临时目录") : r.Write == "branch" ? L.F("写分支 {0}", r.Branch) : L.T("只读");
        var parts = new List<string> { w };
        if (r.Shell) parts.Add(L.T("能跑命令"));
        if (r.Net) parts.Add(L.T("能联网"));
        if (!string.IsNullOrEmpty(r.Repo)) parts.Add(r.Repo);
        if (r.Context == "main") parts.Add(L.T("排进主对话"));
        else if (r.Context == "fork") parts.Add(L.T("分叉子对话"));
        else if (r.Context == "fresh") parts.Add(L.T("全新实例"));
        return string.Join(" · ", parts);
    }

    public static string LastLine(Rec r)
    {
        if (string.IsNullOrEmpty(r.LastWhat)) return "";
        string who = r.LastWho == "user" ? L.T("你") : r.LastWho == "patrol" ? L.T("巡检") : HostForm.AgentNameOf(r.LastWho);
        return who + "：" + r.LastWhat.Replace("\r", "").Replace("\n", " ");
    }

    public static string StateName(string s)
    {
        switch (s)
        {
            case "draft": return L.T("草稿");
            case "pending": return L.T("待批");
            case "approved": return L.T("已批，还没接");
            case "taken": return L.T("在做");
            case "returned": return L.T("退回重做");
            case "delivered": return L.T("交了，等验收");
            case "done": return L.T("完成");
            case "denied": return L.T("不准");
            case "cancelled": return L.T("撤销");
            case "expired": return L.T("超时");
            default: return s;
        }
    }
}

internal sealed partial class HostForm
{
    TasksPanel _tasksPanel;

    // 心跳里：有新的待批单子就在状态那一行提一句（你不在宿主里时任务栏照「做完了」那样闪，见 Pulse 里 SyncFlash）
    // 提示都能点：点了打开委托页（用户 2026-10-15：签委托只能进「设置 → 委托」太麻烦）
    void SyncTasks()
    {
        List<TaskList.Rec> fresh = TaskList.Refresh();
        List<string> events = TaskList.TakeEvents();
        if (_tasksPanel != null && !_tasksPanel.IsDisposed && (fresh.Count > 0 || events.Count > 0)) _tasksPanel.Reload();
        if (fresh.Count == 0 && events.Count > 0)
        {
            ToastAct(events.Count == 1 ? events[0] : L.F("{0} 张委托有新进展 · 设置 → 委托", events.Count), ShowTasks, 10);
            return;
        }
        if (fresh.Count == 0) return;
        TaskList.Rec r = fresh[0];
        ToastAct(fresh.Count == 1
            ? L.F("{0} 想请 {1} 办事，等你签字 · 点这里看", AgentName(r.From), AgentName(r.To))
            : L.F("{0} 张委托等你签字 · 点这里看", fresh.Count), ShowTasks, 15);
        ShowTaskCard();
    }

    // ---- 状态那一行的提示：带动作的那几句点得动 ----

    Rectangle ToastRect()
    {
        int tw = _titleW > 0 ? _titleW : TitleBlockWidth();
        return new Rectangle(_pill.X + BarPad, _pill.Y, tw + D(4), _pill.Height);
    }

    internal bool ToastClickable(Point client)
    {
        return _toastAction != null && _toast != null && DateTime.Now < _toastUntil && ToastRect().Contains(client);
    }

    bool _toastHand;   // 手形是不是我们设的（别动别处设的光标）

    protected override void OnMouseMove(MouseEventArgs e)
    {
        bool on = ToastClickable(e.Location);
        if (on != _toastHand)
        {
            _toastHand = on;
            Cursor = on ? Cursors.Hand : Cursors.Default;
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left || !ToastClickable(e.Location)) return;
        MethodInvoker act = _toastAction;
        _toast = null;
        _toastAction = null;
        Invalidate(_pill);
        if (act != null) BeginInvoke(act);
    }

    // ---- 新的待批单子：弹一张小卡，在卡上直接签 ----
    // 不抢焦点（WS_EX_NOACTIVATE）：你正在哪一格里打字，点卡上的按钮也不会把焦点拿走。
    // 同一张单子只弹一次（TaskList 记着弹过的）；签完一张，还有待批的就换下一张，没有就收起。

    TaskCard _taskCard;

    void ShowTaskCard()
    {
        TaskList.Rec next = TaskList.NextCard();
        if (next == null) { if (_taskCard != null && !_taskCard.IsDisposed) _taskCard.Close(); return; }
        if (_taskCard == null || _taskCard.IsDisposed)
        {
            _taskCard = new TaskCard(this);
            _taskCard.FormClosed += delegate { _taskCard = null; };
        }
        _taskCard.Bind(next);
        _taskCard.Location = TaskCardSpot(_taskCard.Size);
        if (!_taskCard.Visible) _taskCard.Show(this);
        _taskCard.Invalidate();
    }

    // 小卡贴在中间那格的右下角（用户 2026-10-15 截图定的：「这个位置我觉得最合适」「别的 agent 这个地方也是空的」）。
    // 试过的两处都不行：宿主右上角盖住格子里各家自己的按钮（– □ ✕、工具条）和队列右边那排签；
    // 状态那一行提示的正下方盖住中间那格的标题栏、菜单和左边那排签。各家右下角多半是空地（输入框在中下、滚动条贴着最右边）。
    // 队列视图认中间那张；放大时认放大的那格；网格视图没有「中间那格」，用整个格子区的右下角。超出屏幕就往回收。
    internal Point TaskCardSpot(Size size)
    {
        Slot front = _queueMode ? QueueFront : _solo;
        Rectangle area = front != null ? front.Content : Rectangle.Empty;
        // 认出来的那一格不在窗口里（队列里排第一的不一定就是中间露着的那张，藏着的格子停在 -20000 那么远）：
        // 换成窗口里露着的、最大的那一格。2026-10-15 真机上小卡就这样弹到了屏幕外，用户以为没弹
        if (area.IsEmpty || !ClientRectangle.Contains(area))
        {
            area = Rectangle.Empty;
            foreach (Slot s0 in _slots)
            {
                Rectangle c = s0.Content;
                if (c.IsEmpty || !ClientRectangle.Contains(c) || s0.Site == null || !s0.Site.Visible) continue;
                if (c.Width * c.Height > area.Width * area.Height) area = c;
            }
        }
        if (area.IsEmpty)
            area = Rectangle.FromLTRB(ClientRectangle.Left, ToolbarH + TodoStripH, ClientRectangle.Right, ClientRectangle.Bottom);
        Rectangle s = RectangleToScreen(area);
        // 右边让出滚动条，下边让出底下那一溜（各家的状态条 / 滚动条的箭头）
        var p = new Point(s.Right - size.Width - D(28), s.Bottom - size.Height - D(28));
        // 四边都收进宿主所在的那块屏幕（原先只收了右、下两边，算错时左上能跑到屏幕外）
        Rectangle work = Screen.FromControl(this).WorkingArea;
        p.X = Math.Max(work.Left, Math.Min(p.X, work.Right - size.Width));
        p.Y = Math.Max(work.Top, Math.Min(p.Y, work.Bottom - size.Height));
        return p;
    }

    internal sealed class TaskCard : Form
    {
        readonly HostForm _host;
        TaskList.Rec _r;
        sealed class Item { public Rectangle R; public string What; }
        readonly List<Item> _items = new List<Item>();
        Item _hot;
        string _note;

        int D(int v) { return _host.D(v); }
        int Pad { get { return D(14); } }
        int W { get { return D(340); } }

        public TaskCard(HostForm host)
        {
            _host = host;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            DoubleBuffered = true;
            BackColor = ShellStyles.Surface;
            Font = host.Font;
            ClientSize = new Size(W, D(100));
        }

        public TaskList.Rec Task { get { return _r; } }

        public void Bind(TaskList.Rec r)
        {
            _r = r;
            _note = null;
            TaskList.MarkCarded(r.Id);
            Relayout();
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ClassStyle |= 0x00020000;          // CS_DROPSHADOW
                cp.ExStyle |= 0x08000000 | 0x00000080; // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            int round = 2;
            try { Native.DwmSetInt(Handle, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref round, 4); } catch { }
        }

        // 点它也不激活（不然焦点就从格子里跑到卡上来了）
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0021 /* WM_MOUSEACTIVATE */) { m.Result = (IntPtr)3; /* MA_NOACTIVATE */ return; }
            base.WndProc(ref m);
        }

        // 只留三样（用户 2026-10-15：「能不能像 Claude 的选择单一样简洁明了，目前还是太多无用信息了」）：
        //   一句问话（「让 Codex 只读审查 wait_notes.py？」，点它打开委托页看说明、边界、截止）、
        //   一行小字（谁请的 · 能动什么）、不准 / 准许。说明摘要、截止、长串边界、「看全部委托」都拿掉了，委托页里有。
        int _askH;

        string Ask()
        {
            return L.F("让 {0} {1}？", AgentName(_r.To), _r.Title.TrimEnd('？', '?', '。', '.'));
        }

        void Relayout()
        {
            _items.Clear();
            _hot = null;
            int textW = W - Pad * 2 - D(24);
            using (var bold = Fonts.Medium(Font.Size))
            {
                Size one = TextRenderer.MeasureText("中", bold);
                Size need = TextRenderer.MeasureText(Ask(), bold, new Size(textW, 0), TextFormatFlags.WordBreak);
                _askH = Math.Min(one.Height * 2, Math.Max(one.Height, need.Height)) + D(2);
            }
            // 小字跟按钮同一行（左小字、右按钮），像 Claude 的选择框：一句问话 + 一排按钮
            int h = Pad + _askH + D(10) + D(28) + Pad + (_note != null ? D(20) : 0);
            ClientSize = new Size(W, h);
            int by = h - Pad - D(28);
            _items.Add(new Item { R = new Rectangle(W - Pad - D(64), by, D(64), D(28)), What = "approve" });
            _items.Add(new Item { R = new Rectangle(W - Pad - D(64) - D(8) - D(56), by, D(56), D(28)), What = "deny" });
            _items.Add(new Item { R = new Rectangle(Pad, Pad, textW, _askH), What = "all" });
            _items.Add(new Item { R = new Rectangle(W - D(6) - D(24), D(6), D(24), D(24)), What = "close" });
            Invalidate();
        }

        Item Hit(Point p)
        {
            foreach (Item it in _items) if (it.R.Contains(p)) return it;
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
            if (e.Button != MouseButtons.Left || _r == null) return;
            Item it = Hit(e.Location);
            if (it == null) return;
            HostForm host = _host;
            switch (it.What)
            {
                case "close":
                    Close();
                    return;
                case "all":
                    Close();
                    host.BeginInvoke((MethodInvoker)host.ShowTasks);
                    return;
            }
            Cursor = Cursors.WaitCursor;
            string err;
            try { err = TaskList.Act(it.What, _r.Id); }
            finally { Cursor = Cursors.Default; }
            if (err != null) { _note = err; Relayout(); return; }
            host.Toast(it.What == "approve" ? L.F("准许了：{0}", _r.Title) : L.F("没准：{0}", _r.Title));
            TaskList.Refresh();
            TaskList.Rec next = TaskList.NextCard();
            if (next == null) Close();
            else Bind(next);
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
            using (var pen = new Pen(Theme.Brand))
                g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
            if (_r == null) return;
            using (var bold = Fonts.Medium(Font.Size))
            using (var small = new Font(Font.FontFamily, Font.Size * 0.86f))
            {
                Item ask = _items.Find(i => i.What == "all");
                if (ask != null)
                    using (var sf = new StringFormat { Trimming = StringTrimming.EllipsisWord })
                    using (var ink = new SolidBrush(ask == _hot ? Theme.Brand : Theme.Ink))
                        g.DrawString(Ask(), bold, ink, ask.R, sf);
                // 一行小字：谁请的 · 能动什么（· 还有几张等着）
                string w = _r.Write == "workdir" ? L.T("只写临时目录") : _r.Write == "branch" ? L.T("写分支") : L.T("只读");
                int others = Math.Max(0, TaskList.Pending - 1);
                string sub = L.F("{0} 请的", AgentName(_r.From)) + " · " + w + (others > 0 ? L.F(" · 还有 {0} 张", others) : "");
                Item deny = _items.Find(i => i.What == "deny");
                int sy = deny != null ? deny.R.Y : Pad + _askH + D(10);
                using (var sf = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap, LineAlignment = StringAlignment.Center })
                using (var dim = new SolidBrush(Theme.Dim))
                    g.DrawString(sub, small, dim, new RectangleF(Pad, sy, (deny != null ? deny.R.X : W) - Pad - D(8), D(28)), sf);
                if (_note != null)
                    Theme.Text(g, _note, small, Color.FromArgb(255, 217, 48, 37), new RectangleF(Pad, Pad + _askH + D(4), W - Pad * 2, D(18)), StringAlignment.Near);

                foreach (Item it in _items)
                {
                    bool hot = it == _hot;
                    if (it.What == "all") continue;
                    if (it.What == "close")
                    {
                        if (hot)
                            using (GraphicsPath gp = Theme.Round(it.R, D(6)))
                            using (var br = new SolidBrush(ShellStyles.Hover)) g.FillPath(br, gp);
                        Theme.Text(g, "✕", small, Theme.Dim, it.R, StringAlignment.Center);
                        continue;
                    }
                    bool primary = it.What == "approve";
                    using (GraphicsPath gp = Theme.Round(it.R, D(6)))
                    {
                        if (primary)
                            using (var br = new SolidBrush(hot ? ControlPaint.Light(Theme.Brand, 0.1f) : Theme.Brand)) g.FillPath(br, gp);
                        else
                            using (var br = new SolidBrush(hot ? ShellStyles.Hover : Color.Transparent))
                            using (var pen = new Pen(ShellStyles.Border))
                            { g.FillPath(br, gp); g.DrawPath(pen, gp); }
                    }
                    Theme.Text(g, primary ? L.T("准许") : L.T("不准"), Font, primary ? Theme.OnBrand : Theme.Ink, it.R, StringAlignment.Center);
                }
            }
        }
    }

    internal static string AgentNameOf(string id) { return AgentName(id); }

    static string AgentName(string id)
    {
        AgentKind k = Catalog.ById(id);
        return k != null ? k.Name : id;
    }

    void ShowTasks()
    {
        if (_tasksPanel != null && !_tasksPanel.IsDisposed) { _tasksPanel.Close(); return; }
        _menuOpen = true;
        IntPtr giveBack = _lastFocused;
        TaskList.Refresh();
        var p = new TasksPanel(this);
        _tasksPanel = p;
        p.FormClosed += delegate
        {
            _menuOpen = false;
            _tasksPanel = null;
            GiveBackAfterPopup(giveBack);
        };
        p.Location = _settings.PointToScreen(new Point(0, _settings.Height + D(6)));
        p.Show(this);
        p.Activate();
    }

    // 「委托」页：跟设置页同一种弹出面板。一单一块：谁请谁、标题、说明的头两行、边界、截止；
    // 待批的右下角「准许」「不准」，别的「撤销」。失去焦点就关；Esc 关闭。
    internal sealed class TasksPanel : Form, IHostPopup
    {
        readonly HostForm _host;
        sealed class Item { public Rectangle R; public string What; public TaskList.Rec Task; }
        readonly List<Item> _items = new List<Item>();
        readonly List<KeyValuePair<Rectangle, TaskList.Rec>> _cards = new List<KeyValuePair<Rectangle, TaskList.Rec>>();
        Item _hot;
        string _note;     // 上一次操作的结果（出错时写在底下）
        int _more;        // 放不下、没画出来的还有几单
        List<string> _findings = new List<string>();   // 巡检发现的事，画在标题下面
        const int MaxFindings = 3;

        const int MaxCards = 6;
        int D(int v) { return _host.D(v); }
        int Pad { get { return D(16); } }
        int W { get { return D(460); } }

        public TasksPanel(HostForm host)
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

        public void Reload() { Relayout(); }

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
            if (!_busy && !_closing && !IsDisposed) Close();
        }
        public void DismissForSlotClick() { if (!_busy && !_closing && !IsDisposed) Close(); }   // 点进了某一格（HostForm.Popups.cs）
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

        void Relayout()
        {
            _items.Clear();
            _cards.Clear();
            _hot = null;
            int inner = W - Pad * 2;
            int y = D(46);
            _findings = TaskList.Findings();
            if (_findings.Count > MaxFindings) _findings = _findings.GetRange(0, MaxFindings);
            y += _findings.Count * D(20) + (_findings.Count > 0 ? D(8) : 0);
            List<TaskList.Rec> all = TaskList.LiveTasks;
            int shown = Math.Min(all.Count, MaxCards);
            _more = all.Count - shown;
            for (int i = 0; i < shown; i++)
            {
                TaskList.Rec r = all[i];
                // 待批的多两行说明；别的多一行「最新动态」（谁在哪个对话里做、交了什么、验收没有）
                int h = D(r.State == "pending" ? 128 : 116);
                var card = new Rectangle(Pad, y, inner, h);
                _cards.Add(new KeyValuePair<Rectangle, TaskList.Rec>(card, r));
                int by = card.Bottom - D(10) - D(30);
                if (r.State == "pending")
                {
                    _items.Add(new Item { R = new Rectangle(card.Right - D(10) - D(76), by, D(76), D(30)), What = "approve", Task = r });
                    _items.Add(new Item { R = new Rectangle(card.Right - D(10) - D(76) - D(8) - D(64), by, D(64), D(30)), What = "deny", Task = r });
                }
                else if (r.Live)
                    _items.Add(new Item { R = new Rectangle(card.Right - D(10) - D(64), by, D(64), D(30)), What = "cancel", Task = r });
                y += h + D(10);
            }
            if (all.Count == 0) y += D(40);
            if (_more > 0) y += D(24);
            if (_note != null) y += D(40);
            y += D(10);
            ClientSize = new Size(W, y);
            Rectangle work = Screen.FromPoint(Location).WorkingArea;
            if (Bottom > work.Bottom) Top = Math.Max(work.Top, work.Bottom - Height);
            if (Right > work.Right) Left = Math.Max(work.Left, work.Right - Width);
            Invalidate();
        }

        Item Hit(Point p)
        {
            foreach (Item it in _items) if (it.R.Contains(p)) return it;
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
            _busy = true;
            Cursor = Cursors.WaitCursor;
            string err;
            try { err = TaskList.Act(it.What, it.Task.Id); }
            finally { Cursor = Cursors.Default; _busy = false; }
            _note = err;
            TaskList.Refresh();
            if (err == null)
                _host.Toast(it.What == "approve" ? L.F("准许了：{0}", it.Task.Title)
                          : it.What == "deny" ? L.F("没准：{0}", it.Task.Title)
                          : L.F("撤销了：{0}", it.Task.Title));
            Activate();
            Native.SetFocus(Handle);
            Relayout();
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
                Theme.Text(g, L.T("委托"), title, Theme.Ink, new RectangleF(Pad, 0, Width - Pad * 2, D(44)), StringAlignment.Near);
                int pending = TaskList.Pending;
                if (pending > 0)
                    Theme.Text(g, L.F("{0} 张等你签字", pending), small, Theme.Dim, new RectangleF(Pad, 0, Width - Pad * 2, D(44)), StringAlignment.Far);
                int top = D(46);
                foreach (string f in _findings)
                {
                    using (var sf = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
                    using (var brush = new SolidBrush(Color.FromArgb(255, 176, 96, 0)))
                        g.DrawString("⚠ " + f, small, brush, new RectangleF(Pad, top, Width - Pad * 2, D(20)), sf);
                    top += D(20);
                }
                if (_findings.Count > 0) top += D(8);
                if (_cards.Count == 0)
                    Theme.Text(g, L.T("没有没了结的委托。各家 agent 用 task_new 提交的会出现在这里，等你签字。"), small, Theme.Dim,
                        new RectangleF(Pad, top, Width - Pad * 2, D(40)), StringAlignment.Near);

                foreach (var kv in _cards)
                {
                    Rectangle c = kv.Key;
                    TaskList.Rec r = kv.Value;
                    // 了结了的（最近 24 小时，留着回看结果）：不填底色、标题也用淡色
                    using (GraphicsPath gp = Theme.Round(c, D(8)))
                    using (var br = new SolidBrush(Color.FromArgb(r.Live ? (Theme.Light ? 8 : 20) : 0, Theme.Light ? Color.Black : Color.White)))
                    using (var pen = new Pen(r.State == "pending" ? Theme.Brand : ShellStyles.Border))
                    { g.FillPath(br, gp); g.DrawPath(pen, gp); }
                    int x = c.X + D(12), w = c.Width - D(24), yy = c.Y + D(8);
                    // 第一行：谁 → 谁 · 状态（右边是截止）
                    string who = AgentName(r.From) + " → " + AgentName(r.To) + " · " + TaskList.StateName(r.State);
                    Theme.Text(g, who, small, Theme.Dim, new RectangleF(x, yy, w, D(20)), StringAlignment.Near);
                    if (r.Deadline > 0)
                        Theme.Text(g, L.F("截止 {0}", TaskList.When(r.Deadline)), small, Theme.Dim, new RectangleF(x, yy, w, D(20)), StringAlignment.Far);
                    yy += D(20);
                    Theme.Text(g, r.Title, bold, r.Live ? Theme.Ink : Theme.Dim, new RectangleF(x, yy, w, D(24)), StringAlignment.Near);
                    yy += D(24);
                    if (r.State == "pending")
                    {
                        // 说明的头两行：签字前看一眼它到底要干什么
                        string b = (r.Brief ?? "").Replace("\r", "").Replace("\n", " ");
                        using (var sf = new StringFormat { Trimming = StringTrimming.EllipsisCharacter })
                        using (var brush = new SolidBrush(Theme.Dim))
                            g.DrawString(b, small, brush, new RectangleF(x, yy, w, D(36)), sf);
                        yy += D(36);
                    }
                    else
                    {
                        // 最新动态：谁在哪个对话里做、交了什么、验收没有 —— 不用去各家的窗口里翻（用户 2026-10-15）
                        string line = TaskList.LastLine(r);
                        if (line.Length > 0)
                            using (var sf = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
                            using (var brush = new SolidBrush(r.State == "delivered" ? Theme.Ink : Theme.Dim))
                                g.DrawString(line, small, brush, new RectangleF(x, yy, w, D(20)), sf);
                    }
                    // 最后一行左边：边界
                    Theme.Text(g, TaskList.ScopeLine(r), small, Theme.Dim, new RectangleF(x, c.Bottom - D(10) - D(30), w - D(160), D(30)), StringAlignment.Near);
                }

                foreach (Item it in _items)
                {
                    bool hot = it == _hot;
                    bool primary = it.What == "approve";
                    using (GraphicsPath gp = Theme.Round(it.R, D(6)))
                    {
                        if (primary)
                            using (var br = new SolidBrush(hot ? ControlPaint.Light(Theme.Brand, 0.1f) : Theme.Brand)) g.FillPath(br, gp);
                        else
                            using (var br = new SolidBrush(hot ? ShellStyles.Hover : Color.Transparent))
                            using (var pen = new Pen(ShellStyles.Border))
                            { g.FillPath(br, gp); g.DrawPath(pen, gp); }
                    }
                    string label = it.What == "approve" ? L.T("准许") : it.What == "deny" ? L.T("不准") : L.T("撤销");
                    Theme.Text(g, label, Font, primary ? Theme.OnBrand : Theme.Ink, it.R, StringAlignment.Center);
                }

                int fy = _cards.Count > 0 ? _cards[_cards.Count - 1].Key.Bottom + D(10) : D(90);
                if (_more > 0)
                {
                    Theme.Text(g, L.F("还有 {0} 单没列出来（node agenthost-tasks.cjs list）", _more), small, Theme.Dim, new RectangleF(Pad, fy, Width - Pad * 2, D(22)), StringAlignment.Near);
                    fy += D(24);
                }
                if (_note != null)
                    Theme.Text(g, _note, small, Color.FromArgb(255, 217, 48, 37), new RectangleF(Pad, fy, Width - Pad * 2, D(40)), StringAlignment.Near);
            }
        }
    }
}
