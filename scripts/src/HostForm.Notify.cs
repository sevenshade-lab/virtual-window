// 点了某家自己弹的系统通知（「任务完成」之类）→ 跳到那一格。
//
// 点通知时那家会「显示并聚焦自己的窗口」，可它的窗口挂在宿主里、所在格子正藏着（队列里不在中间、
// 或者放大了别的格子），这个请求没有任何可见效果，宿主也没人听 —— 用户 2026-09-26：
// 「点击通知里的 agent 信息无法跳转到对应的 agent」。
//
// 做法：听系统的焦点事件（WinEvent，out-of-context，回调走宿主消息循环）。焦点落进一个**看不见**的格子
// = 那家自己要露面（格子藏着，人点不到它），就把它换到前面。
// 防误判：有的客户端会在后台自己抢焦点（干完活把光标放回输入框），所以只在「宿主前一刻不在前台」
// 时才认 —— 点通知那一下前台是通知中心 / 别的程序；你正在宿主里打字时别家抢焦点一概不理。
// 宿主自己只会把焦点交给看得见的那一格，所以焦点落进藏着的格子不会是宿主引起的。

using System;
using System.Windows.Forms;

internal sealed partial class HostForm
{
    delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr mod, WinEventProc proc, uint pid, uint tid, uint flags);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool UnhookWinEvent(IntPtr hook);

    const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    const uint EVENT_OBJECT_FOCUS = 0x8005;
    const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    const uint WINEVENT_SKIPOWNPROCESS = 0x0002;

    WinEventProc _winEventProc;           // 留住委托，别被 GC 收了
    IntPtr _fgHook, _focusHook;
    DateTime _foreignFgAt = DateTime.MinValue;   // 最近一次前台不是自家的时刻

    // 每 40ms 看一眼嵌入窗口的样式还带不带 WS_CHILD（见 Embed.RepairChildStyle）。
    // GetWindowLong 只读窗口数据、不给对方发消息，客户端卡死也不会拖住宿主；几个格子一次不到 0.1ms。
    Timer _styleGuard;

    void StartStyleGuard()
    {
        if (_styleGuard != null) return;
        _styleGuard = new Timer { Interval = 40 };
        int tick = 0;
        _styleGuard.Tick += delegate
        {
            if (_closed) { _styleGuard.Stop(); return; }
            foreach (Slot s in _slots)
            {
                if (s.Kind.Cli || s.Custom || !s.Embedded) continue;
                if (Embed.RepairChildStyle(s)) Log.W("style " + s.Kind.Id + ": 它自己把 WS_CHILD 弄丢了，守卫计时器装回（事件没接住）");
            }
            if (++tick % 25 == 0) SyncLocationHooks();   // 约 1 秒核对一次要听哪些进程
            if (tick % 5 == 0) RepairCursorLevel();
        };
        _styleGuard.Start();
        SyncLocationHooks();
    }

    // 指针显示计数是整条输入队列共用的（宿主和嵌进来的几家 SetParent 之后就在一条队列上）。
    // 实测有线程把它压到 -1 不放回来（2026-09-28，开着「打字时隐藏指针」），
    // 结果整个宿主范围内都看不到指针，移出宿主才出现。宿主线程一直在这条队列上，由它补回 0。
    // 注意：从别的进程短暂 AttachThreadInput 进来补没用，一断开补的那份就跟着走了。
    void RepairCursorLevel()
    {
        int c = Native.ShowCursor(false) + 1;
        Native.ShowCursor(true);
        if (c >= 0) return;
        int from = c;
        while (c < 0) c = Native.ShowCursor(true);
        Log.W("cursor: 输入队列的指针显示计数被压到 " + from + "，已补回 " + c);
    }

    // 更快的那一路：Codex 改完样式约 3ms 就会发 EVENT_OBJECT_LOCATIONCHANGE（实测），
    // 听到就当场装回 WS_CHILD，不用等上面 40ms 那一拍。按进程注册（只听嵌进来的那几家），
    // 不注册全局 —— 全局的话鼠标每动一下（OBJID_CURSOR）都会回调一次。
    const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
    readonly System.Collections.Generic.Dictionary<uint, IntPtr> _locHooks = new System.Collections.Generic.Dictionary<uint, IntPtr>();
    // 同一批进程再听「窗口销毁 / 显示 / 隐藏」：前面那格的窗口自己藏了（点 ✕ 收进托盘）或关了，
    // 垫在底下的格子得马上藏起来，不然会从空掉的前面那格里露出来（HostForm.Stack.cs）
    const uint EVENT_OBJECT_DESTROY = 0x8001, EVENT_OBJECT_HIDE = 0x8003;
    readonly System.Collections.Generic.Dictionary<uint, IntPtr> _visHooks = new System.Collections.Generic.Dictionary<uint, IntPtr>();

    void SyncLocationHooks()
    {
        if (_winEventProc == null) _winEventProc = OnWinEvent;
        var want = new System.Collections.Generic.HashSet<uint>();
        foreach (Slot s in _slots)
        {
            if (s.Kind.Cli || s.Custom || !s.Embedded || s.Hwnd == IntPtr.Zero) continue;
            uint pid;
            Native.GetWindowThreadProcessId(s.Hwnd, out pid);
            if (pid != 0) want.Add(pid);
        }
        foreach (uint pid in new System.Collections.Generic.List<uint>(_locHooks.Keys))
            if (!want.Contains(pid)) { UnhookWinEvent(_locHooks[pid]); _locHooks.Remove(pid); }
        foreach (uint pid in new System.Collections.Generic.List<uint>(_visHooks.Keys))
            if (!want.Contains(pid)) { UnhookWinEvent(_visHooks[pid]); _visHooks.Remove(pid); }
        foreach (uint pid in want)
        {
            if (!_locHooks.ContainsKey(pid))
            {
                IntPtr hk = SetWinEventHook(EVENT_OBJECT_LOCATIONCHANGE, EVENT_OBJECT_LOCATIONCHANGE, IntPtr.Zero, _winEventProc, pid, 0, WINEVENT_OUTOFCONTEXT);
                if (hk != IntPtr.Zero) _locHooks[pid] = hk;
            }
            if (!_visHooks.ContainsKey(pid))
            {
                IntPtr hk = SetWinEventHook(EVENT_OBJECT_DESTROY, EVENT_OBJECT_HIDE, IntPtr.Zero, _winEventProc, pid, 0, WINEVENT_OUTOFCONTEXT);
                if (hk != IntPtr.Zero) _visHooks[pid] = hk;
            }
        }
    }

    void StartNotifyWatch()
    {
        StartStyleGuard();
        // 以前按 _winEventProc != null 判「装过了」，可 SyncLocationHooks 也会先把它建出来 ——
        // 只要它先跑，前台 / 焦点钩子就一次都没装上，点通知跳转从来没生效过（2026-09-27 诊断日志抓到）。
        if (_fgHook != IntPtr.Zero) return;
        if (_winEventProc == null) _winEventProc = OnWinEvent;
        _fgHook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _winEventProc, 0, 0, WINEVENT_OUTOFCONTEXT);
        _focusHook = SetWinEventHook(EVENT_OBJECT_FOCUS, EVENT_OBJECT_FOCUS, IntPtr.Zero, _winEventProc, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
    }

    void StopNotifyWatch()
    {
        if (_fgHook != IntPtr.Zero) UnhookWinEvent(_fgHook);
        if (_focusHook != IntPtr.Zero) UnhookWinEvent(_focusHook);
        _fgHook = _focusHook = IntPtr.Zero;
        foreach (IntPtr hk in _locHooks.Values) UnhookWinEvent(hk);
        _locHooks.Clear();
        foreach (IntPtr hk in _visHooks.Values) UnhookWinEvent(hk);
        _visHooks.Clear();
    }

    void OnWinEvent(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (_closed || hwnd == IntPtr.Zero) return;
        try
        {
            if (ev == EVENT_SYSTEM_FOREGROUND)
            {
                // 点通知时 Antigravity、Codex 不设焦点，只对自己的窗口 SetForegroundWindow ——
                // 系统发一条「前台 = 格子里那个子窗口」，下一拍前台就落回宿主（2026-09-27 探针抓到，
                // 以前只听焦点事件，于是点通知不跳）。前台刚从外面（通知中心 / 资源管理器）过来才算。
                // 格子自己的窗口不算「外面」：Codex 切页面时会短暂丢掉 WS_CHILD，IsChild 判不出来，
                // 以前被当成外面来的前台，把「刚从外面过来」的窗口期一直续上
                Slot fs = SlotOwning(hwnd);
                if (fs == null && hwnd != Handle && !Native.IsChild(Handle, hwnd)) { _foreignFgAt = DateTime.Now; return; }
                double fgAgo = (DateTime.Now - _foreignFgAt).TotalMilliseconds;
                if (fs == null || SlotShowing(fs)) return;
                if (fgAgo >= 2000) { Log.W("notify " + fs.Kind.Id + ": 藏着的格子成了前台，但前台 " + (int)Math.Min(fgAgo, 99999) + "ms 前就不在外面了，不跳"); return; }
                Log.W("notify " + fs.Kind.Id + ": 藏着的这一格把自己设成前台（多半点了它的通知），换到前面");
                BeginInvoke((MethodInvoker)delegate { JumpTo(fs); });
                return;
            }
            if (ev >= EVENT_OBJECT_DESTROY && ev <= EVENT_OBJECT_HIDE)
            {
                if (idObject != 0) return;   // 只看窗口本身
                foreach (Slot vs in _slots)
                    if (vs.Hwnd == hwnd) { BeginInvoke((MethodInvoker)SyncStackIfCoverChanged); break; }
                return;
            }
            if (ev == EVENT_OBJECT_LOCATIONCHANGE)
            {
                if (idObject != 0) return;   // 只看窗口本身
                foreach (Slot ls in _slots)
                    if (ls.Hwnd == hwnd)
                    {
                        if (Embed.RepairChildStyle(ls)) Log.W("style " + ls.Kind.Id + ": 它自己把 WS_CHILD 弄丢了（切页面），事件一到当场装回");
                        break;
                    }
                return;
            }
            // 焦点事件：只看窗口本身（OBJID_CLIENT / 窗口），落在哪一格
            Slot s = SlotOwning(hwnd);
            if (s == null || SlotShowing(s)) return;
            // 宿主只会把焦点交给看得见的那一格，焦点落进藏着的格子不可能是宿主自己交的，
            // 不用再按「宿主刚交过焦点」挡 —— 那样会连真的点通知一起挡掉：点通知时宿主先被激活、
            // 先把焦点还给原来那格，紧跟着那家才聚焦自己，正好落在窗口期里。
            double ago = (DateTime.Now - _foreignFgAt).TotalMilliseconds;
            if (ago > 2000)
            {
                Log.W("notify " + s.Kind.Id + ": 藏着的格子自己抢焦点，但宿主一直在前台（" + (int)Math.Min(ago, 99999) + "ms 前才不是），不跳");
                return;
            }
            Log.W("notify " + s.Kind.Id + ": 焦点落进藏着的这一格（多半点了它的通知），换到前面");
            BeginInvoke((MethodInvoker)delegate { JumpTo(s); });
        }
        catch { }
    }

    Slot SlotOwning(IntPtr hwnd)
    {
        foreach (Slot s in _slots)
        {
            if (s.Hwnd == IntPtr.Zero || !s.Embedded) continue;
            if (hwnd == s.Hwnd || IsDescendant(hwnd, s.Hwnd)) return s;
        }
        return null;
    }

    // 这一格现在看不看得见（队列里在中间 / 网格里它的格子显示着）
    bool SlotShowing(Slot s)
    {
        if (_queueMode) return s == QueueFront;
        return s.Site != null && s.Site.Visible && s.Pane.Width > 0;
    }

    void JumpTo(Slot s)
    {
        if (_closed || !_slots.Contains(s)) return;
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        if (_queueMode) PinFront(s);
        else if (_solo != null && _solo != s) ToggleSolo(s);
        // 没换上来（比如被在问你的那家挤回去了）就别把焦点塞进藏着的格子：
        // 它一拿焦点又报「藏着的格子成了前台」→ 又 JumpTo，2026-09-28 这样空转了 46 秒、25 轮动画，界面像卡死
        if (!SlotShowing(s)) { Log.W("jump " + s.Kind.Id + ": 没换到前面，不交焦点"); return; }
        if (s.Hwnd != IntPtr.Zero) { _focusByUser = true; GiveFocus(s.Hwnd, true); }
        try { Native.SetForegroundWindow(Handle); } catch { }
    }
}
