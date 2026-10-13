// 【拆分】窗口嵌入：SetParent 进格子、还回桌面；Watch 记着所有嵌过的 HWND。（从原 AgentHost.cs 原样搬出，逻辑未改）

using System;
using System.Collections.Generic;
using System.Drawing;

internal static class Embed
{
    public static bool IsChildOf(IntPtr child, IntPtr parent)
    {
        if (child == IntPtr.Zero || parent == IntPtr.Zero) return false;
        if (!Native.IsWindow(child) || !Native.IsWindow(parent)) return false;
        return Native.IsChild(parent, child);
    }

    // 真正的顶层窗：根就是自己。不要用 GetParent —— 顶层窗它返回的是
    // owner，已经还回去的窗口会被误判成「还挂在别人下面」。
    public static bool IsTopLevel(IntPtr h)
    {
        if (h == IntPtr.Zero || !Native.IsWindow(h)) return false;
        return Native.GetAncestor(h, Native.GA_ROOT) == h;
    }

    // 直接嵌主窗口，不做「试探 SetParent 再挂回」：Electron 窗口经不起
    // 来回折腾，GetParent 对顶层窗口返回的还是 owner，挂回时会挂错。
    public static bool Attach(Slot s, IntPtr parent)
    {
        if (s == null || s.Hwnd == IntPtr.Zero || parent == IntPtr.Zero) return false;
        if (!Native.IsWindow(s.Hwnd)) return false;
        HostWins.Restore(s.Hwnd);
        if (!s.Embedded)
        {
            s.SavedStyle = Native.Gwl(s.Hwnd, Native.GWL_STYLE);
            s.SavedEx = Native.Gwl(s.Hwnd, Native.GWL_EXSTYLE);
        }
        // 先换样式再 SetParent，这是文档要求的顺序。
        // WS_THICKFRAME 必须去掉：Windows 只对带 sizing border 的窗口强制
        // WM_GETMINMAXINFO 的 ptMinTrackSize。Electron 会在那里报 minWidth/minHeight，
        // 留着 THICKFRAME 的话，格子比它的最小尺寸小时 MoveWindow 会被悄悄抬回
        // 最小尺寸，内容就会溢出格子被裁掉（Codex/ChatGPT 的最小尺寸比 Claude 大）。
        int style = Native.Gwl(s.Hwnd, Native.GWL_STYLE);
        style &= ~Native.WS_POPUP;
        style &= ~Native.WS_CAPTION;
        style &= ~Native.WS_THICKFRAME;
        style &= ~Native.WS_SYSMENU;
        style &= ~Native.WS_MINIMIZEBOX;
        style &= ~Native.WS_MAXIMIZEBOX;
        style &= ~Native.WS_MAXIMIZE;
        style &= ~Native.WS_MINIMIZE;
        style |= Native.WS_CHILD | Native.WS_VISIBLE;
        Native.GwlSet(s.Hwnd, Native.GWL_STYLE, style);
        int ex = Native.Gwl(s.Hwnd, Native.GWL_EXSTYLE);
        ex &= ~Native.WS_EX_APPWINDOW;
        // Claude 桌面端被收起、最小化再还原时会自己带上 WS_EX_LAYERED；分层窗口当子窗口嵌进来就一直空白，
        // 叫醒也没用（2026-09-28 实测：换版重开后 Claude 格白屏，去掉这一位当场画出来）
        ex &= ~Native.WS_EX_LAYERED;
        Native.GwlSet(s.Hwnd, Native.GWL_EXSTYLE, ex);
        Native.SetParent(s.Hwnd, parent);
        s.Embedded = IsChildOf(s.Hwnd, parent);
        if (!s.Embedded)
        {
            // 失败就强制还成正常顶层，不要留一个既不是子窗口又没标题栏的残废。
            RestoreTopLevel(s.Hwnd);
            return false;
        }
        Watch.Note(s.Hwnd);
        Fit(s);
        return true;
    }

    // 嵌进来的 Electron 自己把 WS_CHILD 弄丢了（Codex 切对话时会重设窗口样式，变回带标题栏的顶层样式 10C70000）：
    // 父窗口还是我们的格子，就当场把 WS_CHILD 装回、去掉它加的框，只刷新边框，不重排。
    // 以前要等心跳（0.7 秒一拍）发现「不是子窗口了」再整页重排 —— 这段时间它按顶层窗口画、带着标题栏闪一下，
    // 整页重排又闪一下；它正忙时来不及改回就停在外面（用户：Codex 莫名其妙弹出来）。返回 true = 修了。
    public static bool RepairChildStyle(Slot s)
    {
        if (s == null || s.Hwnd == IntPtr.Zero || s.Site == null || !s.Embedded) return false;
        int style = Native.Gwl(s.Hwnd, Native.GWL_STYLE);
        if ((style & Native.WS_CHILD) != 0) return false;
        if (!s.Site.IsHandleCreated || Native.GetAncestor(s.Hwnd, Native.GA_PARENT) != s.Site.Handle) return false;
        int want = (style | Native.WS_CHILD) & ~Native.WS_POPUP & ~Native.WS_CAPTION & ~Native.WS_THICKFRAME
                   & ~Native.WS_MAXIMIZE & ~Native.WS_MINIMIZE;
        Native.GwlSet(s.Hwnd, Native.GWL_STYLE, want);
        Native.SetWindowPos(s.Hwnd, IntPtr.Zero, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_FRAMECHANGED);
        return true;
    }

    public static void Fit(Slot s)
    {
        if (s == null || s.Hwnd == IntPtr.Zero || s.Site == null) return;
        if (!Native.IsWindow(s.Hwnd)) return;
        Size sz = s.Site.ClientSize;
        if (sz.Width < 40 || sz.Height < 40) return;
        // 嵌入后如果窗口上还残留 WS_MAXIMIZE/WS_MINIMIZE（Electron 自己 maximize
        // 过），Chromium 会按「最大化」算 non-client 内边距，内容四边缩一圈。
        int style = Native.Gwl(s.Hwnd, Native.GWL_STYLE);
        int want = style & ~Native.WS_MAXIMIZE & ~Native.WS_MINIMIZE & ~Native.WS_THICKFRAME & ~Native.WS_CAPTION;
        if (want != style) Native.GwlSet(s.Hwnd, Native.GWL_STYLE, want);
        // 它自己藏起来了（点了 ✕ 收进托盘）就别顺手显示回来，交给心跳的「待关」流程
        uint show = s.HiddenSince == DateTime.MinValue ? Native.SWP_SHOWWINDOW : 0;
        // 去掉 THICKFRAME 以后，Claude 桌面端仍按「有缩放边框」算非客户区：左右下各留 7px，
        // 独立窗口时那圈是 DWM 画的透明阴影区，当子窗口就成了黑边（用户 2026-09-27：「Claude 为什么有黑边」）。
        // 先量客户区四边内缩多少，把窗口往外扩同样多，让客户区正好铺满格子；外扩的那圈被格子裁掉。
        // 平常一次摆到位；摆完边框变了（刚嵌进来、样式刚改）才按新量的再摆一次。
        int l, t, r, b;
        ClientInsets(s.Hwnd, out l, out t, out r, out b);
        Place(s.Hwnd, sz, l, t, r, b, Native.SWP_FRAMECHANGED | show);
        int l2, t2, r2, b2;
        ClientInsets(s.Hwnd, out l2, out t2, out r2, out b2);
        // Electron 收到 WM_WINDOWPOSCHANGED 后可能又按自己记的 bounds 改回去
        // （ready-to-show / 恢复上次尺寸），这里立刻核对一次，不对就再压一次。
        if (l2 != l || t2 != t || r2 != r || b2 != b || Misfit(s))
            Place(s.Hwnd, sz, l2, t2, r2, b2, show);
    }

    static void Place(IntPtr h, Size sz, int l, int t, int r, int b, uint flags)
    {
        int w = sz.Width + l + r, ht = sz.Height + t + b;
        Native.MoveWindow(h, -l, -t, w, ht, true);
        Native.SetWindowPos(h, Native.HWND_TOP, -l, -t, w, ht, Native.SWP_NOACTIVATE | flags);
    }

    // 客户区相对整个窗口四边各缩进多少（非客户区）。量不出或不像边框（负数、超过 32）就当没有。
    public static bool ClientInsets(IntPtr h, out int l, out int t, out int r, out int b)
    {
        l = t = r = b = 0;
        Native.RECT win, cli;
        if (!Native.GetWindowRect(h, out win) || !Native.GetClientRect(h, out cli)) return false;
        if (cli.Width <= 0 || cli.Height <= 0) return false;
        var p = new Native.POINT();
        if (!Native.ClientToScreen(h, ref p)) return false;
        l = p.X - win.Left; t = p.Y - win.Top;
        r = win.Width - cli.Width - l; b = win.Height - cli.Height - t;
        if (l < 0 || t < 0 || r < 0 || b < 0 || l > 32 || t > 32 || r > 32 || b > 32) { l = t = r = b = 0; return false; }
        return true;
    }

    // 客户区在屏幕上的位置和大小（嵌入的窗口可能被往外扩过，比的是客户区，不是整个窗口）。
    public static bool ClientScreenRect(IntPtr h, out Rectangle rc)
    {
        rc = Rectangle.Empty;
        Native.RECT cli;
        if (!Native.GetClientRect(h, out cli)) return false;
        var p = new Native.POINT();
        if (!Native.ClientToScreen(h, ref p)) return false;
        rc = new Rectangle(p.X, p.Y, cli.Width, cli.Height);
        return true;
    }

    // 已嵌入的窗口是否没铺满格子：客户区跟 Site 客户区对不上。
    public static bool Misfit(Slot s)
    {
        if (s == null || s.Hwnd == IntPtr.Zero || s.Site == null) return false;
        if (!Native.IsWindow(s.Hwnd) || !s.Site.IsHandleCreated) return false;
        Size sz = s.Site.ClientSize;
        if (sz.Width < 40 || sz.Height < 40) return false;
        Rectangle c;
        if (!ClientScreenRect(s.Hwnd, out c)) return false;
        Point origin = s.Site.PointToScreen(Point.Empty);
        const int slack = 2;
        return Math.Abs(c.Left - origin.X) > slack
            || Math.Abs(c.Top - origin.Y) > slack
            || Math.Abs(c.Width - sz.Width) > slack
            || Math.Abs(c.Height - sz.Height) > slack;
    }

    // 把一个嵌过的窗口还成正常顶层窗口。
    //
    // 不信 SavedStyle：它是嵌入前存的，如果那时窗口已经是崩后只剩标题栏的残缺
    // 样式，按它还原出来的还是残缺的 —— 2026-09-19 关宿主后留下一整块无边框
    // 白板就是这么来的。这里一律强制补齐顶层窗口该有的东西：
    // WS_OVERLAPPEDWINDOW（标题栏/边框/系统菜单/最大最小化）+ WS_EX_APPWINDOW（任务栏图标）。
    // 顺序按文档：先改样式，再 SetParent(0)，最后 SWP_FRAMECHANGED 让它重算框架。
    public static void RestoreTopLevel(IntPtr h) { RestoreTopLevel(h, false); }

    // keepHidden：它自己收进托盘了，还回桌面但保持隐藏。以前一律显示出来，
    // 点 ✕ 之后 Codex 反倒变成一个独立浮窗；「待关」再一看桌面上有它的窗口，就不收了 ——
    // 用户看到的就是「点 ✕ 关不掉」（2026-09-26）。
    public static void RestoreTopLevel(IntPtr h, bool keepHidden)
    {
        if (h == IntPtr.Zero || !Native.IsWindow(h)) return;
        bool stillChild = !IsTopLevel(h);
        int exNow = Native.Gwl(h, Native.GWL_EXSTYLE);
        if ((exNow & (Native.WS_EX_TRANSPARENT | Native.WS_EX_NOACTIVATE)) != 0)
        {
            RestoreOverlay(h, stillChild);
            return;
        }
        int style = Native.Gwl(h, Native.GWL_STYLE);
        stillChild = stillChild || (style & Native.WS_CHILD) != 0;
        style &= ~Native.WS_CHILD;
        style &= ~Native.WS_POPUP;
        style |= Native.WS_OVERLAPPEDWINDOW;
        if (!keepHidden) style |= Native.WS_VISIBLE;
        Native.GwlSet(h, Native.GWL_STYLE, style);
        int ex = Native.Gwl(h, Native.GWL_EXSTYLE);
        ex |= Native.WS_EX_APPWINDOW;
        Native.GwlSet(h, Native.GWL_EXSTYLE, ex);
        // 已经是顶层就不要再 SetParent(0)：那会把 Electron 的 owner 清掉。
        // 还挂在格子里才摘下来。
        if (stillChild) Native.SetParent(h, IntPtr.Zero);
        Native.SetWindowPos(h, Native.HWND_TOP, 120, 80, 1280, 800,
            Native.SWP_FRAMECHANGED | Native.SWP_NOZORDER | (keepHidden ? Native.SWP_NOACTIVATE : Native.SWP_SHOWWINDOW));
        if (!keepHidden) Native.ShowWindow(h, Native.SW_RESTORE);
        if (IsTopLevel(h)) Watch.Forget(h);
    }

    // 鼠标穿透 / 不可激活的悬浮层（现在 HostWins 已不会收它，这里是给以前误收进来的收尾）。
    // 按普通窗口还原会给它加上边框、摆成 1280x800 —— 一个点不到也关不掉的透明大窗口。
    // 按它本来的样子还：无边框、置顶、铺满所在屏幕，不抢焦点。
    static void RestoreOverlay(IntPtr h, bool stillChild)
    {
        int style = Native.Gwl(h, Native.GWL_STYLE);
        style &= ~Native.WS_CHILD;
        style &= ~Native.WS_OVERLAPPEDWINDOW;
        style |= Native.WS_POPUP | Native.WS_VISIBLE;
        Native.GwlSet(h, Native.GWL_STYLE, style);
        if (stillChild) Native.SetParent(h, IntPtr.Zero);
        System.Drawing.Rectangle b = System.Windows.Forms.Screen.FromHandle(h).Bounds;
        Native.SetWindowPos(h, Native.HWND_TOPMOST, b.X, b.Y, b.Width, b.Height,
            Native.SWP_FRAMECHANGED | Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
        if (IsTopLevel(h)) Watch.Forget(h);
    }

    public static void Detach(Slot s)
    {
        if (s == null || s.Hwnd == IntPtr.Zero) return;
        ReleaseToDesktop(s, false);
    }

    // 关宿主：把四家还成正常顶层窗口后**最小化**，不关它们的进程。
    // （2026-09-19 用户定：关宿主等于收起来，不等于退出。）
    // 顺序仍是先还原、确认真的变回顶层了，再 SW_MINIMIZE ——
    // 对一个还挂在我们面板下的子窗口做任何事，Electron 都可能崩。
    public static void Close(Slot s)
    {
        ReleaseToDesktop(s, true);
    }

    // 成功才清 Slot.Hwnd。没还成顶层就留着，让关宿主那边取消关闭。
    public static bool ReleaseToDesktop(Slot s, bool minimize)
    {
        if (s == null || s.Hwnd == IntPtr.Zero) return true;
        IntPtr h = s.Hwnd;
        if (!Native.IsWindow(h))
        {
            Watch.Forget(h);
            s.Embedded = false;
            s.Hwnd = IntPtr.Zero;
            return true;
        }
        RestoreTopLevel(h);
        if (!IsTopLevel(h)) return false;
        // 悬浮层不最小化：它本来就不是给人摆弄的窗口，最小化会让它主人的提示显示不出来。
        if (minimize && (Native.Gwl(h, Native.GWL_EXSTYLE) & (Native.WS_EX_TRANSPARENT | Native.WS_EX_NOACTIVATE)) == 0)
            Native.ShowWindow(h, Native.SW_MINIMIZE);
        Watch.Forget(h);
        s.Embedded = false;
        s.Hwnd = IntPtr.Zero;
        return true;
    }
}
// 记下当前嵌着的 HWND。关宿主 / 面板销毁 / 进程退出都走这里，
// 避免漏还一个导致 Windows 随宿主一起毁掉别人的窗口。
internal static class Watch
{
    static readonly List<IntPtr> _live = new List<IntPtr>();

    public static void Note(IntPtr h)
    {
        if (h == IntPtr.Zero) return;
        for (int i = 0; i < _live.Count; i++)
            if (_live[i] == h) return;
        _live.Add(h);
    }

    public static void Forget(IntPtr h)
    {
        for (int i = _live.Count - 1; i >= 0; i--)
            if (_live[i] == h) _live.RemoveAt(i);
    }

    public static void ReleaseAll(bool minimize)
    {
        for (int i = _live.Count - 1; i >= 0; i--)
        {
            IntPtr h = _live[i];
            _live.RemoveAt(i);
            if (!Native.IsWindow(h)) continue;
            Embed.RestoreTopLevel(h);
            if (minimize && Embed.IsTopLevel(h))
                Native.ShowWindow(h, Native.SW_MINIMIZE);
        }
    }
}
