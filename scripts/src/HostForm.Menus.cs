// 添加其他窗口、窗口系统菜单。「设置」是一个面板，见 HostForm.Settings.cs。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;

internal sealed partial class HostForm
{
    // 「把别的窗口放进格子」。嵌入那一套（Embed / HostWins）本来就跟是谁无关，
    // 只有 Catalog 和 Launch 写死了四家 —— 挑一个正在跑的窗口进来，浏览器、
    // 终端、日志窗口都能排进同一套格子。Layouts 里 5 / 6 格那几套排版
    // 以前够不到（最多只有四家），现在也用得上了。
    ToolStripMenuItem BuildAdoptMenu()
    {
        var root = new ToolStripMenuItem(L.T("添加其他窗口"));
        if (_slots.Count >= MaxSlots)
        {
            root.Text = L.F("把别的窗口放进格子（已经 {0} 格，满了）", MaxSlots);
            root.Enabled = false;
            return root;
        }
        List<WinInfo> all = HostWins.Visible();
        all.Sort(delegate(WinInfo a, WinInfo b) { return b.Area.CompareTo(a.Area); });
        int me = Process.GetCurrentProcess().Id;
        int shown = 0;
        for (int i = 0; i < all.Count && shown < 20; i++)
        {
            WinInfo w = all[i];
            if (w.Pid == me) continue;
            if (w.Area < 200 * 150) continue;              // 小挂件、托盘助手
            if (SlotWithHwnd(w.Handle) != null) continue;
            if (IsShellWindow(w.Handle)) continue;
            string label = w.Title ?? "";
            if (label.Length > 46) label = label.Substring(0, 45) + "…";
            var item = new ToolStripMenuItem(label + "   — " + w.ProcessName);
            WinInfo captured = w;
            item.Click += delegate { AdoptWindow(captured); };
            root.DropDownItems.Add(item);
            shown++;
        }
        if (shown == 0)
        {
            root.Text = L.T("把别的窗口放进格子（没找到别的窗口）");
            root.Enabled = false;
        }
        return root;
    }

    // 桌面、任务栏、开始菜单这些壳层窗口别列出来 —— 嵌进去只会把桌面弄坏。
    static bool IsShellWindow(IntPtr h)
    {
        var sb = new StringBuilder(64);
        Native.GetClassName(h, sb, sb.Capacity);
        string c = sb.ToString();
        return c == "Progman" || c == "WorkerW" || c == "Shell_TrayWnd"
            || c == "Shell_SecondaryTrayWnd" || c == "Windows.UI.Core.CoreWindow";
    }

    void ShowWindowMenu(Point screen)
    {
        if (_closed || _menuOpen) return;
        IntPtr menu = WindowMenuCommands.Create(WindowState);
        if (menu == IntPtr.Zero) return;
        _menuOpen = true;
        IntPtr previousFocus = Native.GetFocus();
        int command = 0;
        try
        {
            // The menu loop runs on the host thread; an embedded client's focus
            // otherwise leaves Escape/arrows on that client's message queue.
            Native.SetFocus(Handle);
            command = Native.TrackPopupMenuEx(menu, 0x100 | 0x80 | 0x2, screen.X, screen.Y, Handle, IntPtr.Zero);
        }
        finally
        {
            Native.DestroyMenu(menu);
            IntPtr fg = Native.GetForegroundWindow();
            if (!_closed && Native.GetFocus() == Handle && Native.IsWindow(previousFocus)
                && Native.IsChild(Handle, previousFocus) && (fg == Handle || Native.IsChild(Handle, fg)))
                Native.SetFocus(previousFocus);
            _menuOpen = false;
            _hoverAt = null;
        }
        if (command != 0) Native.PostMessage(Handle, Native.WM_SYSCOMMAND, (IntPtr)command, IntPtr.Zero);
    }
}
