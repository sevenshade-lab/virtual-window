// 【拆分】焦点与输入法：交焦点、补发激活、悬停切格。（从原 AgentHost.cs 原样搬出，逻辑未改）

using System;
using System.Drawing;
using System.Windows.Forms;

internal sealed partial class HostForm
{
    // 排版一变，原来有焦点的那格可能被藏起来了（放大别的格、换排版、把它移出）。
    // 焦点留在看不见的窗口上：键盘打进隐藏的格子，输入法也没有落点，拼音掉屏幕左上角
    // —— 2026-09-20 用户报「放大 OpenCode 之后输入法又跑左上角」，实测就是这个。
    // 所以：焦点那格还看得见就不动；看不见了就交给放大的那格，没有放大就给第一个可见的。
    void EnsureFocusVisible()
    {
        Slot keep = null, first = null;
        for (int i = 0; i < _slots.Count; i++)
        {
            Slot s = _slots[i];
            if (!s.Embedded || s.Hwnd == IntPtr.Zero || !Native.IsWindow(s.Hwnd)) continue;
            // 看的是「正在显示的那一格」，不是 Site.Visible：垫在前面那格底下的格子也是可见的（HostForm.Stack.cs），
            // 焦点留在那种被挡住的窗口上，字就打进看不见的格子里了
            if (s.Site == null || !SlotShowing(s) || s.Pane.Width <= 0) continue;
            if (first == null) first = s;
            if (s.Hwnd == _lastFocused) keep = s;
        }
        if (keep != null || _menuOpen) return;
        Slot t = (_solo != null && _solo.Embedded && _solo.Site != null && SlotShowing(_solo)) ? _solo : first;
        if (t != null)
        {
            Log.W("EnsureFocusVisible 换焦点 -> " + t.Kind.Id);
            _focusByUser = false;
            GiveFocus(t.Hwnd, true);
        }
    }

    // 跨进程父子窗口会把双方的输入队列绑在一起：鼠标能到子窗口，
    // 但键盘焦点默认落在宿主这边的 WinForms 控件上，所以 Cursor 能点不能打字。
    // 做法：谁被点了就把 SetFocus 交给它；宿主激活时也把焦点还给最近一次点过的格子。
    IntPtr _lastFocused = IntPtr.Zero;
    // 这次焦点是不是真人点出来的。开机、换排版时宿主会自己补焦点，
    // 那种不能算「你在看这一格」，否则黄灯会被自己灭掉。
    bool _focusByUser;

    // 一 悬停切格 一一一一一一一一一一一一一一一一一一一一一一一一一
    //
    // 鼠标移进某一格、停住一下就把焦点交过去。除了省掉按键，还有一个副作用正是
    // 想要的：切格发生在**你点输入框之前**，宿主就有几百毫秒去做输入法重挂 ——
    // 等你真的点下去，那已经是「第二下点击」，正好复刻「点空白再点输入框」。
    // 靠点击触发的补发永远在跟这一下点击赛跑，还要防着把刚弹出的菜单收掉。
    //
    // 三条防「闹鬼」：鼠标必须真的动过（不是停在那儿不动被抢走）；必须在同一格
    // 停够 250 毫秒（横穿过去不算）；菜单开着不抢。
    Timer _hover;
    Slot _hoverAt;
    DateTime _hoverSince;
    Native.POINT _lastCursor;
    bool _mouseMoved;

    void HoverTick()
    {
        SpaceTimeout();
        if (!Prefs.Get("hoverfocus", true) || !_hostActive || _menuOpen || _dragSlot != null) { _hoverAt = null; return; }
        Native.POINT p;
        if (!Native.GetCursorPos(out p)) return;
        if (p.X != _lastCursor.X || p.Y != _lastCursor.Y)
        {
            _lastCursor = p;
            _mouseMoved = true;
        }
        IntPtr under = Native.WindowFromPoint(p);
        Slot hit = null;
        for (int i = 0; i < _slots.Count && under != IntPtr.Zero; i++)
        {
            Slot s2 = _slots[i];
            if (s2.Hwnd == IntPtr.Zero || !s2.Embedded) continue;
            if (under == s2.Hwnd || IsDescendant(under, s2.Hwnd)) { hit = s2; break; }
        }
        if (hit != _hoverAt) { _hoverAt = hit; _hoverSince = DateTime.Now; return; }
        if (hit == null || !_mouseMoved) return;
        if (hit.Hwnd == _lastFocused) return;
        if ((DateTime.Now - _hoverSince).TotalMilliseconds < 250) return;
        _mouseMoved = false;           // 交完就别再抢，直到鼠标又动起来
        Seen.Touch(hit.Kind.Id);
        hit.DoneUnseen = false;
        RefreshAgentIndicator(hit);
        _focusByUser = true;
        GiveFocus(hit.Hwnd);
    }

    string FocusTraceState()
    {
        IntPtr focus = Native.GetFocus();
        string cell = "none";
        for (int i = 0; i < _slots.Count; i++)
        {
            Slot s = _slots[i];
            if (s.Hwnd != IntPtr.Zero && (focus == s.Hwnd || Native.IsChild(s.Hwnd, focus)))
            { cell = s.Kind.Id; break; }
        }
        return " focus=" + focus.ToInt64().ToString("X") + " cell=" + cell
            + " host=" + Handle.ToInt64().ToString("X")
            + " foreground=" + Native.GetForegroundWindow().ToInt64().ToString("X")
            + " last=" + _lastFocused.ToInt64().ToString("X");
    }

    void FocusUnderCursor()
    {
        Native.POINT p;
        if (!Native.GetCursorPos(out p)) return;
        IntPtr under = Native.WindowFromPoint(p);
        if (under == IntPtr.Zero) return;
        for (int i = 0; i < _slots.Count; i++)
        {
            Slot s = _slots[i];
            if (s.Hwnd == IntPtr.Zero || !s.Embedded) continue;
            if (under == s.Hwnd || IsDescendant(under, s.Hwnd))
            {
                // 「看过」只算真人点进来：开机自动交焦点、换排版时补焦点都不算，
                // 否则重启一次所有黄灯就全灭了。
                Seen.Touch(s.Kind.Id);
                s.DoneUnseen = false;
                RefreshAgentIndicator(s);
                _focusByUser = true;
                GiveFocus(s.Hwnd);
                return;
            }
        }
    }

    // 嵌进来的桌面端右上角那三个钮（– □ ✕）：Electron 在网页层自己画的（问它 WM_NCHITTEST 回的是 HTCLIENT），
    // 嵌成子窗口后时灵时不灵 —— 2026-09-26 实测点 ✕ 一次收进托盘、一次毫无反应。按位置由宿主接管：
    //   ✕ = 发 WM_CLOSE（它收进托盘 → 心跳「待关」20 秒后收掉进程，跟单独开时点 ✕ 退出一样）
    //   □ = 宿主最大化 / 还原；– = 宿主最小化（让子窗口自己最小化只会缩成格子里一个小方块）。
    // 终端、自选窗口不管：那里右上角不一定是这三个钮。
    bool CaptionButtonClick()
    {
        Native.POINT p;
        if (!Native.GetCursorPos(out p)) return false;
        IntPtr under = Native.WindowFromPoint(p);
        if (under == IntPtr.Zero) return false;
        foreach (Slot s in _slots)
        {
            if (s.Hwnd == IntPtr.Zero || !s.Embedded || s.Kind.Cli || s.Custom) continue;
            if (under != s.Hwnd && !IsDescendant(under, s.Hwnd)) continue;
            Native.RECT r;
            if (!Native.GetWindowRect(s.Hwnd, out r)) return false;
            // 高 30：标准标题栏按钮的高度。设成 40 会盖到 Codex 标题栏下面那排工具钮（分享、面板切换，
            // 上沿约 32）的上半截 —— 点它们会被当成 – □ ✕
            int bw = (int)Math.Round(46 * _scale), bh = (int)Math.Round(30 * _scale);
            if (p.Y < r.Top || p.Y >= r.Top + bh || p.X < r.Right - 3 * bw || p.X >= r.Right) return false;
            int k = (r.Right - 1 - p.X) / bw;   // 0=✕ 1=□ 2=–
            if (k == 0)
            {
                Log.W("caption " + s.Kind.Id + ": 点了 ✕，替它发关闭");
                s.CloseClickedAt = DateTime.Now;
                Native.PostMessage(s.Hwnd, (int)Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            }
            else if (k == 1)
            {
                Log.W("caption " + s.Kind.Id + ": 点了 □，宿主最大化/还原");
                BeginInvoke((MethodInvoker)delegate { WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized; });
            }
            else
            {
                Log.W("caption " + s.Kind.Id + ": 点了 –，宿主最小化");
                BeginInvoke((MethodInvoker)delegate { WindowState = FormWindowState.Minimized; });
            }
            return true;
        }
        return false;
    }

    static bool IsDescendant(IntPtr h, IntPtr ancestor)
    {
        int guard = 0;
        while (h != IntPtr.Zero && guard++ < 64)
        {
            if (h == ancestor) return true;
            h = Native.GetParent(h);
        }
        return false;
    }

    // 焦点与输入法的正确交接。
    //
    // 两个症状同源：(a) 四个格子同时闪光标 —— 各子窗口跨进程、各自线程有独立的
    // "焦点"概念，宿主没告诉失焦的那三家"你没焦点了"；(b) 打不出中文、候选框飘到
    // 屏幕左上角 —— 输入法上下文(HIMC)挂在了宿主线程上，中文组合完送不到子窗口。
    //
    // 做法：
    //  1. 给其它三家发 WM_KILLFOCUS，让它们把光标收掉；
    //  2. 短暂 AttachThreadInput 到目标线程，SetFocus 过去后**立刻**断开 ——
    //     不长时间合并输入队列，就不会把 IME 拽到宿主线程；
    //  3. 用 ImmAssociateContext 把目标窗口自己的 HIMC 显式关联回去，
    //     再发 WM_IME_SETCONTEXT 让它重开输入法 UI（候选框就会跟着它走）；
    //  4. 宿主自己 ImeMode = Disable，永远不接管输入法。
    //  5. （2026-09-19 Claude）实测宿主线程与子窗口线程的输入状态一直是连着的
    //     （GetGUIThreadInfo 看宿主线程的焦点就是子窗口），候选框仍飘到左上角。
    //     嵌进来的 Chromium 窗口成了 WS_CHILD，再也收不到 WM_ACTIVATE，它自己记着
    //     「窗口没激活」，就不把输入框交给输入法（TSF），拼音只能落到默认位置。
    //     所以交焦点时替系统补发激活/失活，谁有焦点谁就是「激活的窗口」。
    // 焦点圈换了家：只把这两格的范围标脏，别整窗重画（宿主跟四家共用输入队列）。
    void RepaintFocusRing(IntPtr a, IntPtr b)
    {
        if (a == b) return;
        for (int i = 0; i < _slots.Count; i++)
        {
            Slot s = _slots[i];
            if (s.Pane.Width <= 0) continue;
            if (s.Hwnd == a || s.Hwnd == b) Invalidate(Rectangle.Inflate(s.Pane, D(4), D(4)));
        }
    }

    void GiveFocus(IntPtr h) { GiveFocus(h, false, false); }

    void GiveFocus(IntPtr h, bool relink) { GiveFocus(h, relink, false); }

    // relink：宿主刚被切回前台，输入法跟着别的程序走了，要让这一格重新接上。
    void GiveFocus(IntPtr h, bool relink, bool full)
    {
        if (h == IntPtr.Zero || !Native.IsWindow(h)) return;
        // 菜单开着就别动焦点，不然菜单会被顶掉（用户点「启动 ▾」菜单时就撞上过）
        if (_menuOpen) return;
        // 同一格里接着点、焦点也还在它身上：什么都不用做。
        // 每次点击都重走一遍「让别家失焦 + SetActiveWindow + SetFocus」是多余的，
        // 而且会打扰客户端自己的弹窗（2026-09-20 用户报 Claude 的三条杠、
        // Codex 的「我的账户」弹窗一闪就没）。
        if (!relink && h == _lastFocused)
        {
            IntPtr cur = Native.GetFocus();
            if (cur == h || IsDescendant(cur, h)) { Log.W("  GiveFocus 同格且已有焦点 -> 什么都不做"); return; }
        }
        Log.W("  GiveFocus -> " + h.ToInt64().ToString("X") + " relink=" + relink + " full=" + full
              + " reattachWillRun=" + (relink || _lastFocused != h));
        // 换格（或宿主刚回前台）才需要让它重新接输入法；同一格里接着点不用 ——
        // 失活再激活会把它刚弹出来的菜单收掉。
        bool reattach = relink || _lastFocused != h;
        IntPtr was = _lastFocused;
        _lastFocused = h;
        RepaintFocusRing(was, h);
        ActiveControl = null;

        // 1. 其它格子失焦
        for (int i = 0; i < _slots.Count; i++)
        {
            Slot s = _slots[i];
            if (s.Hwnd == IntPtr.Zero || !s.Embedded || s.Hwnd == h) continue;
            Native.PostMessage(s.Hwnd, Native.WM_KILLFOCUS, h, IntPtr.Zero);
            Native.PostMessage(s.Hwnd, Native.WM_IME_SETCONTEXT, IntPtr.Zero, IntPtr.Zero);
            Native.PostMessage(s.Hwnd, Native.WM_NCACTIVATE, IntPtr.Zero, IntPtr.Zero);
            // 让页面里那个可编辑元素**真的**失焦一次。
            //
            // 上面这些消息发的都是外层 Chrome_WidgetWin_1，而真正收 WM_SETFOCUS /
            // WM_KILLFOCUS、也是输入法挂载点的，是它里面那个 Chrome_RenderWidgetHostHWND
            // （实测四个格子底下都有一个）。以前从来没碰过它。
            //
            // 用户 2026-09-20 报「点空白处再点输入框 100% 能修好」—— 那一下走的正是
            // 这条路：页面里的输入框真的失焦又聚焦了一次，Blink 才会重挂输入法上下文。
            // 而「伪造窗口激活」（Activate 那套摘 WS_CHILD 位的办法）绕了一大圈，
            // 所以时灵时不灵。
            //
            // 只对**离开的**那一格做。给正在进入的那格发失焦会把它刚弹出来的菜单
            // 收掉（09-20 已经踩过一次），而离开的那格上不可能有刚弹出的菜单。
            IntPtr inner = Native.FindWindowEx(s.Hwnd, IntPtr.Zero, "Chrome_RenderWidgetHostHWND", null);
            if (inner != IntPtr.Zero) Native.PostMessage(inner, Native.WM_KILLFOCUS, h, IntPtr.Zero);
            // 走掉的那格：真的让它失活（同样要摘 WS_CHILD 位，见 Reattach 的注释），
            // 它才会把输入框从输入法那里撤下来；下一格才接得上。
            if (reattach) Activate(s.Hwnd, false);
        }

        // 2. 短暂关联输入队列，只为把焦点交过去
        uint me = Native.GetCurrentThreadId();
        uint pidIgnored;
        uint them = Native.GetWindowThreadProcessId(h, out pidIgnored);
        if (them == 0) return;
        bool attached = them != me && Native.AttachThreadInput(me, them, true);
        try
        {
            Native.SetActiveWindow(h);
            Native.SetFocus(h);
        }
        finally
        {
            if (attached) Native.AttachThreadInput(me, them, false);
        }

        // 3. 把输入法上下文明确交给它
        IntPtr himc = Native.ImmGetContext(h);
        if (himc != IntPtr.Zero)
        {
            Native.ImmAssociateContext(h, himc);
            Native.ImmReleaseContext(h, himc);
        }
        // 5. 补发激活：先 NCACTIVATE 再 ACTIVATE，顺序同系统。
        Native.PostMessage(h, Native.WM_NCACTIVATE, (IntPtr)1, IntPtr.Zero);
        // 让它重新接输入法要「失活→激活」一对，只发激活没用（实测）。
        // 但这一对要是在这次点击之前发，点击会被吃掉（2026-09-20 用户报「always allow 点不动」），
        // 所以推迟到这次点击处理完之后再发。
        if (reattach) ReattachLater(h, full);
        // WM_IME_SETCONTEXT wParam=TRUE, lParam=ISC_SHOWUIALL(0xC000000F)
        Native.PostMessage(h, Native.WM_IME_SETCONTEXT, (IntPtr)1, (IntPtr)unchecked((int)0xC000000F));
    }

    // 让嵌进来的 Chromium 把输入框重新交给输入法（2026-09-20 实测定位，读的是 Chromium 源码）。
    //
    // 症状：回到一格、直接点它本来就有焦点的输入框，拼音出不来（任务栏「英」、字母直接上屏，
    // 或者拼音掉到屏幕左上角）；先点一下空白处再点输入框就好。
    // 原因：Chromium 给文字输入框用的输入法文档只是 SetFocus 上去，没跟窗口绑定
    // （tsf_bridge.cc OnTextInputTypeChanged）；焦点离开这个进程再回来，系统只会恢复成
    // 空文档。它只在「输入框焦点变了」或「窗口激活了」时才重新登记 —— 点空白处
    // 让输入框失焦再得焦，所以好了。
    // 为什么以前补发 WM_ACTIVATE 没用：hwnd_message_handler.cc 只对
    // IsTopLevelWindow() 的窗口处理 WM_ACTIVATE，而这个判断只看有没有 WS_CHILD 样式位。
    // 做法：摘掉 WS_CHILD 位的这一瞬间同步发激活（走掉那格发失活），发完立刻还原。
    // 父子关系不变（只改样式位，不 SetParent、不 SWP_FRAMECHANGED），窗口不会动。
    // 限时 300ms：某一家卡住时不把宿主拖死。
    // 等这次点击走完再补「激活」。150ms：鼠标按下和抬起都已经送到客户端了。
    // 只发激活、不发失活 —— 发失活会让页面失焦，把这次点击刚弹出来的菜单关掉
    // （2026-09-20 Codex 复核提的，实测 Codex 的「文件」菜单确实会被关）。
    // 失活在离开上一格时已经发过，这边的「激活」对它就是一次状态变化，输入法照样能重接。
    Timer _reTimer;
    IntPtr _rePending;
    // 这次补发要不要带上「失活」。点格子时不带（会把刚弹出的菜单关掉），
    // 宿主重新被激活时要带（那一刻客户端没收到过失活，只发激活等于没变化）。
    bool _reFull;

    void ReattachLater(IntPtr h) { ReattachLater(h, false); }

    void ReattachLater(IntPtr h, bool full)
    {
        _rePending = h;
        _reFull = full;
        if (_reTimer == null)
        {
            _reTimer = new Timer();
            _reTimer.Interval = 150;
            _reTimer.Tick += delegate
            {
                _reTimer.Stop();
                IntPtr t = _rePending;
                _rePending = IntPtr.Zero;
                // 到点了再核一遍上下文（Codex 复核建议）：这 150ms 里用户可能已经
                // 切走、把这格移出、或者关了宿主。
                if (t == IntPtr.Zero || t != _lastFocused || !Native.IsWindow(t) || _menuOpen) return;
                bool mine = false;
                for (int i = 0; i < _slots.Count; i++)
                {
                    Slot s = _slots[i];
                    if (s.Hwnd == t && s.Embedded && s.Site != null && SlotShowing(s)
                        && Embed.IsChildOf(t, s.Site.Handle)) mine = true;
                }
                if (!mine) return;
                // 前台已经是别的程序了就别去动它 —— 宿主再被切回来时，WM_ACTIVATE 那条路会补。
                // 注意修好之后前台常常是客户端自己（它是宿主的子窗口），所以不能只认宿主。
                IntPtr fg = Native.GetForegroundWindow();
                if (fg != Handle && !Native.IsChild(Handle, fg)) return;
                Log.W("延后补发 full=" + _reFull + " -> " + t.ToInt64().ToString("X"));
                if (_reFull) Activate(t, false);
                Activate(t, true);
            };
        }
        _reTimer.Stop();
        _reTimer.Start();
    }

    void Activate(IntPtr h, bool active)
    {
        if (h == IntPtr.Zero || !Native.IsWindow(h)) return;
        Log.W("  Activate(" + h.ToInt64().ToString("X") + ", " + (active ? "激活" : "失活") + ")");
        int style = Native.Gwl(h, Native.GWL_STYLE);
        if ((style & Native.WS_CHILD) == 0) return;
        IntPtr ignored;
        // 两次写样式都带上「它该是显示着的」这一位，别照抄刚读到的 WS_VISIBLE（2026-10-04，「Cursor 莫名其妙关了」的源头）：
        // Chromium 处理 WM_NCACTIVATE 时会先摘掉自己的 WS_VISIBLE、处理完再装回（不让系统画旧式标题栏，Cursor 实测一次约 30ms），
        // 而 GiveFocus 刚给它投过一条 WM_NCACTIVATE。我们读样式正好落在这段里，读到的就是「不可见」；
        // 跨线程 SetWindowLong 又要等它的线程空下来才生效 —— 那时它已经把 WS_VISIBLE 装回去了，我们这份旧值再把它抹掉。
        // 探针实录：可见位没了、却没有任何 EVENT_OBJECT_HIDE（不是 ShowWindow 藏的），时刻正对着 Activate(失活)。
        // 只在还原时重读一次不够（试过，反而每次都中：还原那一读正落在它处理 WM_NCACTIVATE 的时候）。
        int vis = ShouldBeVisible(h) ? Native.WS_VISIBLE : 0;
        Native.GwlSet(h, Native.GWL_STYLE, (style & ~Native.WS_CHILD) | vis);
        try
        {
            Native.SendMessageTimeout(h, (uint)Native.WM_ACTIVATE, active ? (IntPtr)1 : IntPtr.Zero, Handle,
                0x0002 /* SMTO_ABORTIFHUNG */, 300, out ignored);
        }
        finally
        {
            Native.GwlSet(h, Native.GWL_STYLE, Native.Gwl(h, Native.GWL_STYLE) | Native.WS_CHILD | vis);
        }
    }

    // 这格的窗口按理是显示着的：嵌着、心跳上次看它没藏、你没刚点 ✕、也不在 Wake 藏一下的那 300ms 里。
    // 拿不准（不是格子里的窗口）就算不是，样式照读到的写。
    bool ShouldBeVisible(IntPtr h)
    {
        if (Waking(h)) return false;
        for (int i = 0; i < _slots.Count; i++)
        {
            Slot s = _slots[i];
            if (s.Hwnd != h) continue;
            return s.Embedded && s.HiddenSince == DateTime.MinValue && (DateTime.Now - s.CloseClickedAt).TotalSeconds >= 30;
        }
        return false;
    }
}
