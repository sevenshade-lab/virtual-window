// 不在前面的格子怎么放：叠在前面那格底下，而不是藏起来（2026-09-26，根治「切换 agent 闪一下 / Codex 卡住不动」）。
//
// 以前队列视图里不在中间的格子、网格「放大」时别的格子，一律 Site.Visible = false。
// 可嵌进来的 Chromium —— 你自己开的 Codex / Claude / Cursor / OpenCode 都没有宿主的启动参数，
// 原生遮挡检测是开着的 —— 一发现自己「不可见」（父窗口藏了）就判定被挡住：停止出帧、丢掉画面，
// 还把监听的事件都注销掉，只在它自己窗口显示 / 隐藏时才重新判定。我们只把父窗口（格子）显示回来，它察觉不到：
//   - 重新露面时一片空白（切换动画等 300ms 超时后揭开的就是空白，4 秒后空白检测把它藏一下再显示才恢复）；
//   - 或者停在旧画面不动 —— 实测 Codex 在中间时点别的对话，窗口样式照变（它收到了），画面 11 秒纹丝不动；
//     过一阵被别的事件叫醒，画面一下子跳过去，就是「切换对话还是会闪」「卡顿」。
// 日志：用户 20:19 连切几次，Codex 两次、Cursor 一次都是等满 300ms 没画出来。
//
// 现在：不在前面的格子保持可见，原位垫在前面那格底下，被它整个挡住（队列视图里所有格子内容区本来就一样大；
// 网格放大时别的格子都在放大那格的范围里）。Chromium 一直觉得自己可见、一直在画；
// 轮到谁只是把它的格子提到最上面，瞬间就是新画面。
// 实测 DWM 按窗口上下顺序合成：Claude 垫在 Codex 底下（两个都是 DirectComposition）、
// 垫在 Antigravity 底下（它带了关掉 DirectComposition 的参数），上面那家都干干净净，一点不透。
//
// 只在前面那格是嵌好的 Chromium 窗口、而且它自己没藏起来时才垫；否则（空格子、命令行终端这类
// 没验证过的窗口、它点了 ✕ 收进托盘）照旧藏 —— 前面那格没有不透明的画面时，垫在下面的会露出来。
// 以前被藏过的 Chromium 已经把自己判成「被挡住」了，第一次垫回来时叫醒一次（它自己的窗口藏一下再显示）。

using System;
using System.Drawing;
using System.Text;

internal sealed partial class HostForm
{
    // 前面那格：队列视图是中间那张，网格视图是放大的那格；它得有一个嵌好、自己可见的 Chromium 窗口
    Slot CoverSlot()
    {
        Slot c = _queueMode ? QueueFront : _solo;
        if (c == null || c.Site == null || !c.Site.IsHandleCreated || !c.Site.Visible) return null;
        if (!LiveEmbedded(c) || !IsChromium(c.Hwnd)) return null;
        // 它自己藏了（点 ✕）就挡不住了；我们叫醒它时那一下藏不算（Wake），不然底下几家会跟着藏、再被全部叫醒一遍
        if ((Native.Gwl(c.Hwnd, Native.GWL_STYLE) & Native.WS_VISIBLE) == 0 && !Waking(c.Hwnd)) return null;
        return c;
    }

    // 刚被 Wake 藏一下再显示的窗口：这 300ms 里的「藏」是我们自己弄的
    readonly System.Collections.Generic.Dictionary<IntPtr, DateTime> _waking = new System.Collections.Generic.Dictionary<IntPtr, DateTime>();

    bool Waking(IntPtr h)
    {
        DateTime until;
        return _waking.TryGetValue(h, out until) && DateTime.Now < until;
    }

    static bool LiveEmbedded(Slot s)
    {
        return s != null && s.Embedded && s.Hwnd != IntPtr.Zero && Native.IsWindow(s.Hwnd);
    }

    static bool IsChromium(IntPtr h)
    {
        var sb = new StringBuilder(64);
        Native.GetClassName(h, sb, sb.Capacity);
        return sb.ToString().StartsWith("Chrome_WidgetWin", StringComparison.Ordinal);
    }

    // 这格能不能垫在 cover 底下：得有嵌好的窗口，而且整个落在 cover 的范围里（不然边上会露出一条）
    bool CanTuck(Slot s, Slot cover)
    {
        if (s == cover || s.Site == null || !s.Site.IsHandleCreated || !LiveEmbedded(s)) return false;
        Rectangle target = _queueMode ? s.Content : s.Site.Bounds;
        return target.Width > 0 && cover.Site.Bounds.Contains(target);
    }

    // 不在前面的格子：能垫就垫（可见、紧贴在 cover 下面），不能就藏。只在排版结束时统一调（SyncStack），
    // 这样前面那格已经在最上面了，垫的这格变可见的那一刻不会先露出来。
    void Tuck(Slot s, Slot cover)
    {
        if (cover != null && CanTuck(s, cover))
        {
            bool was = s.Site.Visible;
            if (_queueMode && s.Site.Bounds != s.Content) s.Site.Bounds = s.Content;
            // 先定上下顺序再显示：插在 cover 下面（藏着的窗口也能先排好位置）
            Native.SetWindowPos(s.Site.Handle, cover.Site.Handle, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            if (!was)
            {
                s.Site.Visible = true;
                Wake(s, "垫到底下");
            }
            return;
        }
        if (s.Site != null && s.Site.Visible) s.Site.Visible = false;
    }

    // 排版之后统一整理：前面那格提到最上面，其余不显示的格子垫在它底下（或藏起来）
    void SyncStack()
    {
        if (_closed) return;
        Slot cover = CoverSlot();
        _lastCover = cover;
        if (cover != null)
            Native.SetWindowPos(cover.Site.Handle, Native.HWND_TOP, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        foreach (Slot s in _slots)
        {
            if (s == cover || s.Site == null) continue;
            if (SlotShowing(s)) continue;   // 网格里正常显示的格子不动
            Tuck(s, cover);
        }
    }

    // 叫醒一个自以为被挡住的 Chromium：它自己的窗口藏一下再显示（不抢激活）。它的遮挡检测只认自己窗口的显隐，
    // 这样它会重新判成可见、整窗重画。用 Async：对方忙的时候别把宿主界面线程卡住。
    void Wake(Slot s, string why)
    {
        if (!LiveEmbedded(s) || !IsChromium(s.Hwnd)) return;
        if ((Native.Gwl(s.Hwnd, Native.GWL_STYLE) & Native.WS_VISIBLE) == 0) return;   // 它自己藏着的别动
        Log.W("wake " + s.Kind.Id + "（" + why + "）");
        // 嵌着的时候又被它自己加上了分层样式：去掉并让边框重算，不然怎么叫都是空白（见 Embed）
        int ex = Native.Gwl(s.Hwnd, Native.GWL_EXSTYLE);
        if ((ex & Native.WS_EX_LAYERED) != 0)
        {
            Native.GwlSet(s.Hwnd, Native.GWL_EXSTYLE, ex & ~Native.WS_EX_LAYERED);
            Native.SetWindowPos(s.Hwnd, IntPtr.Zero, 0, 0, 0, 0, 0x37);   // NOSIZE|NOMOVE|NOZORDER|NOACTIVATE|FRAMECHANGED
            Log.W("  去掉 " + s.Kind.Id + " 的 WS_EX_LAYERED");
        }
        _waking[s.Hwnd] = DateTime.Now.AddMilliseconds(300);
        Native.ShowWindowAsync(s.Hwnd, 0);   // SW_HIDE
        Native.ShowWindowAsync(s.Hwnd, 8);   // SW_SHOWNA
        // 它要是拿着键盘焦点，藏一下焦点就丢了（藏起来的窗口不能有焦点），显示回来也不会自己拿回去：稍后还给它。
        // 到点再判该不该给它 —— 换人时宿主紧接着就把焦点交给新的中间那格，那一刻可能还没轮到它藏
        IntPtr h = s.Hwnd;
        var t = new System.Windows.Forms.Timer { Interval = 150 };
        t.Tick += delegate
        {
            t.Stop();
            t.Dispose();
            if (_closed || h != _lastFocused || !Native.IsWindow(h) || _menuOpen) return;
            IntPtr f = Native.GetFocus();
            if (f != h && !Native.IsChild(h, f)) GiveFocus(h, true);
        };
        t.Start();
    }

    // 心跳里核对：前面那格变了（它自己点 ✕ 藏了、窗口没了、又回来了）就重新整理，
    // 别让垫在底下的从空掉的前面那格里露出来
    Slot _lastCover;

    void SyncStackIfCoverChanged()
    {
        Slot c = CoverSlot();
        if (c == _lastCover) return;
        _lastCover = c;
        SyncStack();
    }
}
