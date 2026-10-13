// 【拆分】关掉一家客户端的进程树。（从原 AgentHost.cs 原样搬出，逻辑未改）

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

// 关掉一家客户端：先让它自己退，不行再结束进程树。
//
// 用户 2026-09-20 定的语义：点了那家自己的 ✕ 就是"我不要它了"。
// Electron 这类应用点 ✕ 多半只是把窗口藏进托盘、进程全留着（四家加起来 7GB），
// 所以我们接管这件事：窗口没了就把进程收干净。
// 先 WM_CLOSE 留几秒给它写完自己的会话文件，真不走才强制结束 —— 直接杀容易把
// 正在写的流水截断（Codex 那份 rollout 就是一直开着写的）。
internal static class Procs
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct PE32
    {
        public uint dwSize, cntUsage, th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID, cntThreads, th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }
    [DllImport("kernel32.dll")] static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32FirstW(IntPtr snap, ref PE32 e);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32NextW(IntPtr snap, ref PE32 e);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

    static List<int> Tree(int root)
    {
        var kids = new Dictionary<int, List<int>>();
        IntPtr snap = CreateToolhelp32Snapshot(0x2, 0);
        if (snap == IntPtr.Zero || snap == (IntPtr)(-1)) return new List<int> { root };
        try
        {
            var e = new PE32();
            e.dwSize = (uint)Marshal.SizeOf(typeof(PE32));
            for (bool ok = Process32FirstW(snap, ref e); ok; ok = Process32NextW(snap, ref e))
            {
                int parent = (int)e.th32ParentProcessID, me = (int)e.th32ProcessID;
                if (!kids.ContainsKey(parent)) kids[parent] = new List<int>();
                kids[parent].Add(me);
            }
        }
        finally { CloseHandle(snap); }
        // 广度优先收集，最后从叶子往根杀
        var all = new List<int>();
        var queue = new Queue<int>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            int p = queue.Dequeue();
            if (all.Contains(p)) continue;
            all.Add(p);
            List<int> cs;
            if (kids.TryGetValue(p, out cs))
                for (int i = 0; i < cs.Count; i++) if (cs[i] != p) queue.Enqueue(cs[i]);
        }
        return all;
    }

    /// 结束这个窗口所属的整棵进程树。gentleMs > 0 时先给窗口发 WM_CLOSE 等它自己退。
    /// 这个进程（或它的子进程）现在还有没有可见的顶层窗口。
    /// 有 = 它只是把窗口重开了（Cursor 的「重启窗口」、页面重载），不是被你关掉了。
    public static bool HasVisibleWindow(int pid)
    {
        var tree = Tree(pid);
        bool found = false;
        Native.EnumWindows(delegate(IntPtr h, IntPtr lp)
        {
            if (found) return false;
            if (!Native.IsWindowVisible(h)) return true;
            uint p;
            Native.GetWindowThreadProcessId(h, out p);
            if (!tree.Contains((int)p)) return true;
            var sb = new StringBuilder(120);
            Native.GetWindowText(h, sb, sb.Capacity);
            if (sb.Length == 0) return true;            // 无标题的多半是隐藏辅助窗口
            found = true;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    public static void CloseApp(IntPtr hwnd, int gentleMs) { CloseApp(hwnd, gentleMs, 0); }

    public static void CloseApp(IntPtr hwnd, int gentleMs, int knownPid)
    {
        uint pid = (uint)knownPid;
        if (pid == 0 && hwnd != IntPtr.Zero && Native.IsWindow(hwnd)) Native.GetWindowThreadProcessId(hwnd, out pid);
        if (pid == 0) return;
        if (gentleMs > 0 && Native.IsWindow(hwnd))
        {
            Native.PostMessage(hwnd, (int)Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            DateTime until = DateTime.Now.AddMilliseconds(gentleMs);
            while (DateTime.Now < until)
            {
                try { if (Process.GetProcessById((int)pid).HasExited) return; }
                catch { return; }                      // 进程没了 = 它自己退干净了
                System.Threading.Thread.Sleep(150);
            }
        }
        List<int> tree = Tree((int)pid);
        for (int i = tree.Count - 1; i >= 0; i--)      // 先叶子后根
        {
            try
            {
                Process p = Process.GetProcessById(tree[i]);
                if (!p.HasExited) p.Kill();
            }
            catch { }
        }
    }
}
