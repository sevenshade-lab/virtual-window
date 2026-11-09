// 放大 = 全屏：放大某一格（网格里单独铺满、队列里中间那张铺满）时，整个虚拟窗口盖住整块屏幕（连任务栏），
// 上面那一排工具条收起来，只剩这一格的表头（名字、灯、「还原」）。点「还原」回到原来的样子。
// 用户 2026-10-15：「放大的时候能不能全屏，类似原生全屏的那种。目前放大之后最上面依然存在无用的这些选项」。
//
// 做法跟系统最大化一样：窗口还是「最大化」状态，只是最大化的范围从工作区换成整块屏幕（WM_GETMINMAXINFO 那里认 _full），
// 四边照样多出一圈边框落到屏幕外。不改窗口样式、不置顶：切走到别的程序时它就是一个普通的最大化窗口，任务栏照常出来。

using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

internal sealed partial class HostForm
{
    bool _full;          // 现在是不是全屏（工具条收起、盖住任务栏）
    bool _fullBusy;
    bool _fullWasMax;    // 进全屏之前是不是最大化：出来时回到哪儿
    Rectangle _fullBefore;   // 进全屏之前窗口占的地方（最大化的就是工作区外扩一圈边框），出来时原样摆回去

    [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr h);

    bool ZoomedNow { get { return _queueMode ? (_queueZoom && QueueFront != null) : _solo != null; } }

    // Relayout 开头调：放大了就进全屏，没放大就出来。所有放大 / 还原的入口（按钮、双击表头、快捷键、格子少到一格）
    // 最后都走 Relayout，在这一处跟着就不会漏。
    void SyncFullscreen()
    {
        // 窗体没显示时不动窗口（离屏测试只核对排版，见 LookTests 的「放大 = 全屏」）
        if (_fullBusy || !IsHandleCreated || !Visible || WindowState == FormWindowState.Minimized) return;
        bool want = ZoomedNow;
        if (want == _full) return;
        _fullBusy = true;
        try
        {
            if (want)
            {
                // 看窗口真实的状态：WindowState 是 WinForms 记的，窗体没显示时可能跟窗口对不上
                _fullWasMax = IsZoomed(Handle);
                _fullBefore = Bounds;
                _full = true;
                SetToolbarShown(false);
                WindowState = FormWindowState.Maximized;
                PlaceMaximized();
            }
            else
            {
                _full = false;
                SetToolbarShown(true);
                if (!_fullWasMax) WindowState = FormWindowState.Normal;
                // 最大化的也照记下的摆回去：系统不会因为范围换回工作区就自己重算
                Native.SetWindowPos(Handle, IntPtr.Zero, _fullBefore.X, _fullBefore.Y, _fullBefore.Width, _fullBefore.Height,
                    Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_FRAMECHANGED);
            }
            Log.W("fullscreen " + (want ? "放大，铺满整块屏幕" : "还原，回到" + (_fullWasMax ? "最大化" : "普通窗口")));
        }
        finally { _fullBusy = false; }
    }

    void SetToolbarShown(bool on)
    {
        foreach (Control c in new Control[] { _claim, _arrange, _cycle, _view, _roster, _todo, _settings, _themeBtn, _quota, _langBtn, _minBtn, _maxBtn, _xBtn })
            if (c != null) c.Visible = on;
        // 待办条带跟工具条一起藏；回来时 LayoutToolbar 末尾按有没有待办再定
        if (_todoStrip != null && !on) _todoStrip.Visible = false;
        if (on && _queueMode) _cycle.Visible = false;
    }

    // 已经是最大化时换成整块屏幕：系统不会自己重算，照最大化该占的地方摆一次
    void PlaceMaximized()
    {
        Rectangle r;
        if (!MaxBounds(out r)) return;
        Native.SetWindowPos(Handle, IntPtr.Zero, r.X, r.Y, r.Width, r.Height,
            Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_FRAMECHANGED);
    }

    // 最大化时窗口占的地方（屏幕坐标）：全屏是整块屏幕，平常是工作区；四边各多出一圈边框
    bool MaxBounds(out Rectangle r)
    {
        r = Rectangle.Empty;
        IntPtr mon = Native.MonitorFromWindow(Handle, 2 /* MONITOR_DEFAULTTONEAREST */);
        var mi = new Native.MONITORINFO();
        mi.cbSize = Marshal.SizeOf(mi);
        if (mon == IntPtr.Zero || !Native.GetMonitorInfo(mon, ref mi)) return false;
        Native.RECT a = _full ? mi.rcMonitor : mi.rcWork;
        int fx = Native.GetSystemMetrics(32 /* SM_CXSIZEFRAME */) + Native.GetSystemMetrics(92), fy = FrameY();
        r = new Rectangle(a.Left - fx, a.Top - fy, a.Width + fx * 2, a.Height + fy * 2);
        return true;
    }
}
