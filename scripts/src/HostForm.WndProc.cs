// 【拆分】窗口消息：自绘标题栏、Snap、激活/点击时的焦点处理。（从原 AgentHost.cs 原样搬出，逻辑未改）

using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

internal sealed partial class HostForm
{
    // 标题栏和工具栏合成一条：把系统标题栏并进客户区（NCCALCSIZE 不留标题高度），
    // 三个窗口按钮由上面的自绘控件处理，工具条空白处当标题栏拖动。
    // 最大化时窗口会超出屏幕一圈边框，顶上要补回这一圈，不然内容被裁。
    int FrameY()
    {
        return Native.GetSystemMetrics(33 /* SM_CYSIZEFRAME */) + Native.GetSystemMetrics(92 /* SM_CXPADDEDBORDER */);
    }

    protected override void WndProc(ref Message m)
    {
        bool trace = m.Msg == Native.WM_ACTIVATE || m.Msg == Native.WM_MOUSEACTIVATE
            || m.Msg == Native.WM_SETFOCUS || m.Msg == Native.WM_KILLFOCUS
            || (m.Msg == Native.WM_PARENTNOTIFY && ((int)m.WParam & 0xFFFF) == Native.WM_LBUTTONDOWN);
        // Diagnostics only: retain the original dispatch order and never change focus here.
        if (trace) Log.W("CLICKTRACE before msg=" + m.Msg.ToString("X") + FocusTraceState());
        try { DispatchHostMessage(ref m); }
        finally
        {
            if (trace) Log.W("CLICKTRACE after msg=" + m.Msg.ToString("X")
                + " result=" + m.Result.ToInt64().ToString("X") + FocusTraceState());
        }
    }

    void DispatchHostMessage(ref Message m)
    {
        // Both entry points use a native popup and the normal WM_SYSCOMMAND path.
        if (m.Msg == 0x00A5 /* WM_NCRBUTTONUP */ && (int)m.WParam == 2 /* HTCAPTION */)
        {
            ShowWindowMenu(new Point((short)((long)m.LParam & 0xFFFF), (short)(((long)m.LParam >> 16) & 0xFFFF)));
            m.Result = IntPtr.Zero;
            return;
        }
        if (m.Msg == 0x0112 && ((int)m.WParam & 0xFFF0) == 0xF100 && m.LParam == (IntPtr)0x20)
        {
            ShowWindowMenu(PointToScreen(new Point(0, ToolbarH)));
            m.Result = IntPtr.Zero;
            return;
        }
        if (m.Msg == 0x00A0 /* WM_NCMOUSEMOVE */ && _maxBtn != null)
        {
            _maxBtn.CaptionHover((int)m.WParam == 9 /* HTMAXBUTTON */);
            if ((int)m.WParam != 9) _captionMaxDown = false;
            var tracking = new Native.TRACKMOUSEEVENT();
            tracking.Size = (uint)Marshal.SizeOf(tracking);
            tracking.Flags = 0x12; // TME_LEAVE | TME_NONCLIENT
            tracking.Hwnd = Handle;
            Native.TrackMouseEvent(ref tracking);
        }
        if (m.Msg == 0x02A2 /* WM_NCMOUSELEAVE */ && _maxBtn != null)
        {
            _maxBtn.CaptionHover(false);
            _captionMaxDown = false;
        }
        if ((m.Msg == 0x00A1 || m.Msg == 0x00A3) && (int)m.WParam == 9)
        {
            // Keep Snap's nonclient hit target, but avoid DefWindowProc drawing
            // and tracking a second native caption button at its default position.
            _captionMaxDown = true;
            m.Result = IntPtr.Zero;
            return;
        }
        if (m.Msg == 0x00A2 && (int)m.WParam == 9)
        {
            bool pressed = _captionMaxDown;
            _captionMaxDown = false;
            if (pressed) WindowState = WindowState == FormWindowState.Maximized
                ? FormWindowState.Normal : FormWindowState.Maximized;
            m.Result = IntPtr.Zero;
            return;
        }
        if (m.Msg == 0x0202 /* WM_LBUTTONUP */ || m.Msg == 0x001F /* WM_CANCELMODE */)
            _captionMaxDown = false;
        if (m.Msg == Native.WM_HOTKEY)
        {
            if ((int)m.WParam == WindowMenuKey)
            {
                IntPtr fg = Native.GetForegroundWindow();
                if (fg == Handle || Native.IsChild(Handle, fg))
                    ShowWindowMenu(PointToScreen(new Point(0, ToolbarH)));
                return;
            }
            int id = (int)m.WParam - HotkeyBase;
            if (id >= 0 && id <= MaxSlots) { HotkeyCell(id); return; }
            int alt = (int)m.WParam - AltKeyBase;
            if (alt >= 0 && alt < AltCellKeys.Length) HotkeyCell(alt + 1);
            else if (alt == 100) CycleCell();
            else if (alt == 101) HotkeyCell(0);
            return;
        }
        if (m.Msg == 0x001F /* WM_CANCELMODE */) EndSlotDrag(false);
        if (m.Msg == Program.WM_APP_CLAIM)
        {
            ClaimFromOutside();
            return;
        }
        if (m.Msg == Program.WM_APP_CYCLE)
        {
            CycleLayout();
            return;
        }
        if (m.Msg == Program.WM_APP_VIEW)
        {
            ToggleQueueMode();
            return;
        }
        if (m.Msg == Program.WM_APP_ZOOM)
        {
            Slot z = _queueMode ? QueueFront : (_solo ?? _slots.Find(x => x.Hwnd != IntPtr.Zero && x.Hwnd == _lastFocused));
            if (z != null) ToggleZoom(z);
            return;
        }
        if (m.Msg == Program.WM_APP_ROSTER)
        {
            BeginInvoke((MethodInvoker)ShowRoster);
            return;
        }
        // 只收起：把四家还成独立窗口并最小化，**不关它们**（外部程序要「收起」时发这条）。
        // 直接 WM_CLOSE 现在等于「连客户端一起关」，两种意图得分开。
        if (m.Msg == Program.WM_APP_COLLAPSE)
        {
            _collapseOnly = true;
            Close();
            return;
        }
        // 系统切了深浅色。以前只在启动时读一次注册表，切完得重开宿主。
        if (m.Msg == 0x001A /* WM_SETTINGCHANGE */)
        {
            try
            {
                string area = m.LParam != IntPtr.Zero ? Marshal.PtrToStringUni(m.LParam) : null;
                if (area == "ImmersiveColorSet") OnSystemColorsChanged();
            }
            catch { }
            base.WndProc(ref m);
            return;
        }
        if (m.Msg == Native.WM_NCCALCSIZE && m.WParam != IntPtr.Zero)
        {
            int top = Marshal.ReadInt32(m.LParam, 4);
            base.WndProc(ref m);
            Marshal.WriteInt32(m.LParam, 4, WindowState == FormWindowState.Maximized ? top + FrameY() : top);
            m.Result = IntPtr.Zero;
            return;
        }
        // 自定义边框的窗口最大化时，显式把最大化范围限到工作区
        // （再各边多出一圈边框，跟系统最大化一样，边框落到屏幕外）。
        if (m.Msg == Native.WM_GETMINMAXINFO)
        {
            base.WndProc(ref m);
            IntPtr mon = Native.MonitorFromWindow(Handle, 2 /* MONITOR_DEFAULTTONEAREST */);
            var mi = new Native.MONITORINFO();
            mi.cbSize = Marshal.SizeOf(mi);
            if (mon != IntPtr.Zero && Native.GetMonitorInfo(mon, ref mi))
            {
                int fx = Native.GetSystemMetrics(32 /* SM_CXSIZEFRAME */) + Native.GetSystemMetrics(92);
                int fy = FrameY();
                Native.RECT w = mi.rcWork, r = mi.rcMonitor;
                Marshal.WriteInt32(m.LParam, 8, w.Width + fx * 2);
                Marshal.WriteInt32(m.LParam, 12, w.Height + fy * 2);
                Marshal.WriteInt32(m.LParam, 16, w.Left - r.Left - fx);
                Marshal.WriteInt32(m.LParam, 20, w.Top - r.Top - fy);
            }
            m.Result = IntPtr.Zero;
            return;
        }
        if (m.Msg == Native.WM_NCHITTEST)
        {
            Point screen = new Point((short)((long)m.LParam & 0xFFFF), (short)(((long)m.LParam >> 16) & 0xFFFF));
            if (_maxBtn != null && _maxBtn.Visible && _maxBtn.Bounds.Contains(PointToClient(screen)))
            {
                m.Result = (IntPtr)9; // HTMAXBUTTON: preserve Windows maximize and Snap handling.
                return;
            }
            IntPtr dwm;
            if (Native.DwmDefWindowProc(Handle, m.Msg, m.WParam, m.LParam, out dwm))
            {
                m.Result = dwm;
                return;
            }
            base.WndProc(ref m);
            if ((int)m.Result == 1 /* HTCLIENT */)
            {
                int lp = unchecked((int)(long)m.LParam);
                Point pt = PointToClient(new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF)));
                if (WindowState != FormWindowState.Maximized && pt.Y < FrameY())
                    m.Result = (IntPtr)12; // HTTOP
                else if (pt.Y < ToolbarH + D(6))
                    m.Result = (IntPtr)2;  // HTCAPTION
            }
            return;
        }
        if (m.Msg == Native.WM_PARENTNOTIFY)
        {
            int ev = (int)m.WParam & 0xFFFF;
            // 当场交焦点，不 BeginInvoke：这条消息是客户端取到鼠标按下、还没处理之前同步发来的，
            // 在这里做完 Reattach，它处理这次点击时输入法已经接好了；放到之后做就是赛跑
            // （2026-09-20 实测 BeginInvoke 版 9 次里错 1~2 次），还会把这次点击弹出的菜单收掉。
            if (ev == Native.WM_LBUTTONDOWN || ev == Native.WM_RBUTTONDOWN || ev == Native.WM_MBUTTONDOWN)
            {
                Log.W("PARENTNOTIFY 按下 ev=" + ev.ToString("X"));
                if (ev == Native.WM_LBUTTONDOWN && CaptionButtonClick()) return;
                FocusUnderCursor();
            }
        }
        else if (m.Msg == Native.WM_ACTIVATE)
        {
            int state = (int)m.WParam & 0xFFFF;
            // Release Alt+Space immediately on leaving; do not wait for the heartbeat.
            SyncWindowMenuHotkey(state != 0 || m.LParam == Handle || Native.IsChild(Handle, m.LParam));
            Log.W("WM_ACTIVATE state=" + state + " to=" + m.LParam.ToInt64().ToString("X")
                  + " lastFocused=" + _lastFocused.ToInt64().ToString("X"));
            // 是不是"从外面回来"：看上一个激活窗口（lParam）是不是自己家的。
            // 在自家窗口里点一下也会来一条 state=2（WA_CLICKACTIVE），那种不算回来 ——
            // 当成回来就会多发一次失活，把这次点击刚弹出来的菜单关掉。
            bool fromOutside = true;
            if (state != 0)
            {
                IntPtr prev = m.LParam;
                if (prev != IntPtr.Zero && (prev == Handle || Native.IsChild(Handle, prev)))
                    fromOutside = false;
                if (fromOutside && prev != IntPtr.Zero)
                {
                    uint ppid;
                    Native.GetWindowThreadProcessId(prev, out ppid);
                    for (int i = 0; i < _slots.Count && fromOutside; i++)
                    {
                        Slot s3 = _slots[i];
                        if (s3.Hwnd == IntPtr.Zero || !Native.IsWindow(s3.Hwnd)) continue;
                        uint cpid;
                        Native.GetWindowThreadProcessId(s3.Hwnd, out cpid);
                        if (cpid == ppid) fromOutside = false;
                    }
                }
            }
            if (state != 0 && !fromOutside) Log.W("  自家窗口之间的激活，不补焦点");
            // Clicking an already-focused embedded client reactivates its top-level
            // host. Form/DefWindowProc then focuses the host, undoing PARENTNOTIFY.
            // Four-client trace: WM_ACTIVATE(2) enters with client focus and exits
            // with host focus. Consume only this internal click activation; do not
            // synthesize another focus/IME cycle or change the working hover path.
            if (state == 2 && !fromOutside && !_menuOpen)
            {
                IntPtr focused = Native.GetFocus();
                for (int i = 0; i < _slots.Count; i++)
                {
                    Slot s = _slots[i];
                    if (!s.Embedded || s.Hwnd == IntPtr.Zero || s.Site == null || !SlotShowing(s)) continue;
                    if (!Embed.IsChildOf(s.Hwnd, s.Site.Handle)) continue;
                    if (focused != s.Hwnd && !Native.IsChild(s.Hwnd, focused)) continue;
                    Log.W("CLICKFIX preserve embedded click focus cell=" + s.Kind.Id + FocusTraceState());
                    m.Result = IntPtr.Zero;
                    return;
                }
            }
            if (state != 0 && fromOutside && _lastFocused != IntPtr.Zero)
            {
                // 排一个「把焦点还给刚才那一格」。但**要记下排队时想给谁**：
                // 点另一格时系统先给宿主发激活、再发这次点击的 WM_PARENTNOTIFY，
                // 于是这个异步动作会在"焦点已经交给新格子"之后才跑 ——
                // 不判断的话它会把焦点抢回上一格，你点的那格当场失焦，
                // 页面里刚弹出来的菜单就没了（2026-09-20 用户报 Claude 三条杠、
                // Codex「我的账户」一闪就消失）。
                IntPtr want = _lastFocused;
                BeginInvoke(new Action(delegate
                {
                    if (_lastFocused != want) { Log.W("  激活后补焦点：跳过（期间换过格子）"); return; }
                    Log.W("  激活后补焦点 -> " + want.ToInt64().ToString("X"));
                    GiveFocus(want, true, true);
                }));
            }
            else if (state == 0)
            {
                // 宿主失去激活（最小化、切去别的程序）：给当前这一格发一次失活。
                // 少了这一下，「最小化→还原→直接打字」时我们只补了激活，
                // 对 Chromium 来说没有状态变化，它不会重新登记输入法，
                // 拼音就掉到屏幕左上角（2026-09-20 用户报，只有 Claude 那家自己能恢复）。
                //
                // 但要排除「激活的是自家格子」：我们把焦点交给某一格时，
                // 系统眼里的激活窗口就成了那一格，宿主随即收到这条消息 ——
                // 那种情况下再发失活，等于自己把自己刚点亮的格子关掉。
                IntPtr to = m.LParam;
                bool ours = to != IntPtr.Zero && (to == Handle || Native.IsChild(Handle, to));
                // 客户端弹自己的顶层小窗（菜单、账户弹窗）时，激活也会离开宿主。
                // 那不是"你切走了"，是它自己的弹窗 —— 这时候发失活会把弹窗关掉。
                if (!ours && to != IntPtr.Zero)
                {
                    uint topid;
                    Native.GetWindowThreadProcessId(to, out topid);
                    for (int i = 0; i < _slots.Count && !ours; i++)
                    {
                        Slot s2 = _slots[i];
                        if (s2.Hwnd == IntPtr.Zero || !Native.IsWindow(s2.Hwnd)) continue;
                        uint cpid;
                        Native.GetWindowThreadProcessId(s2.Hwnd, out cpid);
                        if (cpid == topid) ours = true;
                    }
                }
                if (!ours && _lastFocused != IntPtr.Zero && Native.IsWindow(_lastFocused))
                {
                    if (_reTimer != null) _reTimer.Stop();
                    _rePending = IntPtr.Zero;
                    Activate(_lastFocused, false);
                }
            }
        }
        base.WndProc(ref m);
    }
}
