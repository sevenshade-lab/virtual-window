// 【拆分】关宿主 / 句柄销毁 / 异常时把客户端还回桌面。（从原 AgentHost.cs 原样搬出，逻辑未改）

using System;
using System.Collections.Generic;
using System.Text;
using System.Windows.Forms;

internal sealed partial class HostForm
{
    // 关宿主 = 把四家还成顶层并最小化，**不关进程**。
    // 关键是关之前必须确认每一个都真的还回去了：宿主的 Site 面板一销毁，
    // 还挂在下面的子窗口会被系统连带销毁，Electron 没了窗口就退出进程 ——
    // 2026-09-19 16:40 四家进程一起没了就是这么来的。所以哪个没还回去，
    // 就再试一次；还不行就取消这次关闭，宁可宿主留着，也不能把人家进程弄死。
    void OnHostClosing(object sender, FormClosingEventArgs e)
    {
        try { _tick.Stop(); } catch { }
        try { _hover.Stop(); } catch { }
        try { if (_transition != null && !_transition.IsDisposed) _transition.Close(); } catch { }

        // 一并关掉客户端（默认关闭这个行为）。在还原之前先问、先记下是谁，
        // 因为还原成功之后 slot 里的 Hwnd 就被清空了。
        bool shutAgents = !_collapseOnly && Prefs.Get("closeagents", true);
        var toClose = new List<IntPtr>();
        if (shutAgents)
        {
            var busy = new List<string>();
            for (int i = 0; i < _slots.Count; i++)
            {
                Slot s = _slots[i];
                if (s.Hwnd == IntPtr.Zero || !Native.IsWindow(s.Hwnd)) continue;
                if (s.Custom || s.Kind.Cli) continue;   // Terminal can host unrelated tabs/processes; release only.
                toClose.Add(s.Hwnd);
                if (s.AgentState == "working") busy.Add(s.Kind.Name);
            }
            if (busy.Count > 0)
            {
                // 正在干活的别默不作声地关掉
                var names = new StringBuilder();
                for (int i = 0; i < busy.Count; i++)
                {
                    if (i > 0) names.Append(L.Sep);
                    names.Append(busy[i]);
                }
                DialogResult r = MessageBox.Show(this,
                    L.F("{0} 还在跑。关掉虚拟窗口时要连它们一起关吗？\n\n「是」＝一起关掉；「否」＝只收起虚拟窗口，客户端留着；「取消」＝不关了。", names),
                    L.T(Title), MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                if (r == DialogResult.Cancel)
                {
                    e.Cancel = true;
                    try { _tick.Start(); } catch { }
                    return;
                }
                if (r == DialogResult.No) toClose.Clear();
            }
        }

        var stuck = new List<Slot>();
        for (int i = 0; i < _slots.Count; i++)
        {
            Slot s = _slots[i];
            if (s.Hwnd == IntPtr.Zero || !Native.IsWindow(s.Hwnd)) continue;
            IntPtr h = s.Hwnd;
            if (!Embed.ReleaseToDesktop(s, true))
            {
                s.Hwnd = h;
                s.Embedded = true;
                Watch.Note(h);
                stuck.Add(s);
            }
        }
        Watch.ReleaseAll(true);
        for (int i = stuck.Count - 1; i >= 0; i--)
        {
            IntPtr h = stuck[i].Hwnd;
            if (h == IntPtr.Zero || !Native.IsWindow(h) || Embed.IsTopLevel(h))
                stuck.RemoveAt(i);
        }
        if (stuck.Count > 0)
        {
            e.Cancel = true;
            try { _tick.Start(); } catch { }
            var names = new StringBuilder();
            for (int i = 0; i < stuck.Count; i++)
            {
                if (i > 0) names.Append(L.Sep);
                names.Append(stuck[i].Kind.Name);
                stuck[i].StateLbl.Text = "还原失败，未关闭";
            }
            MessageBox.Show(this,
                L.F("{0} 没能还原成独立窗口，这次先不关宿主，避免把它们的进程一起带走。\n可以点那格的「移出」再试，或者等它响应了再关。", names),
                L.T(Title), MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        // 还原成功之后再关它们：发 WM_CLOSE 走正常退出，不杀进程。
        // 还不回去的那些（stuck）上面已经拦下不关宿主了，走不到这里。
        // 先给 3 秒让它自己退（会话文件写完），还在就结束进程树。
        for (int i = 0; i < toClose.Count; i++) Procs.CloseApp(toClose[i], i == 0 ? 3000 : 400);

        SaveBounds();
        _closed = true;
        WriteStatus(false);
    }

    bool _closed;
    bool _collapseOnly;

    protected override void OnHandleDestroyed(EventArgs e)
    {
        try { if (_kbHook != IntPtr.Zero) Native.UnhookWindowsHookEx(_kbHook); }
        catch { }
        _kbHook = IntPtr.Zero;
        try { Watch.ReleaseAll(true); } catch { }
        try { StopNotifyWatch(); } catch { }
        base.OnHandleDestroyed(e);
    }

    // 宿主自己出异常时先把所有子窗口还成顶层再死。宿主的 HWND 一销毁，
    // 挂在下面的子窗口会被系统连带销毁 —— 那等于把四家客户端全杀了。
    public void EmergencyRelease()
    {
        try { _tick.Stop(); } catch { }
        try { Watch.ReleaseAll(true); } catch { }
        for (int i = 0; i < _slots.Count; i++)
        {
            try
            {
                Slot s = _slots[i];
                if (s.Hwnd != IntPtr.Zero) Embed.RestoreTopLevel(s.Hwnd);
            }
            catch { }
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _tick.Stop();
        _resizeTick.Stop();
        _resizeTick.Dispose();
        Native.UnregisterHotKey(Handle, WindowMenuKey);
        if (_hotkeys)
        {
            for (int i = 0; i <= 4; i++) Native.UnregisterHotKey(Handle, HotkeyBase + i);
            _hotkeys = false;
        }
        if (_reTimer != null) _reTimer.Stop();
        try { Qos.RestoreAll(); } catch { }
        base.OnFormClosed(e);
    }
}
