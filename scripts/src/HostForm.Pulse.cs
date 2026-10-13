// 【拆分】心跳：状态灯刷新、QoS 触发、待关观察、孤儿认回、Misfit 重压。（从原 AgentHost.cs 原样搬出，逻辑未改）

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;
using System.Windows.Forms;

internal sealed partial class HostForm
{
    // 心跳每 0.7 秒一次，而宿主线程跟四家客户端共用一条输入队列 ——
    // 宿主这边卡一下，格子里的点击和动画就跟着卡（2026-09-19 用户报侧栏展开卡顿，
    // 实测宿主空闲也吃 14% 单核）。所以心跳只做必要的事：
    //  - 枚举全桌面窗口（每个都查进程和 exe 路径，很贵）只在有空格子时做；
    //  - 不再每拍 Relayout + 整窗重画，只有状态真变了才重排。
    int _qosTick;
    int _agentTick;

    // 孤儿自愈：窗口还挂在某个格子的面板下面，但 slot 里的 Hwnd 已经被清空了。
    //
    // 2026-09-20 用户撞上：点了工具栏的「客户端 / CLI」，ToggleMode 把四个 slot 的 Hwnd
    // 全清了再重新找，而「重新找」只扫**顶层窗口** —— 那四个此刻是宿主的子窗口，
    // 不在顶层列表里，于是永远找不回来：窗口还在格子里显示着，宿主却不认得它们，
    // 点击不交焦点、热键找不到格子、输入法也没人管。
    //
    // 与其堵某一条路径，不如让它自己好：只要某格空着、而它的面板下面挂着一个客户端窗口，
    // 就认回来。只在有空格子时扫（EnumChildWindows 一个面板，很便宜）。
    // 「待关」观察期：窗口没了不等于用户要关它 —— Cursor 的「重启窗口」、客户端自己
    // 重载页面都会让窗口消失一下再回来。所以先观察 5 秒：
    //   回来了（这一格又被纳入、或者桌面上又出现了这家的窗口）→ 当没事发生；
    //   没回来 → 按用户 2026-09-20 定的语义，把这家的进程树收掉。
    // 嵌进来的 Electron 偶尔整窗画面丢掉、卡在一片空白（只剩它原生画的 – □ ×）：
    // 2026-09-26 Codex 纳回来之后一直空白，点它、改尺寸都不管用，隐藏再显示一次立刻恢复
    // （Chromium 重新可见时会重新提交画面）。这里盯着看得见的格子，连续两拍空白就这么救一次。
    // 只在宿主在前台、没在播动画时看（否则截到的是盖在上面的别的窗口 / 浮层）；
    // 终端本来就是一整块黑，不看；每格 10 秒最多救一次，免得误判时来回闪。
    // 截屏判空白一次 22~45ms（实测），**不能放界面线程**：宿主界面线程跟嵌进来的客户端共用输入队列，
    // 每拍卡它几十毫秒，客户端就一顿一顿的（用户 2026-09-26：Codex 切对话卡卡的）。放后台线程截，界面线程只收结果。
    DateTime _blankCheckAt;
    volatile bool _blankBusy;
    readonly Dictionary<Slot, int> _blankStreak = new Dictionary<Slot, int>();
    readonly Dictionary<Slot, DateTime> _blankFixedAt = new Dictionary<Slot, DateTime>();

    void BlankWatch()
    {
        if (_closed || _blankBusy || !_hostActive || WindowState == FormWindowState.Minimized) return;
        if (_transition != null && !_transition.IsDisposed) return;
        if ((DateTime.Now - _blankCheckAt).TotalMilliseconds < 1950) return;
        _blankCheckAt = DateTime.Now;
        var todo = new List<KeyValuePair<Slot, Rectangle>>();
        foreach (Slot s in _slots)
        {
            if (!s.Embedded || s.Hwnd == IntPtr.Zero || s.Kind.Cli || s.Custom) continue;
            // 只看正在显示的那一格：垫在底下的格子 Site 也是可见的，截到的却是盖在上面那家
            if (s.Site == null || !SlotShowing(s) || s.Site.Width < 200 || s.Site.Height < 200) continue;
            if (!Native.IsWindowVisible(s.Hwnd)) continue;
            todo.Add(new KeyValuePair<Slot, Rectangle>(s, s.Site.RectangleToScreen(Rectangle.Inflate(s.Site.ClientRectangle, -12, -12))));
        }
        if (todo.Count == 0) return;
        _blankBusy = true;
        System.Threading.ThreadPool.QueueUserWorkItem(delegate
        {
            var blank = new List<Slot>();
            var fine = new List<Slot>();
            try { foreach (var kv in todo) (LayoutTransition.Painted(kv.Value) ? fine : blank).Add(kv.Key); }
            catch { }
            try { BeginInvoke((MethodInvoker)delegate { _blankBusy = false; BlankResult(blank, fine); }); }
            catch { _blankBusy = false; }
        });
    }

    void BlankResult(List<Slot> blank, List<Slot> fine)
    {
        if (_closed) return;
        foreach (Slot s in fine) _blankStreak[s] = 0;
        // 截的这一秒里宿主要是开始播动画 / 换了前台，结果作废
        if (_transition != null && !_transition.IsDisposed) return;
        foreach (Slot s in blank)
        {
            if (!_slots.Contains(s) || s.Hwnd == IntPtr.Zero || s.Site == null || !SlotShowing(s)) continue;
            int streak;
            _blankStreak.TryGetValue(s, out streak);
            _blankStreak[s] = ++streak;
            if (streak < 2) continue;
            DateTime last;
            if (_blankFixedAt.TryGetValue(s, out last) && (DateTime.Now - last).TotalSeconds < 10) continue;
            _blankFixedAt[s] = DateTime.Now;
            _blankStreak[s] = 0;
            Log.W("blank " + s.Kind.Id + ": 画面空白 " + streak + " 拍，叫醒它重画");
            Wake(s, "画面空白");
        }
    }

    void WatchClosing()
    {
        for (int i = 0; i < _slots.Count; i++)
        {
            Slot s = _slots[i];
            if (s.Kind.Cli || s.Custom) continue;
            if (s.ClosingSince == DateTime.MinValue) continue;
            if (s.Hwnd != IntPtr.Zero) { s.ClosingSince = DateTime.MinValue; s.ClosingPid = 0; continue; }
            // 15 秒：Cursor 的「重启窗口」实测能超过 5 秒，按 5 秒判会误杀（2026-09-20 踩过）
            if ((DateTime.Now - s.ClosingSince).TotalSeconds < 15) continue;

            int pid = s.ClosingPid;
            s.ClosingSince = DateTime.MinValue;
            s.ClosingPid = 0;
            if (!s.ClosingByUser) { Log.W("closing " + s.Kind.Id + ": 不是点 ✕ 之后藏的，只放成独立窗口，不收进程（pid=" + pid + "）"); continue; }
            if (pid == 0 || !Prefs.Get("closeagents", true)) { Log.W("closing " + s.Kind.Id + ": 不收（pid=" + pid + " closeagents=" + Prefs.Get("closeagents", true) + "）"); continue; }
            // 两道复核，任一条成立就别动它：
            //  1) 桌面上又出现了这家的窗口（按种类认）；
            //  2) 这个进程树下还有可见窗口（重启窗口时新窗口属于同一个进程，
            //     但标题/类名可能一时对不上种类规则，所以这条更可靠）。
            if (HostWins.Find(s.Kind, HostWins.Visible()) != null) { Log.W("closing " + s.Kind.Id + ": 桌面上又有它的窗口，不收"); continue; }
            try { if (Procs.HasVisibleWindow(pid)) { Log.W("closing " + s.Kind.Id + ": 进程 " + pid + " 还有可见窗口，不收"); continue; } }
            catch { continue; }
            Log.W("closing " + s.Kind.Id + ": 窗口收起 15 秒没回来，结束进程 " + pid);
            try { Procs.CloseApp(IntPtr.Zero, 0, pid); } catch (Exception ex) { Log.W("closing 失败: " + ex.Message); }
        }
    }

    void ReadoptOrphans()
    {
        for (int i = 0; i < _slots.Count; i++)
        {
            Slot s = _slots[i];
            if (s.Hwnd != IntPtr.Zero || s.Site == null || !s.Site.IsHandleCreated) continue;
            IntPtr found = IntPtr.Zero;
            var sb = new StringBuilder(64);
            Native.EnumChildWindows(s.Site.Handle, delegate(IntPtr h, IntPtr lp)
            {
                if (found != IntPtr.Zero) return false;
                sb.Length = 0;
                Native.GetClassName(h, sb, sb.Capacity);
                // 客户端主窗口都是 Chromium 的这个类；我们自己的控件不是
                if (sb.ToString().StartsWith("Chrome_WidgetWin", StringComparison.Ordinal)) found = h;
                return found == IntPtr.Zero;
            }, IntPtr.Zero);
            if (found == IntPtr.Zero || !Native.IsWindow(found)) continue;
            s.Hwnd = found;
            s.Embedded = true;
            Watch.Note(found);
            _pulseDirty = true;
        }
    }

    void Pulse()
    {
        if (_dragSlot != null) return;
        SyncHotkeys();
        BlankWatch();

        // 每 3 拍（约 2 秒）看一眼状态文件。文件没变就是几个字段的比较，很便宜。
        if (++_agentTick >= 3)
        {
            _agentTick = 0;
            // 注意：不能只在「文件变了」时才更新。写的那头停了以后文件不再变，
            // 但状态会过期（钩子数据太旧），这时也得把灯改回去、重画一次。
            // 状态来源：各家官方钩子写的会话文件（HookStates）+ 没钩子的看 CPU（CpuStates）。
            // 虚拟窗口是独立软件，不靠别的程序来判状态；外部程序只读它的数据。
            HookStates.Refresh();
            var cliIds = new List<string>();
            foreach (Slot c in _slots) if (c.Kind.Cli) cliIds.Add(c.Kind.Id);
            CliStates.Refresh(cliIds);
            // 没有钩子的看进程忙不忙（CpuStates，后台线程采样，结果下一拍用）
            {
                var roots = new Dictionary<string, int>();
                var muted = new HashSet<string>();
                uint idle = CpuStates.InputIdleMs();
                foreach (Slot c in _slots)
                {
                    if (!UsesCpu(c) || c.Hwnd == IntPtr.Zero || !Native.IsWindow(c.Hwnd)) continue;
                    // 刚嵌进来 / 重新嵌入：客户端按新尺寸重画会冒十几秒 CPU，
                    // 不静默就被当成「干了一次活」，一开宿主就亮一排黄灯（用户 2026-09-25 截图）
                    if (c.CpuHwnd != c.Hwnd)
                    {
                        c.CpuHwnd = c.Hwnd;
                        DateTime until = DateTime.Now.AddSeconds(15);
                        if (until > c.CpuMuteUntil) c.CpuMuteUntil = until;
                    }
                    uint pid;
                    Native.GetWindowThreadProcessId(c.Hwnd, out pid);
                    if (pid == 0) continue;
                    roots[c.Kind.Id] = (int)pid;
                    bool typing = _hostActive && c.Hwnd == _lastFocused && idle < 4000;
                    if (typing || DateTime.Now < c.CpuMuteUntil) muted.Add(c.Kind.Id);
                }
                CpuStates.SampleAsync(roots, muted);
            }
            {
                bool agentStatusChanged = false;
                for (int i = 0; i < _slots.Count; i++)
                {
                    Slot s = _slots[i];
                    // CLI 会话：钩子写的状态（CliStates）；桌面客户端：Agents 里记的
                    string st; int sec;
                    HookNote note = null;
                    if (UsesCpu(s)) { st = CpuStates.StateOf(s.Kind.Id); sec = CpuStates.SecsOf(s.Kind.Id); }
                    else
                    {
                        st = s.Kind.Cli ? HookStates.Slot(s.Kind.Id, out sec, out note) : HookStates.Desktop(s.Kind.FamilyId, out sec, out note);
                        if (st == null && s.Kind.Cli) { st = CliStates.StateOf(s.Kind.Id); sec = CliStates.SecsOf(s.Kind.Id); }
                        // 有钩子的家钩子还没数据（Codex 没在 /hooks 信任、Cursor 没重启加载）：显示未知，
                        // 不拿 CPU 猜 —— 实测 Codex 桌面端闲着也占 0.19 核，猜出来全是「在干活」
                    }
                    // 后面方括号里是引起这次变化的那条钩子记录（哪个事件、哪个工具、哪个会话）：一格合并了这一家的所有会话，
                    // 灯「怎么又黄了」光看状态查不出来（2026-10-12 就是对着这几行日志加会话文件才对上的）
                    if (s.AgentState != st) Log.W("state " + s.Kind.Id + " " + (s.AgentState ?? "null") + " -> " + (st ?? "null") + " (" + sec + "s)" + HookNote.Why(note));
                    agentStatusChanged |= s.AgentState != st;
                    s.AgentState = st;
                    // Elapsed time only changes the hover tooltip. Do not relayout
                    // and repaint every embedded client just because time passed.
                    s.AgentSecs = sec;
                    s.AgentNote = note;

                    // 黄灯＝它做完了、而且你还没点进这一格看过。
                    // 光看状态不够：done 只表示「最后一条是它的回复」，既不知道你派没派活，
                    // 也不知道你看没看过。所以拿「它做完的时刻」跟「你上次点进这格的时刻」比。
                    // 人正看着这一格（宿主在前台、焦点在它身上）也算看过。
                    IntPtr fgw = Native.GetForegroundWindow();
                    bool here = fgw == Handle || Native.IsChild(Handle, fgw);
                    if (here && _focusByUser && s.Hwnd != IntPtr.Zero && s.Hwnd == _lastFocused)
                        Seen.Touch(s.Kind.Id);
                    // asking（停在半路问你）跟 done 一样亮黄灯，而且更急
                    bool unseen = (st == "done" || st == "asking") && DateTime.Now.AddSeconds(-sec) > Seen.Of(s.Kind.Id);
                    s.DoneUnseen = unseen;
                    RefreshAgentIndicator(s);
                    SyncTip(s);
                }
                if (agentStatusChanged) WriteStatus(true);
                if (agentStatusChanged || _queuePending) QueueAfterStates();
                bool anyUnseen = false;
                for (int i = 0; i < _slots.Count; i++) if (_slots[i].DoneUnseen) anyUnseen = true;
                IntPtr fg2 = Native.GetForegroundWindow();
                SyncFlash(anyUnseen, fg2 == Handle || Native.IsChild(Handle, fg2));
            }
        }

        // 约 5 秒一次。进程快照要枚举全机进程，宿主跟四家共用一条输入队列，
        // 在界面线程上做会变成每 5 秒一次的小停顿（格子里打字、动画跟着顿）——
        // 所以只在界面线程上取窗口列表，扫描和设置扔到后台线程。
        if (++_qosTick >= 7)
        {
            _qosTick = 0;
            var live = new List<IntPtr>();
            for (int i = 0; i < _slots.Count; i++)
                if (!_slots[i].Kind.Cli && !_slots[i].Custom && _slots[i].Embedded && _slots[i].Hwnd != IntPtr.Zero && Native.IsWindow(_slots[i].Hwnd))
                    live.Add(_slots[i].Hwnd);
            Qos.SyncAsync(live);
        }
        if (_toast != null && DateTime.Now >= _toastUntil) { _toast = null; Invalidate(_pill); }
        foreach (Slot pending in _slots)
            if (pending.Hwnd == IntPtr.Zero && pending.StateLbl.Text == "正在启动…"
                && _launching.ContainsKey(pending.Kind.Id) && DateTime.Now >= _launching[pending.Kind.Id])
            { pending.StateLbl.Text = "启动未完成，可重试"; pending.StateLbl.DotOnly = false; }
        bool waiting = DateTime.Now < _lockUntil;
        bool anyEmpty = false;
        for (int i = 0; i < _slots.Count; i++)
            if (_slots[i].Hwnd == IntPtr.Zero || !Native.IsWindow(_slots[i].Hwnd)) anyEmpty = true;
        WatchClosing();
        if (anyEmpty) ReadoptOrphans();
        anyEmpty = false;
        for (int i = 0; i < _slots.Count; i++)
            if (_slots[i].Hwnd == IntPtr.Zero || !Native.IsWindow(_slots[i].Hwnd)) anyEmpty = true;
        if (anyEmpty)
        {
            IntPtr[] before = new IntPtr[_slots.Count];
            for (int i = 0; i < _slots.Count; i++) before[i] = _slots[i].Hwnd;
            Attach(HostWins.Visible());
            for (int i = 0; i < _slots.Count; i++)
            {
                if (_slots[i].Hwnd == before[i]) continue;
                _pulseDirty = true;
                // 刚启动的那家窗口出来了：不用再干等「启动锁」到期（原来 15~25 秒，
                // 期间窗口就那么飘在最上面，用户 2026-09-20 报「嵌进去太慢」）。
                // 留 1.2 秒让 Electron 把自己的初始尺寸折腾完，再嵌。
                if (_slots[i].Hwnd != IntPtr.Zero && DateTime.Now < _lockUntil)
                {
                    DateTime soon = DateTime.Now.AddSeconds(1.2);
                    if (soon < _lockUntil) _lockUntil = soon;
                    waiting = DateTime.Now < _lockUntil;
                }
            }
        }
        bool needPlace = false;
        for (int i = 0; i < _slots.Count; i++)
        {
            Slot s = _slots[i];
            if (s.Hwnd == IntPtr.Zero)
            {
                if (waiting) continue;
                continue;
            }
            if (!Native.IsWindow(s.Hwnd))
            {
                // 自己挑进来的窗口没了 = 这格没意义了，直接撤掉。
                if (s.Custom) { RemoveSlot(s); i--; _pulseDirty = true; continue; }
                // 窗口被销毁了：这家要么正在退，要么只关了窗口留着进程。
                // 用户定的语义是「关窗口＝不要它了」，所以把残留的进程树收掉。
                s.Hwnd = IntPtr.Zero;
                s.Embedded = false;
                s.HiddenSince = DateTime.MinValue;
                if (s.Site != null && SlotShowing(s)) PlaceChrome(s);
                _pulseDirty = true;
                continue;
            }
            // 自己把窗口藏起来（Electron 点 ✕ 多半是收进托盘，窗口还在、只是不可见）。
            // 注意要看**这个窗口自己**的 WS_VISIBLE，不能用 IsWindowVisible ——
            // 放大某一格时别的格子的面板是隐藏的，IsWindowVisible 会连带算成不可见，
            // 那样会把没关的客户端一起杀掉。
            bool selfVisible = (Native.Gwl(s.Hwnd, Native.GWL_STYLE) & Native.WS_VISIBLE) != 0;
            if (!selfVisible && Waking(s.Hwnd)) continue;   // 我们叫醒它时藏的那一下（HostForm.Stack.cs），不是它自己藏
            if (!selfVisible)
            {
                // 格子上的 ✕ 都经宿主的手（Focus.cs 替它发关闭），所以「30 秒内没点过 ✕」就一定不是你要关它
                bool clickedClose = (DateTime.Now - s.CloseClickedAt).TotalSeconds < 30;
                if (s.HiddenSince == DateTime.MinValue)
                {
                    s.HiddenSince = DateTime.Now;
                    Log.W("hidden " + s.Kind.Id + ": 窗口藏起来了（" + (clickedClose ? "你刚点了 ✕" : "你没点 ✕") + "）");
                }
                else if (!clickedClose && !s.Custom && !s.Kind.Cli && (DateTime.Now - s.HiddenSince).TotalSeconds > 1.5)
                {
                    // 你没点 ✕，是它自己藏的：留在格子里、显示回来，藏几次就显示几次。
                    // 以前两分钟内第三次就脱开成独立窗口 —— 可那个窗口还是藏着的，看着就是「Cursor 又自己关了」
                    // （2026-10-04：切走 Cursor 时它连藏三次，脱开后 21 分钟没影，直到用户自己再开一次）。
                    // 也不用等 5 秒：那是给「点了 ✕」留的，没点就没什么好等。
                    int n = (DateTime.Now - s.ReshownAt).TotalSeconds < 120 ? s.ReshownCount + 1 : 1;
                    Log.W("hidden " + s.Kind.Id + ": 你没点 ✕，显示回来" + (n > 1 ? "（两分钟内第 " + n + " 次）" : ""));
                    s.ReshownCount = n;
                    s.ReshownAt = DateTime.Now;
                    s.HiddenSince = DateTime.MinValue;
                    Native.ShowWindowAsync(s.Hwnd, 8);   // SW_SHOWNA：不抢焦点
                    _pulseDirty = true;
                    continue;
                }
                else if ((DateTime.Now - s.HiddenSince).TotalSeconds > 5)
                {
                    // 收进托盘了：脱开它，进观察期（下面 WatchClosing 处理）
                    Log.W("hidden " + s.Kind.Id + ": 藏了 5 秒，脱开，进 15 秒观察期");
                    IntPtr h = s.Hwnd;
                    uint pid;
                    Native.GetWindowThreadProcessId(h, out pid);
                    Embed.RestoreTopLevel(h, true);
                    Watch.Forget(h);
                    s.Hwnd = IntPtr.Zero;
                    s.Embedded = false;
                    s.HiddenSince = DateTime.MinValue;
                    _pulseDirty = true;
                    // 自选窗口不进「待关」观察期：那可能是用户的文件管理器、
                    // 浏览器，它自己藏起来是它的事，轮不到我们收它的进程树。
                    if (s.Custom) { RemoveSlot(s); i--; continue; }
                    if (s.Kind.Cli) { PlaceChrome(s); continue; }
                    s.ClosingSince = DateTime.Now;
                    s.ClosingPid = (int)pid;
                    s.ClosingByUser = (DateTime.Now - s.CloseClickedAt).TotalSeconds < 30;
                    if (s.Site != null && SlotShowing(s)) PlaceChrome(s);
                    continue;
                }
                // 藏着的这几秒别去核对尺寸：Fit 带 SWP_SHOWWINDOW，会把它又显示回来 ——
                // 用户点了 ✕ 看着像没反应（2026-09-26 实测：点 ✕ 后 0.4 秒它藏了，下一拍就被宿主显示回来）。
                continue;
            }
            else s.HiddenSince = DateTime.MinValue;
            if (s.Site == null) continue;
            // 队列视图里不在中间的格子 Site 是藏着的，但刚找到的窗口也得嵌进去（PlaceAll 会藏着嵌）——
            // 以前这里一律跳过藏着的，「＋ 新会话」开的终端排在侧栏，就一直「找到窗口，待嵌入」飘在外面。
            if (!s.Site.Visible)
            {
                if (_queueMode && !Embed.IsChildOf(s.Hwnd, s.Site.Handle) && !Embed.RepairChildStyle(s)) needPlace = true;
                continue;
            }
            if (!Embed.IsChildOf(s.Hwnd, s.Site.Handle))
            {
                // 多半只是它自己把 WS_CHILD 弄丢了，父窗口没变：轻修，别整页重排
                if (Embed.RepairChildStyle(s)) Log.W("style " + s.Kind.Id + ": 心跳发现 WS_CHILD 丢了，已装回");
                else needPlace = true;
            }
            else if (s.Embedded)
            {
                if (Embed.Misfit(s))
                {
                    // 头几拍硬压：Electron ready-to-show 时会按记住的旧尺寸改回去，
                    // 压两三次就服了。一直不服说明是它自己的 min/max 尺寸顶着，
                    // 再压只会闪，停手，把尺寸差显示出来。
                    if (s.MisfitStreak < 4) Embed.Fit(s);
                    s.MisfitStreak++;
                    PlaceChrome(s);
                }
                else if (s.MisfitStreak != 0)
                {
                    s.MisfitStreak = 0;
                    PlaceChrome(s);
                }
            }
        }
        if (needPlace && !waiting) Relayout(true);
        else if (waiting || _pulseDirty) Relayout(false);
        else SyncStackIfCoverChanged();
        _pulseDirty = false;
    }

    bool _pulseDirty;

    // 这一格的状态靠不靠 CPU 兜底：自己挑进来的窗口、名单里没钩子的桌面端、除 Claude/Codex 以外的 CLI 会话
    static bool UsesCpu(Slot s)
    {
        if (s.Custom) return true;
        AgentKind fam = Catalog.ById(s.Kind.FamilyId);
        // 装了全局钩子的几家：CLI 会话也会触发（钩子带 AGENTHOST_SLOT）
        if (fam == null || fam.DesktopState != "hooks") return true;
        // 钩子还没数据的（OpenCode 装了插件但还没重启加载）退回看 CPU，一有钩子数据就改听钩子的
        if (!fam.HookFallbackCpu) return false;
        int secs;
        return (s.Kind.Cli ? HookStates.Slot(s.Kind.Id, out secs) : HookStates.Desktop(s.Kind.FamilyId, out secs)) == null;
    }
}
