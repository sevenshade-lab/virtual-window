// 【拆分】枚举桌面顶层窗口、认出是哪一家。（从原 AgentHost.cs 原样搬出，逻辑未改）

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Text;

internal static class HostWins
{
    public static List<WinInfo> Visible()
    {
        var list = new List<WinInfo>();
        Native.EnumWindows(delegate(IntPtr h, IntPtr lp)
        {
            if (!Native.IsWindowVisible(h)) return true;
            int cloaked = 0;
            Native.DwmGetInt(h, Native.DWMWA_CLOAKED, out cloaked, 4);
            if (cloaked != 0) return true;
            int ex = Native.Gwl(h, Native.GWL_EXSTYLE);
            if ((ex & Native.WS_EX_TOOLWINDOW) != 0) return true;
            // 鼠标穿透 / 不可激活的是悬浮提示层，不是给人用的主窗口。2026-09-25 踩到：
            // Claude 桌面端 computer-use 时那层「正在操作电脑」的提示，标题也叫 Claude，
            // 被当成 Claude 主窗口嵌进格子 —— 点不到、关不掉、移出也没用，真的主窗口反倒留在外面。
            if ((ex & (Native.WS_EX_TRANSPARENT | Native.WS_EX_NOACTIVATE)) != 0) return true;
            var sb = new StringBuilder(512);
            Native.GetWindowText(h, sb, sb.Capacity);
            string title = sb.ToString();
            if (string.IsNullOrEmpty(title)) return true;
            if (title == HostForm.Title) return true;
            uint pid = 0;
            Native.GetWindowThreadProcessId(h, out pid);
            string proc = "", exe = "";
            try
            {
                Process p = Process.GetProcessById((int)pid);
                proc = p.ProcessName;
                try { exe = p.MainModule.FileName; } catch { }
            }
            catch { return true; }
            Native.RECT fr;
            int hr = Native.DwmGetWindowAttribute(h, Native.DWMWA_EXTENDED_FRAME_BOUNDS, out fr, 16);
            if (hr != 0) Native.GetWindowRect(h, out fr);
            int area = Math.Max(0, fr.Width) * Math.Max(0, fr.Height);
            // 不按面积过滤主窗口：客户端崩过一次重开后，主窗口常常只剩
            // 一条 237×39 的标题栏，按面积筛会把它们全漏掉。嵌入时会重新 Fit。
            // 只把明显不是主窗口的（尺寸为 0 的消息窗口）跳掉。
            if (fr.Width <= 0 || fr.Height <= 0) return true;
            list.Add(new WinInfo
            {
                Handle = h,
                Title = title,
                ProcessName = proc,
                ExePath = exe ?? "",
                Pid = (int)pid,
                Area = area
            });
            return true;
        }, IntPtr.Zero);
        return list;
    }

    public static bool Match(WinInfo w, AgentKind k)
    {
        if (w == null || k == null) return false;
        if (LooksLikeUtility(w.Title)) return false;
        bool marked = w.Title.IndexOf(k.TitleMark, StringComparison.Ordinal) >= 0;
        // A CLI session owns one explicitly named terminal window. Never match
        // by family/process alone: several sessions may be running concurrently.
        if (k.Cli) return IsTerm(w.ProcessName) && marked;
        if (IsTerm(w.ProcessName)) return false;
        // 客户端模式下标题带我们的标记也照认：上次嵌过、这次宿主重开，
        // 标题还挂着「虚拟窗口 · Claude」，不能因此把它漏掉。
        if (marked && NameIn(w.ProcessName, k.Processes)) return true;
        if (NameIn(w.ProcessName, k.Processes))
        {
            if (k.Id == "opencode" && w.ExePath.IndexOf("node_modules", StringComparison.OrdinalIgnoreCase) >= 0)
                return false;
            // 同一 Cursor.exe 会同时开「Cursor Agents」桌面窗和 VS Code IDE 窗。
            // 点启动若认所有 Cursor 窗，会按面积嵌到 IDE。桌面端只要 Agents。
            // 以前只排除「… - Cursor」，可 IDE 刚开、还没打开文件夹时标题就叫「Cursor」，照样被嵌进来。
            if (k.Id == "cursor" && !IsCursorAgentsTitle(w.Title)) return false;
            return true;
        }
        if (IsFrameHost(w.ProcessName) && TitleHints(k, w.Title)) return true;
        if (k.Id == "opencode" && w.Title.IndexOf("OpenCode", StringComparison.OrdinalIgnoreCase) >= 0)
            return !TitleHints(Catalog.ById("cursor"), w.Title);
        return false;
    }

    static bool IsFrameHost(string proc)
    {
        return proc != null && proc.Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase);
    }

    static bool TitleHints(AgentKind k, string title)
    {
        if (k == null || string.IsNullOrEmpty(title)) return false;
        if (k.Id == "claude")
            return title.IndexOf("Claude", StringComparison.OrdinalIgnoreCase) >= 0
                && title.IndexOf("Cursor", StringComparison.OrdinalIgnoreCase) < 0;
        if (k.Id == "codex")
            return title.IndexOf("Codex", StringComparison.OrdinalIgnoreCase) >= 0
                || title.IndexOf("ChatGPT", StringComparison.OrdinalIgnoreCase) >= 0;
        if (k.Id == "cursor")
            return title.IndexOf("Cursor", StringComparison.OrdinalIgnoreCase) >= 0;
        if (k.Id == "opencode")
            return title.IndexOf("OpenCode", StringComparison.OrdinalIgnoreCase) >= 0;
        // 名单扩到十几家以后，「标题里含名字」对 Pi、Devin 这种短名字太宽：
        // 只给商店版（走 ApplicationFrameHost 的）用，别的一律按进程名认。
        if (string.IsNullOrEmpty(k.AppsFolder)) return false;
        return title.IndexOf(k.Name, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    static bool NameIn(string proc, string[] names)
    {
        if (string.IsNullOrEmpty(proc) || names == null) return false;
        for (int i = 0; i < names.Length; i++)
            if (string.Equals(proc, names[i], StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    static bool IsTerm(string proc)
    {
        return proc != null && (
            proc.Equals("WindowsTerminal", StringComparison.OrdinalIgnoreCase)
            || proc.Equals("conhost", StringComparison.OrdinalIgnoreCase)
            || proc.Equals("OpenConsole", StringComparison.OrdinalIgnoreCase)
            || proc.Equals("powershell", StringComparison.OrdinalIgnoreCase)
            || proc.Equals("pwsh", StringComparison.OrdinalIgnoreCase)
            || proc.Equals("cmd", StringComparison.OrdinalIgnoreCase));
    }

    static bool LooksLikeUtility(string title)
    {
        return title.IndexOf("settings", StringComparison.OrdinalIgnoreCase) >= 0
            || title.IndexOf("extensions", StringComparison.OrdinalIgnoreCase) >= 0
            || title.IndexOf("devtools", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    // 桌面端窗口标题是「Cursor Agents」。IDE 是 VS Code 那套：
    // 「文件 - 工作区 - Cursor」。CLI 是另一档，跟这两个都无关。
    static bool IsCursorAgentsTitle(string title)
    {
        return !string.IsNullOrEmpty(title)
            && title.IndexOf("Cursor Agents", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    public static WinInfo Find(AgentKind k, List<WinInfo> all)
    {
        WinInfo best = null;
        for (int i = 0; i < all.Count; i++)
        {
            if (!Match(all[i], k)) continue;
            if (best == null || Score(all[i], k) > Score(best, k)) best = all[i];
        }
        return best;
    }

    static int Score(WinInfo w, AgentKind k)
    {
        int s = w.Area;
        if (IsFrameHost(w.ProcessName) && (k.Id == "claude" || k.Id == "codex")) s += 1000000;
        if (NameIn(w.ProcessName, k.Processes)) s += 500000;
        // Codex 桌面版是 ChatGPT.exe，一个进程里可能同时有「ChatGPT」聊天窗和
        // 「Codex」窗两个可见顶层窗口。要嵌的是 Codex 那个，别按面积选到聊天窗。
        if (k.Id == "codex" && w.Title.IndexOf("Codex", StringComparison.OrdinalIgnoreCase) >= 0) s += 300000;
        if (k.Id == "cursor" && IsCursorAgentsTitle(w.Title)) s += 300000;
        // 崩后重开只剩 237×39 一条标题栏的窗口，面积几乎为 0；如果同进程还有
        // 一个正常大小的窗口，优先要正常的那个（Area 本身已体现，这里不再加权）。
        return s;
    }

    public static void Restore(IntPtr h)
    {
        if (h == IntPtr.Zero) return;
        Native.AllowSetForegroundWindow(-1);
        if (Native.IsIconic(h)) Native.ShowWindow(h, Native.SW_RESTORE);
        int style = Native.Gwl(h, Native.GWL_STYLE);
        style = style & ~Native.WS_MAXIMIZE & ~Native.WS_MINIMIZE;
        Native.GwlSet(h, Native.GWL_STYLE, style);
        Native.SendMessage(h, Native.WM_SYSCOMMAND, (IntPtr)Native.SC_RESTORE, IntPtr.Zero);
        Native.ShowWindow(h, Native.SW_RESTORE);
    }

    static void FrameAdjust(IntPtr h, out int l, out int t, out int r, out int b)
    {
        Native.RECT win, frame;
        Native.GetWindowRect(h, out win);
        int hr = Native.DwmGetWindowAttribute(h, Native.DWMWA_EXTENDED_FRAME_BOUNDS, out frame, 16);
        if (hr != 0)
        {
            l = t = r = b = 0;
            return;
        }
        l = frame.Left - win.Left;
        t = frame.Top - win.Top;
        r = win.Right - frame.Right;
        b = win.Bottom - frame.Bottom;
    }

    public static Native.RECT VisibleBounds(IntPtr h)
    {
        Native.RECT frame;
        int hr = Native.DwmGetWindowAttribute(h, Native.DWMWA_EXTENDED_FRAME_BOUNDS, out frame, 16);
        if (hr != 0) Native.GetWindowRect(h, out frame);
        return frame;
    }

    public static void Place(IntPtr h, Rectangle target)
    {
        if (h == IntPtr.Zero || target.Width < 80 || target.Height < 80) return;
        Restore(h);
        Flush(h, target);
        Flush(h, target);
    }

    static void Flush(IntPtr h, Rectangle target)
    {
        int l, t, r, b;
        FrameAdjust(h, out l, out t, out r, out b);
        int x = target.X - l;
        int y = target.Y - t;
        int w = target.Width + l + r;
        int ht = target.Height + t + b;
        uint flags = Native.SWP_NOACTIVATE | Native.SWP_FRAMECHANGED | Native.SWP_SHOWWINDOW;
        Native.MoveWindow(h, x, y, w, ht, true);
        Native.SetWindowPos(h, Native.HWND_TOP, x, y, w, ht, flags);
    }

    public static bool Drifted(IntPtr h, Rectangle target, int slack)
    {
        if (h == IntPtr.Zero || !Native.IsWindow(h)) return false;
        Native.RECT v = VisibleBounds(h);
        return Math.Abs(v.Left - target.X) > slack
            || Math.Abs(v.Top - target.Y) > slack
            || Math.Abs(v.Width - target.Width) > slack
            || Math.Abs(v.Height - target.Height) > slack;
    }

    public static void Focus(IntPtr h)
    {
        if (h == IntPtr.Zero) return;
        uint cur = Native.GetCurrentThreadId();
        uint fgPid;
        IntPtr fg = Native.GetForegroundWindow();
        Native.GetWindowThreadProcessId(fg, out fgPid);
        uint tgt;
        Native.GetWindowThreadProcessId(h, out tgt);
        Native.AttachThreadInput(cur, fgPid, true);
        Native.AttachThreadInput(cur, tgt, true);
        Native.SetForegroundWindow(h);
        Native.AttachThreadInput(cur, fgPid, false);
        Native.AttachThreadInput(cur, tgt, false);
    }
}
