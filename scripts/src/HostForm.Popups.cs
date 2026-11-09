// 顶栏弹出来的东西（设置 / 名单 / 额度 / 新会话 / 委托这几张面板，设置底下和主题的小菜单）
// 在你点进某一格时要自己关掉 —— 2026-10-29 用户：「点开上面的菜单栏再点空白的地方，有时不会自己关」。
//
// 关不掉的原因（10-29 用两进程的探针复现）：格子是别家进程的子窗口，点它既不经过宿主线程的消息过滤器，
// 也不换激活窗口（宿主弹小菜单前先把自己激活了），而 WinForms 的菜单只认这两条路，于是一直开着；
// 面板是靠失活关的，可「⋯」菜单开着（_busy）、点过启动钉住（_pinned）的时候失活不关，
// 「⋯」菜单关掉后还会把面板重新激活，焦点从你刚点的格子里抢回来。
// 统一改成：WM_PARENTNOTIFY 说鼠标按在某一格里 → 开着的菜单、面板全关掉，焦点归点的那格，不还给弹之前那格。

using System;
using System.Windows.Forms;

// 顶栏的弹出面板都实现这个：点进格子时宿主叫它关
internal interface IHostPopup { void DismissForSlotClick(); }

internal sealed partial class HostForm
{
    ToolStripDropDown _openMenu;     // 宿主自己弹着的小菜单（设置底下那份 / 主题）
    bool _dismissingForSlotClick;    // 正在因为点进格子而关弹层：这期间关掉的不把焦点还回去

    // 记下宿主自己弹的小菜单，关了就忘
    void TrackMenu(ToolStripDropDown menu)
    {
        _openMenu = menu;
        menu.Closed += delegate { if (_openMenu == menu) _openMenu = null; };
    }

    // 弹层（菜单 / 面板）关掉后把焦点还给弹之前那一格。
    // 是点进格子关掉的就不还：这次点击本身已经把焦点交给点的那格，再还等于把它抢走。
    void GiveBackAfterPopup(IntPtr giveBack)
    {
        if (_dismissingForSlotClick) return;
        if (giveBack == IntPtr.Zero || !Native.IsWindow(giveBack) || SlotWithHwnd(giveBack) == null) return;
        BeginInvoke((MethodInvoker)delegate { if (!_menuOpen) GiveFocus(giveBack, true); });
    }

    // 鼠标现在压在哪一格的窗口上（嵌进来的才算）
    Slot SlotUnderCursor()
    {
        Native.POINT p;
        if (!Native.GetCursorPos(out p)) return null;
        IntPtr under = Native.WindowFromPoint(p);
        if (under == IntPtr.Zero) return null;
        for (int i = 0; i < _slots.Count; i++)
        {
            Slot s = _slots[i];
            if (s.Hwnd == IntPtr.Zero || !s.Embedded) continue;
            if (under == s.Hwnd || IsDescendant(under, s.Hwnd)) return s;
        }
        return null;
    }

    // 鼠标按进了某一格：把顶栏弹着的都关掉。返回有没有关掉什么
    bool DismissPopupsForSlotClick(Slot s)
    {
        bool any = false;
        _dismissingForSlotClick = true;
        try
        {
            ToolStripDropDown menu = _openMenu;
            if (menu != null && !menu.IsDisposed && menu.Visible)
            {
                menu.Close(ToolStripDropDownCloseReason.AppFocusChange);
                any = true;
            }
            Form[] panels = { _settingsPanel, _rosterPanel, _quotaPanel, _newSession, _tasksPanel, _todoPanel };
            foreach (Form f in panels)
            {
                var p = f as IHostPopup;
                if (p == null || f.IsDisposed) continue;
                p.DismissForSlotClick();
                any = true;
            }
        }
        catch (Exception ex) { Log.W("popup: 关弹层出错 " + ex.Message); }
        finally { _dismissingForSlotClick = false; }
        if (any) Log.W("popup: 点进 " + s.Kind.Id + "，关掉顶栏弹着的");
        return any;
    }
}
