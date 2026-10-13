// 画的时候抛了异常怎么办。
//
// WinForms 自己的做法是：给这个控件记上「画的时候出过错」，从此只画一个红叉，再把异常往外抛。
// 2026-10-12 全机内存耗尽的那一刻（别的程序吃掉了十几 G，系统报「页面文件太小」），侧栏一颗状态灯
// 就这么成了红叉 —— 多半是 GDI+ 申请不到内存；抛出去的异常又触发了应急处理，把心跳停了（见 Program.cs）。
// 画失败只是这一次没画成，不该留一辈子：各个自绘控件的 OnPaint / OnPaintBackground 都兜一层，
// 失败了到这里记一行日志，过一会儿再画一遍（内存缓过来就好了）。

using System;
using System.Collections.Generic;
using System.Windows.Forms;

internal static class PaintGuard
{
    static readonly HashSet<Control> _again = new HashSet<Control>();
    static Timer _timer;
    static DateTime _lastLog = DateTime.MinValue;
    static int _unlogged;

    // 一共兜住过几次（测试用；也方便调试时看一眼）
    public static int Count;

    public static void Failed(Control c, Exception ex)
    {
        Count++;
        try
        {
            // 子控件借父窗口的 OnPaint 画底（Theme.PaintUnder）是反射调的，异常外面包着一层
            while (ex is System.Reflection.TargetInvocationException && ex.InnerException != null) ex = ex.InnerException;
            // 连着失败时（内存一直紧）别刷屏：两秒记一条，中间的只数数
            DateTime now = DateTime.Now;
            if ((now - _lastLog).TotalSeconds >= 2)
            {
                Log.W("paint failed: " + Who(c) + " " + ex.GetType().Name + ": " + ex.Message
                    + (_unlogged > 0 ? "（这之前另有 " + _unlogged + " 次没记）" : "") + Where(ex));
                _lastLog = now;
                _unlogged = 0;
            }
            else _unlogged++;
            if (c == null || c.IsDisposed) return;
            _again.Add(c);
            if (_timer == null)
            {
                _timer = new Timer { Interval = 1500 };
                _timer.Tick += delegate { Repaint(); };
            }
            if (!_timer.Enabled) _timer.Start();
        }
        catch { }   // 这里再出事也不能往外抛：外面就是 WinForms 的红叉
    }

    static void Repaint()
    {
        _timer.Stop();
        var list = new List<Control>(_again);
        _again.Clear();
        foreach (Control c in list)
        {
            // 连子控件一起：它们的底是借这个控件画的（Theme.PaintUnder），刚才也没画成
            try { if (!c.IsDisposed && c.IsHandleCreated) c.Invalidate(true); }
            catch { }
        }
    }

    static string Who(Control c)
    {
        if (c == null) return "?";
        string name = c.GetType().Name;
        try { if (!string.IsNullOrEmpty(c.Text) && c.Text.Length <= 24) name += "「" + c.Text + "」"; } catch { }
        return name;
    }

    // 异常是在哪儿抛的：调用栈最里面那一行
    static string Where(Exception ex)
    {
        try
        {
            string st = ex.StackTrace;
            if (string.IsNullOrEmpty(st)) return "";
            int nl = st.IndexOf('\n');
            return "  " + (nl > 0 ? st.Substring(0, nl) : st).Trim();
        }
        catch { return ""; }
    }
}
