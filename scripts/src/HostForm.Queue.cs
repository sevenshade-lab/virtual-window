// 队列视图：跟四宫格并列的第二种显示方式（思路来自 github.com/ZIXT233/Que）。
// 一次只看一个，像翻一本书 —— 中间是轮到你的那个会话；排队等你的是它后面的书页，在右边各露一条页边、
// 贴一个索引签（PageBook.cs，点签翻到那一页）；在干活的（你回过、翻过去的）是左边从中间那张伸出来的签。
//
// 中间那张是「粘住」的：你回复了它（变成 working）它也留在中间，好让你看它回；
// 只有新情况才自动换人 —— 有人在问你（asking，最优先），或者中间那张在干活时有人刚做完、你还没看过。
// 早就看过、一直挂在队列里的老会话不抢中间（用户 2026-09-25：回复完 Claude 画面被换成 23 小时前的 Codex）。
// 最早照抄 Que 的「回复完就换下一个」，对边看边聊的用法太突兀。
//
// 所有格子的内容区都是中间那张大卡片的尺寸，不在中间的只是把 Site 藏起来（跟「放大某格」一样）：
// 轮到谁只是换哪个 Site 显示，客户端不用重新排版，换得快、不闪。
// Site 藏起来不会被当成「客户端自己藏起来」：那边看的是窗口自己的 WS_VISIBLE。

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;

internal sealed partial class HostForm
{
    bool _queueMode;
    // 排队顺序：先来先到。只在「谁不再干活了」时追加到队尾，「跳过」把队头挪到队尾。
    readonly List<Slot> _waitQ = new List<Slot>();
    string _queueSig = "";
    // 网格 ⇄ 队列、放大 ⇄ 还原的变形动画里，跟着动的书页只算最前面这几张（空白卡片）；再多的等浮层揭开直接出现
    const int QueuePeeks = 2;
    // 右边的书页：排在中间那张后面等你的，每家一页，按排队顺序（几何见 PageBook）
    readonly List<Slot> _pages = new List<Slot>();
    PageBook.Geometry _book;
    // 等你的那几家在右边的索引签上，在干活的在左边的签上。prefs.txt 里写 queue.railwaiting=1 改成左边一条侧栏、
    // 两种都列（那样等你的左右各出现一次；用户 2026-10-12 看示意图时先选了不重复的这种）
    static bool RailWaiting { get { return Prefs.Get("queue.railwaiting", false); } }
    // 换人时中间那张像书页一样翻过去；queue.pageturn=0 退回原来的淡入淡出
    static bool PageTurn { get { return Prefs.Get("queue.pageturn", true); } }
    // 队列里的「放大」：中间那张铺满整个内容区，左边的签和右边的书页先收起。
    // 以前这个位置是「完成」（归档，不再排队）—— 归档后点它的卡片也上不了中间，用户以为 Codex 点不动了；
    // 2026-09-27 用户定：去掉「完成」，换成跟网格一样的「放大 / 还原」。
    bool _queueZoom;
    Slot _lastQueueFront;
    bool _queuePending;   // 因为你在打字推迟了的换人，心跳里接着判

    // 粘在中间的那张（见文件头）
    Slot _sticky;
    // 中间那张是你从侧栏点上来的：在你回它（它变 working）之前，在问你的不来抢。
    // 以前 asking 每次重排都抢回中间，有一家一直在问时侧栏点谁都被拽回去（用户 2026-09-28：点别的 agent 切不过去）。
    bool _manualPin;
    // 点上来时它可能正在干活：得先看到它闲下来过一次，之后再变 working 才算「你回了它」
    bool _pinSawIdle;

    Slot QueueFront
    {
        get
        {
            if (_sticky != null && _slots.Contains(_sticky)) return _sticky;
            return _waitQ.Count > 0 ? _waitQ[0] : null;
        }
    }

    // 没人粘在中间时选谁：宿主刚开（每次换版都会重开）先恢复上次中间那张；
    // 没记录就放你最近点进去看过的那一家；都没有才是队首 ——
    // 以前一重开就是队首那条早看过的老会话（用户：「进来的时候依然是 Codex 在首页」）。
    bool _frontRestored;

    Slot StartFront()
    {
        if (!_frontRestored)
        {
            _frontRestored = true;
            string id = Prefs.GetStr("queue.front", "");
            Slot saved = _slots.Find(x => x.Kind.Id == id);
            if (saved != null) return saved;
            Slot recent = null;
            DateTime best = DateTime.MinValue;
            foreach (Slot x in _slots)
            {
                DateTime t = Seen.Of(x.Kind.Id);
                if (t > best) { best = t; recent = x; }
            }
            if (recent != null && best > DateTime.MinValue) return recent;
        }
        return _waitQ.Count > 0 ? _waitQ[0] : null;
    }

    static bool NewNews(Slot s) { return s.AgentState == "asking" || (s.AgentState == "done" && s.DoneUnseen); }

    static bool Busy(Slot s) { return s.AgentState == "working"; }

    // 按最新状态整理排队顺序。返回 true = 中间、队列或侧栏有变化，得重排。
    bool SyncQueue()
    {
        for (int i = _waitQ.Count - 1; i >= 0; i--)
            if (!_slots.Contains(_waitQ[i]) || Busy(_waitQ[i])) _waitQ.RemoveAt(i);
        var fresh = new List<Slot>();
        foreach (Slot s in _slots)
            if (!Busy(s) && !_waitQ.Contains(s)) fresh.Add(s);
        // 同一拍里新来的几个：先做完的排前面（done 的秒数大 = 做完得早），没状态的垫后
        fresh.Sort(delegate(Slot a, Slot b)
        {
            int ra = a.AgentState == "done" ? 0 : 1, rb = b.AgentState == "done" ? 0 : 1;
            if (ra != rb) return ra.CompareTo(rb);
            if (ra == 0) return b.AgentSecs.CompareTo(a.AgentSecs);
            return _slots.IndexOf(a).CompareTo(_slots.IndexOf(b));
        });
        _waitQ.AddRange(fresh);
        // 在问你（出选择题 / 等批准计划）的插到队首：它停在半路，你不答它就一直卡着。
        // 多个都在问就按原来的先后。
        var askers = _waitQ.FindAll(q => q.AgentState == "asking");
        if (askers.Count > 0)
        {
            _waitQ.RemoveAll(q => q.AgentState == "asking");
            _waitQ.InsertRange(0, askers);
        }

        // 谁粘在中间：在问你的最优先；中间那张在干活（你已经回了）或空着时，刚做完没看过的顶上；
        // 其余情况原样不动。
        if (_sticky != null && !_slots.Contains(_sticky)) _sticky = null;
        if (_manualPin)
        {
            if (_sticky == null) _manualPin = false;
            else if (!Busy(_sticky)) _pinSawIdle = true;
            else if (_pinSawIdle) _manualPin = false;
        }
        if (askers.Count > 0 && !_manualPin && (_sticky == null || _sticky.AgentState != "asking")) _sticky = askers[0];
        else if (_sticky == null || Busy(_sticky))
        {
            Slot news = fresh.Find(NewNews);
            if (news != null) _sticky = news;
            else if (_sticky == null) _sticky = StartFront();
        }

        var sb = new StringBuilder();
        Slot front = QueueFront;
        sb.Append(front == null ? "-" : front.Kind.Id).Append('|');
        foreach (Slot s in _waitQ) sb.Append(s.Kind.Id).Append(',');
        sb.Append('|');
        foreach (Slot s in _slots) if (Busy(s)) sb.Append(s.Kind.Id).Append(',');
        sb.Append('|').Append(_queueZoom ? 'Z' : '-');
        string sig = sb.ToString();
        if (sig == _queueSig) return false;
        _queueSig = sig;
        return true;
    }

    // 侧栏（只有 queue.railwaiting=1 时才有）是一条窄图标栏（照 Codex 的左栏）：每家一个方块，只有图标和状态灯，
    // 名字悬停才出。以前是 232 宽的名片，用户 2026-09-28 嫌占地方。
    int SideW { get { return D(60); } }
    int ChipH { get { return D(46); } }
    // 排队和在干活两组之间的分隔线（替掉原来的两个小标题）
    Rectangle _railDivider;
    // 鼠标停在哪个方块上（画浅底 + 出名字）
    Slot _railHot;

    // 左边在干活的那几家是从中间那张左边伸出来的签；侧栏两种都列（queue.railwaiting）时是侧栏里的方块。
    // （2026-10-12 之前平常也是侧栏。做成签先是试用开关，用户在真宿主上用过说可以，定下来。）
    bool LeftTabsOn { get { return !RailWaiting; } }

    Rectangle QueueFrontRect(Rectangle box)
    {
        // 左边只留签伸出来的那一段：签离窗口左边一道间隔，跟右边的签离窗口右边一样。
        // （试用时第一版照侧栏留着 60 加两道间隔，签和窗口左边之间空着 42。用户 2026-10-12 在真宿主上看了说
        //   「左边的签离电脑最左边框还有一段距离，这段距离不能浪费了，可以把 agent 主页面的面积扩的更大」。）
        // 跟右边一样，这个宽度不跟着有几家在干活变（没人在干活也留着）：它一变所有客户端都得重排。
        // 侧栏两种都列（queue.railwaiting）时左边是那条 60 宽的侧栏，两边各一道间隔。
        int left = box.X + GapPx + (LeftTabsOn ? PageBook.LeftReserve(_scale) : SideW + GapPx);
        int right = box.Right - GapPx - PageBook.Reserve(_scale);
        return new Rectangle(left, box.Y + GapPx, Math.Max(200, right - left), Math.Max(160, box.Height - 2 * GapPx));
    }

    // 侧栏两组的小标题位置（画在 OnPaint 里）
    Rectangle _sideWaitLabel, _sideBusyLabel;
    Rectangle _queueEmpty;
    // 右边每一页的位置（前面的在前），变形动画拿它当空白卡片。网格视图时为空。
    readonly List<Rectangle> _peekRects = new List<Rectangle>();

    void LayoutQueue(Rectangle box, bool place)
    {
        SyncQueue();
        // 工具条是在这之前排的（Relayout 开头），副标题里的「等你 N · 在干活 M」这时才定下来：宽度变了就重排一次
        if (TitleBlockWidth() != _titleW) LayoutToolbar();
        _solo = null;
        Slot front = QueueFront;
        // 刚换到中间的那家按新位置重画会冒一阵 CPU，CPU 兜底先别算它
        if (front != null && front != _lastQueueFront) front.CpuMuteUntil = DateTime.Now.AddSeconds(4);
        if (front != _lastQueueFront && front != null) Prefs.SetStr("queue.front", front.Kind.Id);
        if (front != _lastQueueFront)
            Log.W("queue front " + (_lastQueueFront == null ? "-" : _lastQueueFront.Kind.Id) + " -> "
                  + (front == null ? "-" : front.Kind.Id + "(" + (front.AgentState ?? "null") + ")"));
        _lastQueueFront = front;
        bool zoomed = _queueZoom && front != null;
        Rectangle fr = zoomed
            ? new Rectangle(box.X + GapPx, box.Y + GapPx, Math.Max(200, box.Width - 2 * GapPx), Math.Max(160, box.Height - 2 * GapPx))
            : QueueFrontRect(box);
        int inset = D(8);
        Rectangle content = new Rectangle(fr.X + inset, fr.Y + HeaderH,
            Math.Max(40, fr.Width - inset * 2), Math.Max(40, fr.Height - HeaderH - inset));

        // 右边的书页：排在中间那张后面等你的，每家一页
        _pages.Clear();
        if (!zoomed) foreach (Slot s in _waitQ) if (s != front) _pages.Add(s);
        // 签上写它在等什么（TabNote）：每个签上要写几行，写了字的签高一截（PageBook.Layout 的 lines）
        var cap = new int[_pages.Count];
        for (int k = 0; k < cap.Length; k++)
        {
            _pages[k].TabNote = TabNote(_pages[k]);   // 这次排版照它排的；心跳里发现变了再说（SyncTabNote）
            cap[k] = NoteLines(_pages[k].TabNote);
        }
        _book = PageBook.Layout(fr, _pages.Count, _scale, HeaderH, cap);
        bool both = RailWaiting;

        // 左边：在干活的。侧栏两种都列（queue.railwaiting）时等你的也列在这儿，在上
        int x = box.X + GapPx + (SideW - ChipH) / 2, y = box.Y + GapPx;
        var waiting = new List<Slot>();
        if (both) foreach (Slot s in _waitQ) if (s != front) waiting.Add(s);
        var busy = new List<Slot>();
        foreach (Slot s in _slots) if (Busy(s) && s != front) busy.Add(s);
        _sideWaitLabel = _sideBusyLabel = Rectangle.Empty;
        _railDivider = Rectangle.Empty;
        var chips = new Dictionary<Slot, Rectangle>();
        foreach (Slot s in waiting) { chips[s] = new Rectangle(x, y, ChipH, ChipH); y += ChipH + D(8); }
        if (waiting.Count > 0 && busy.Count > 0)
        {
            _railDivider = new Rectangle(x + D(10), y + D(2), ChipH - D(20), Math.Max(1, D(1)));
            y += D(12);
        }
        // 在干活的是从中间那张左边伸出来的签，跟右边的索引签同高、同距、从同一个高度排起（PageBook.LeftTabs）；
        // 侧栏两种都列（queue.railwaiting）时是侧栏里一个个方块。
        _leftSlots.Clear();
        _leftTabs = NoTabs;
        if (LeftTabsOn && !zoomed)
        {
            _leftSlots.AddRange(busy);
            _leftTabs = PageBook.LeftTabs(fr, busy.Count, _scale, HeaderH);
            for (int k = 0; k < busy.Count; k++) chips[busy[k]] = _leftTabs[k];
        }
        else
            foreach (Slot s in busy) { chips[s] = new Rectangle(x, y, ChipH, ChipH); y += ChipH + D(8); }
        // 放大时左边整个收起，只剩中间那张
        if (zoomed) { chips.Clear(); _railDivider = Rectangle.Empty; }
        if (_railHot != null && !chips.ContainsKey(_railHot) && !_pages.Contains(_railHot)) _railHot = null;

        _queueEmpty = front == null ? fr : Rectangle.Empty;
        _peekRects.Clear();
        _peekRects.AddRange(_book.Pages);
        foreach (Slot s in _slots)
        {
            // 内容区一律是中间那张的尺寸：轮到谁只换显示，不重排客户端
            s.Content = content;
            if (s == front)
            {
                s.Pane = fr;
                PlaceChrome(s);
                continue;
            }
            // 不在中间的：左边的签（或者侧栏的方块），或者右边的索引签（它的小卡片就是那个签，图标和灯都放在签上）
            Rectangle chip;
            int page = both ? -1 : _pages.IndexOf(s);
            if (chips.TryGetValue(s, out chip)) s.Pane = chip;
            else if (page >= 0) s.Pane = _book.Tabs[page];
            else { s.Pane = Rectangle.Empty; HideChrome(s); continue; }
            PlaceChip(s);
        }
        Invalidate();
        if (place) PlaceAll();
        SyncStack();
        if (!_closed) EnsureFocusVisible();
        if (!_closed) WriteStatus(true);
        SaveSlots();
    }

    // 不在队列视图（或者一格都没有）：没有书页
    void ClearBook()
    {
        _peekRects.Clear();
        _pages.Clear();
        _book = null;
        _leftSlots.Clear();
        _leftTabs = NoTabs;
    }

    // 索引签上写的那一小句（只有嵌好了、状态是钩子报上来的才有）。两行的中间是 \n
    static string TabNote(Slot s)
    {
        return s.Embedded && s.AgentState != null ? TipNotes.Short(s.AgentState, s.DoneUnseen, s.AgentNote) : "";
    }

    static int NoteLines(string note) { return string.IsNullOrEmpty(note) ? 0 : note.IndexOf('\n') >= 0 ? 2 : 1; }

    // 心跳里调：签上那一小句变了。行数没变（比如等你批准的工具换了一个）只重画那个签；
    // 行数变了签的高矮要跟着变，下面几个签也得挪：只把右边那几个签重排一遍（RelayoutTabs）
    void SyncTabNote(Slot s)
    {
        if (!_queueMode || _queueZoom || !_pages.Contains(s)) return;
        string now = TabNote(s);
        if (now == (s.TabNote ?? "")) return;
        if (NoteLines(now) != NoteLines(s.TabNote)) { RelayoutTabs(); return; }
        s.TabNote = now;
        InvalidateChip(s);
    }

    // 只重排右边的签：页还是那几页、顺序不变，中间那张和客户端都不动，只是签的高矮照现在要写的那一句重算。
    // 不走 Relayout —— 那里面会重新整理排队顺序（SyncQueue）。这是在心跳里、状态刚刷新的时候调的：赶上这一拍有一家
    // 刚开始问你，照 Relayout 走中间那张当场就换了，不带动画，也不管你是不是正在中间那张里打字。
    // 换不换、什么时候换是紧接着的 QueueAfterStates 的事。
    void RelayoutTabs()
    {
        if (_book == null || _book.Count != _pages.Count) return;
        Rectangle was = _book.Bounds;
        var cap = new int[_pages.Count];
        for (int k = 0; k < cap.Length; k++)
        {
            _pages[k].TabNote = TabNote(_pages[k]);
            cap[k] = NoteLines(_pages[k].TabNote);
        }
        _book = PageBook.Layout(_book.Front, _pages.Count, _scale, HeaderH, cap);
        _peekRects.Clear();
        _peekRects.AddRange(_book.Pages);
        // 签就是那一家的小卡片（图标和灯跟着它）；侧栏也列着它时（queue.railwaiting）小卡片在侧栏，不用挪
        if (!RailWaiting)
            for (int k = 0; k < _pages.Count; k++)
            {
                _pages[k].Pane = _book.Tabs[k];
                PlaceChip(_pages[k]);
            }
        Invalidate(Rectangle.Inflate(Rectangle.Union(was, _book.Bounds), D(4), D(4)), true);
    }

    // 左边的签：在干活的那几家（放大时、侧栏两种都列时是空的）
    static readonly Rectangle[] NoTabs = new Rectangle[0];
    readonly List<Slot> _leftSlots = new List<Slot>();
    Rectangle[] _leftTabs = NoTabs;

    // 这一家的小卡片是不是左边的签：是就返回第几个，不是返回 -1
    int LeftTabOf(Slot s)
    {
        if (!_queueMode || _leftTabs.Length == 0) return -1;
        int k = _leftSlots.IndexOf(s);
        return k < _leftTabs.Length ? k : -1;
    }

    // 这一家的小卡片是不是右边的索引签：是就返回第几页，侧栏方块、中间那张返回 -1
    int TabOf(Slot s)
    {
        if (!_queueMode || _book == null || RailWaiting) return -1;
        int k = _pages.IndexOf(s);
        return k < _book.Count ? k : -1;
    }

    // 鼠标在哪一页上（索引签，或者露出来的那条页边）。不在任何一页上返回 null
    Slot PageAt(Point p)
    {
        if (!_queueMode || _queueZoom || _book == null) return null;
        int k = PageBook.At(_book, p);
        return k >= 0 && k < _pages.Count ? _pages[k] : null;
    }

    // 不在中间的小卡片（左右的签、侧栏的方块）：只留图标和状态灯。客户端（Site）垫在中间那格底下还是藏起来，排版结束时 SyncStack 统一定
    // （见 HostForm.Stack.cs：以前一律藏，嵌进来的 Chromium 会以为自己被挡住、停止出帧）。
    void PlaceChip(Slot s)
    {
        SyncIcon(s);
        s.DropBtn.Visible = false;
        if (s.ZoomBtn != null) s.ZoomBtn.Visible = false;
        s.LaunchBtn.Visible = false;
        s.TitleLbl.Visible = false;   // 名字不上栏，悬停时出（RailHover）
        SyncStateLabel(s);
        // 只有嵌好了才是一个点；「没找到窗口」这类文字放不下，收进悬停提示里
        s.StateLbl.Visible = s.StateLbl.DotOnly;
        // 灯压在图标右下角（侧栏方块里再往外 2；签上不往外，见 PageBook.DotCenter）
        Rectangle ic = ChipIconRect(s);
        PointF dot = ChipDotCenter(s, ic);
        s.StateLbl.Location = new Point((int)dot.X - s.StateLbl.Width / 2, (int)dot.Y - s.StateLbl.Height / 2);
        _tips.SetToolTip(s.StateLbl, RailTip(s));
    }

    Rectangle RailIconRect(Rectangle chip)
    {
        int sz = D(26);
        return new Rectangle(chip.X + (chip.Width - sz) / 2, chip.Y + (chip.Height - sz) / 2, sz, sz);
    }

    // 小卡片上的图标画在哪：侧栏方块里居中；索引签上靠右（签的左半截是从页边伸出来的那一段）
    Rectangle ChipIconRect(Slot s)
    {
        int k = TabOf(s);
        if (k >= 0) return _book.Icons[k];
        k = LeftTabOf(s);
        return k >= 0 ? PageBook.LeftIcon(_leftTabs[k], _scale) : RailIconRect(s.Pane);
    }

    // 灯的中心相对图标在哪：灯控件照它摆，画上去的灯（queue.railwaiting 时签上那一颗）也照它
    PointF ChipDotCenter(Slot s, Rectangle icon)
    {
        return TabOf(s) >= 0 || LeftTabOf(s) >= 0 ? PageBook.DotCenter(icon) : new PointF(icon.Right + D(2), icon.Bottom + D(2));
    }

    // 侧栏方块上没有名字：悬停出名字，有状态就连它在干什么、停下来要你做什么一起说（跟表头的提示同一份）
    string RailTip(Slot s)
    {
        string name = s.TitleLbl.Text;
        if (!s.StateLbl.DotOnly) return name + " · " + L.T(s.StateLbl.Text);
        return AgentTip(s, name) ?? name;
    }

    // 鼠标在宿主上移动：停在侧栏方块、索引签或者露出来的页边上，就画浅底、出名字
    void RailHover(object sender, System.Windows.Forms.MouseEventArgs e)
    {
        Slot hot = null;
        if (_queueMode && !_queueZoom)
        {
            foreach (Slot s in _slots)
                if (s != QueueFront && s.Pane.Width > 0 && s.Pane.Contains(e.Location)) { hot = s; break; }
            if (hot == null) hot = PageAt(e.Location);
        }
        if (hot == _railHot) return;
        InvalidateChip(_railHot);
        _railHot = hot;
        InvalidateChip(hot);
        Cursor = hot != null ? System.Windows.Forms.Cursors.Hand : System.Windows.Forms.Cursors.Default;
        _tips.SetToolTip(this, hot != null ? RailTip(hot) : null);
    }

    // 悬停变了要重画哪儿。连子控件一起（灯是压在小卡片上的子控件，底是它借父窗口画的：
    // 只重画父窗口的话，灯四周那一小块还是旧的底色）。
    void InvalidateChip(Slot s)
    {
        if (s == null) return;
        Invalidate(s.Pane, true);
        if (LeftTabOf(s) >= 0) Invalidate(Rectangle.Inflate(s.Pane, D(3), D(3)), true);   // 左边的签：连描边一起
        int k = _book == null ? -1 : _pages.IndexOf(s);
        if (k < 0 || k >= _book.Count) return;
        Invalidate(Rectangle.Inflate(_book.Tabs[k], D(3), D(3)), true);
        // 签和它那一页的页边一起变（见 PageBook.Paint），那一条也得重画
        int left = k == 0 ? _book.Front.Right : _book.Pages[k - 1].Right;
        Invalidate(Rectangle.FromLTRB(left - D(2), _book.Pages[k].Y - D(2), _book.Pages[k].Right + D(3), _book.Pages[k].Bottom + D(3)), true);
    }

    // 动画要的每一「张」卡片的位置：先是每家各一张（中间 / 侧栏 / 没有），再接右边叠着的那几张空白卡。
    // plain：哪几张用空白卡片代替（叠在后面、截不到完整样子）；order：叠放顺序，先画的在下面 ——
    // 侧栏 → 右边叠着的（后面的先画）→ 中间那张。网格视图时没有叠着的，顺序就是格子顺序。
    // 切换前在中间的那张（动画里它缩回侧栏时要叠在最上面）
    Slot _leavingFront;

    Rectangle[] TransitionRects(out bool[] plain, out int[] order)
    {
        int n = _slots.Count;
        var rects = new Rectangle[n + QueuePeeks];
        plain = new bool[n + QueuePeeks];
        for (int i = 0; i < n; i++) rects[i] = _slots[i].Pane;
        for (int k = 0; k < QueuePeeks; k++)
        {
            rects[n + k] = _queueMode && k < _peekRects.Count ? _peekRects[k] : Rectangle.Empty;
            plain[n + k] = true;
        }
        // 从下到上：叠在后面的空白卡（它们只该露出边缘，放最底）→ 侧栏 → 新的中间那张 →
        // 正在离开中间的那张（缩回侧栏的过程看得见；以前它归在侧栏层、被两张空白卡整个盖住，开头一片白）。
        var ord = new List<int>();
        Slot front = _queueMode ? QueueFront : null;
        Slot leaving = _leavingFront != null && _leavingFront != front && _slots.Contains(_leavingFront) ? _leavingFront : null;
        for (int k = QueuePeeks - 1; k >= 0; k--) ord.Add(n + k);
        for (int i = 0; i < n; i++) if (_slots[i] != front && _slots[i] != leaving) ord.Add(i);
        if (front != null) ord.Add(_slots.IndexOf(front));
        if (leaving != null) ord.Add(_slots.IndexOf(leaving));
        order = ord.ToArray();
        return rects;
    }

    // 带过渡动画地改一次排版：盖浮层（截旧的）→ change() 真正改 → 开播 → 宿主当场画掉新边框 → 允许截新的。
    // morph = 变形（队列视图用）；否则是换排版的「收缩再展开」。
    void RunTransition(Action change, bool morph = true, bool fade = false)
    {
        LayoutTransition tr = null;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Rectangle area = new Rectangle(0, ToolbarH, ClientSize.Width, Math.Max(0, ClientSize.Height - ToolbarH));
        if (_slots.Count > 0)
        {
            if (_transition != null && !_transition.IsDisposed) _transition.Close();
            bool[] plain; int[] order;
            Rectangle[] from = TransitionRects(out plain, out order);
            tr = fade ? LayoutTransition.CoverFade(this, area)
               : morph ? LayoutTransition.Cover(this, area, from, true, plain, D(12), Pane)
                       : LayoutTransition.Cover(this, area, from);
            _transition = tr;
        }
        long tCover = sw.ElapsedMilliseconds;
        _leavingFront = _queueMode ? QueueFront : null;
        // 队列里换人：原来中间那张像书页一样往左翻过去，露出底下换上来的那一家（见 LayoutTransition.TurnPage）。
        // 只在中间那张的位置没变时翻（放大 / 还原、网格 ⇄ 队列不算）；别的情况照旧淡出。
        Slot turnFrom = fade && tr != null && _queueMode && !_queueZoom && PageTurn ? QueueFront : null;
        Rectangle turnRect = turnFrom != null ? turnFrom.Pane : Rectangle.Empty;
        change();
        if (tr != null)
        {
            bool[] plain2; int[] order2;
            Rectangle[] to = TransitionRects(out plain2, out order2);
            Slot nf = _queueMode ? QueueFront : null;
            if (turnFrom != null && nf != null && nf != turnFrom && !_queueZoom && turnRect.Width > 0 && nf.Pane == turnRect)
                tr.TurnPage(turnRect);
            if (fade && nf != null && nf.Embedded && nf.Site != null && nf.Site.Visible && nf.Site.Width > 40 && nf.Site.Height > 40)
            {
                tr.WaitForPaint(nf.Site.RectangleToScreen(Rectangle.Inflate(nf.Site.ClientRectangle, -8, -8)));
                // 100ms 还没画出来：多半是它以为自己被挡住了（刚从藏着的状态垫回来、还没醒），在浮层底下叫醒它
                Slot wakeTarget = nf;
                tr.SlowPaint = delegate
                {
                    try { BeginInvoke((System.Windows.Forms.MethodInvoker)delegate { if (!_closed && QueueFront == wakeTarget) Wake(wakeTarget, "切换时没画出来"); }); }
                    catch { }
                };
            }
            tr.Begin(to, plain2, order2);
            _leavingFront = null;
            long tBegin = sw.ElapsedMilliseconds;
            // 动画在自己的渲染线程上跑，先开播；宿主再当场把新排版的卡片边框画掉（几十毫秒），
            // 画完才让浮层截第二张 —— 不等的话截到的是挪到新位置的客户端配旧边框。
            PaintTicks = 0; PaintCalls = 0;
            long tU = sw.ElapsedMilliseconds;
            Update();
            // Update() 只画宿主窗口自己；表头名字、状态灯、侧栏卡片这些子控件要等下一轮消息循环才画，
            // 淡出早开就会露出「内容已换、表头还是上一家」（2026-09-26 逐帧拍到，用户看着是闪一下）。
            // 只当场画宿主自己的控件，不碰嵌进来的客户端（跨进程同步重画，客户端卡住会把宿主拖住）。
            UpdateChrome();
            tr.HostPainted();
            Log.W("layout-anim click→cover " + tCover + "ms, →begin " + tBegin + "ms, host painted " + sw.ElapsedMilliseconds
                + "ms (Update " + (sw.ElapsedMilliseconds - tU) + "ms, OnPaint x" + PaintCalls + " = "
                + (PaintTicks * 1000 / System.Diagnostics.Stopwatch.Frequency) + "ms)");
        }
    }

    void UpdateChrome()
    {
        foreach (System.Windows.Forms.Control c in Controls)
        {
            if (!c.Visible || c is SitePanel) continue;
            c.Update();
        }
    }

    // 点不在中间的小卡片（左边的签、右边的索引签、侧栏的方块）：拉到中间来看
    void PinFront(Slot s)
    {
        if (s == null || s == QueueFront) return;
        RunTransition(delegate
        {
            // 点了谁谁就粘在中间（跟「粘住」同一套），不再另记一个「点来看」：
            // 以前它状态一变点来看的记录就清掉，中间又跳回原来粘着的那家（用户：点完 Claude 又跳回 Cursor）
            _sticky = s;
            _manualPin = true;
            _pinSawIdle = !Busy(s);
            _queueSig = "";
            Relayout(true);
        }, true, true);
    }

    // 「完成」：这家先不用管了，移到已完成；已完成的（拉到中间看时）再点一下 = 放回队列
    // 「放大 / 还原」统一入口：网格里是这一格单独铺满（_solo），队列里是中间那张铺满、侧栏收起（_queueZoom）。
    void ToggleZoom(Slot s)
    {
        if (s == null) return;
        if (!_queueMode) { ToggleSolo(s); return; }
        if (s != QueueFront) return;
        RunTransition(delegate
        {
            _queueZoom = !_queueZoom;
            _queueSig = "";
            Relayout(true);
        }, true, true);
    }

    // 「跳过」：队头挪到队尾（或者放开点来看的那个）
    void SkipFront()
    {
        int others = _waitQ.Count - (QueueFront != null && _waitQ.Contains(QueueFront) ? 1 : 0);
        if (others <= 0) { Toast(L.T("后面没有别人在等")); return; }
        RunTransition(delegate
        {
            // 中间那张在队里就挪到队尾；在干活（不在队里）就直接换队首上来
            Slot cur = QueueFront;
            if (cur != null && _waitQ.Remove(cur)) _waitQ.Add(cur);
            _sticky = _waitQ.Count > 0 ? _waitQ[0] : null;
            _manualPin = false;
            _queueSig = "";
            Relayout(true);
        }, true, true);
    }

    void ToggleQueueMode()
    {
        if (_slots.Count == 0) { Toast(L.T("先纳入或启动几家，再切到队列")); return; }
        RunTransition(delegate
        {
            _queueMode = !_queueMode;
            _queueZoom = false;
            Prefs.Set("queue", _queueMode);
            _queueSig = "";
            SyncViewButton();
            if (!_queueMode) ClearBook();
            Relayout(true);
        });
    }

    void SyncViewButton()
    {
        _view.Text = _queueMode ? "网格" : "队列";
        _cycle.Text = _queueMode ? "跳过" : "换排版";
        Tip(_view, _queueMode ? "切回格子视图" : "一次只看一个：等你回复的排队，在干活的收到侧栏");
        Tip(_cycle, _queueMode ? "这个先不看，排到队尾" : "换下一套排版（同样格子数下轮换）");
        LayoutToolbar();
        Invalidate(_pill);
    }

    // 心跳里状态刷新之后调：队列有变化就带动画重排
    void QueueAfterStates()
    {
        if (!_queueMode) return;
        // 先试算一遍看有没有变化；有变化就退回旧的，让 RunTransition 先按旧样子截图，再在 change 里落地
        var oldQ = new List<Slot>(_waitQ);
        Slot oldSticky = _sticky;
        string oldSig = _queueSig;
        if (!SyncQueue()) { _queuePending = false; return; }
        var newQ = new List<Slot>(_waitQ);
        Slot newSticky = _sticky;
        Slot newFront = QueueFront;
        _waitQ.Clear(); _waitQ.AddRange(oldQ); _sticky = oldSticky; _queueSig = oldSig;
        // 你正在中间那张里打字：先别换人（换了焦点跟着走，字会打进别的窗口），
        // 提示一句，等你停手（下一拍再判）再换。点「跳过」「完成」这种手动的不走这里。
        Slot curFront = QueueFront;
        if (newFront != curFront && curFront != null && _hostActive && CpuStates.InputIdleMs() < 3000)
        {
            if (newFront != null && newFront.AgentState == "asking") Toast(L.F("{0} 在问你，停手后换过去", newFront.Kind.Name));
            _queuePending = true;   // 心跳每一拍再判，停手了就换
            return;
        }
        _queuePending = false;
        RunTransition(delegate
        {
            _waitQ.Clear(); _waitQ.AddRange(newQ); _sticky = newSticky;
            _queueSig = "";
            Relayout(true);
        }, true, true);
    }

    // 画卡片的顺序：网格视图就是格子顺序；队列视图中间那张最后画，压住它后面那几页的左边。
    IEnumerable<int> PaintOrder()
    {
        Slot front = _queueMode ? QueueFront : null;
        for (int i = 0; i < _slots.Count; i++) if (_slots[i] != front) yield return i;
        if (front != null) yield return _slots.IndexOf(front);
    }

    // 队列视图额外要画的：侧栏的分隔线、右边的书页（页边 + 索引签的底）、没人排队时的一句话。
    void PaintQueueExtras(Graphics g, Rectangle clip)
    {
        int wait = 0, busy = 0;
        Slot front = QueueFront;
        foreach (Slot s in _waitQ) if (s != front) wait++;
        foreach (Slot s in _slots) if (Busy(s) && s != front) busy++;
        if (!_railDivider.IsEmpty && clip.IntersectsWith(_railDivider))
            using (var pen = new Pen(Line))
                g.DrawLine(pen, _railDivider.X, _railDivider.Y, _railDivider.Right, _railDivider.Y);
        // 左边的签：底在这儿画，图标在 OnPaint 的主循环里画，灯是控件
        if (_leftTabs.Length > 0 && front != null)
            PageBook.PaintLeft(g, front.Pane, _leftTabs, _railHot == null ? -1 : _leftSlots.IndexOf(_railHot), _scale, clip);
        if (_book != null && _book.Count > 0 && clip.IntersectsWith(_book.Bounds))
        {
            PageBook.Paint(g, _book, _railHot == null ? -1 : _pages.IndexOf(_railHot), _scale, clip);
            // 签上写它在等什么：图标下面一小句（一行或两行，居中），在问你的用正文色、别的用淡色
            if (_book.Texts != null)
                for (int k = 0; k < _book.Count && k < _pages.Count; k++)
                {
                    Rectangle t = _book.Texts[k];
                    if (t.IsEmpty || !clip.IntersectsWith(t)) continue;
                    Slot s = _pages[k];
                    string[] rows = (s.TabNote ?? "").Split('\n');
                    int rowH = t.Height / Math.Max(1, NoteLines(s.TabNote));
                    for (int i = 0; i < rows.Length && (i + 1) * rowH <= t.Height; i++)
                        Theme.Text(g, rows[i], SubFont, s.AgentState == "asking" ? Ink : Dim,
                            new RectangleF(t.X, t.Y + i * rowH, t.Width, rowH), StringAlignment.Center);
                }
            // 签上的图标和灯平常是那一家自己的小卡片（OnPaint 的主循环画图标，灯是控件）；
            // 侧栏也列着它时（queue.railwaiting）小卡片在侧栏，签上这一份在这里画
            if (RailWaiting)
                for (int k = 0; k < _book.Count && k < _pages.Count; k++)
                {
                    Slot s = _pages[k];
                    if (!clip.IntersectsWith(Rectangle.Inflate(_book.Tabs[k], D(4), D(4)))) continue;
                    if (s.IconBmp != null) Theme.Icon(g, s.IconBmp, _book.Icons[k], D(4), D(7));
                    if (s.StateLbl.DotOnly) PageBook.Dot(g, s.StateLbl.Tone, PageBook.DotCenter(_book.Icons[k]), s.StateLbl.Height * 0.34f);
                }
        }
        if (!_queueEmpty.IsEmpty && clip.IntersectsWith(_queueEmpty))
        {
            Theme.Card(g, _queueEmpty, D(12), Pane, _scale);
            var r = new RectangleF(_queueEmpty.X, _queueEmpty.Y + _queueEmpty.Height / 2 - D(30), _queueEmpty.Width, D(28));
            Theme.Text(g, L.T("没有人在等你"), NameFont, Ink, r, StringAlignment.Center);
            r.Offset(0, D(30));
            Theme.Text(g, L.T("干完活的会话会排到这里"), SubFont, Dim, r, StringAlignment.Center);
        }
    }

    string QueueSubTitle()
    {
        int busy = 0;
        foreach (Slot s in _slots) if (Busy(s)) busy++;
        int wait = _waitQ.Count;
        if (_queueZoom && QueueFront != null) return L.F("放大 {0} · 点还原回队列", QueueFront.Kind.Name);
        return L.F("队列 · 等你 {0} · 在干活 {1}", wait, busy);
    }
}
