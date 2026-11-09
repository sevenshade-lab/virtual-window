// 每一格客户端整棵进程树占多少内存，和整机还剩多少。
//
// 用户 2026-10-21：几家桌面端一起开着内存吃紧。宿主自己才几十兆，占内存的是嵌进来的那几家（Electron 本体、
// 它们各自拉起的会话进程），宿主改不了别家的内存、也不该替用户关掉谁 —— 先把账算清楚、摆出来：
// 悬停在表头上最后一行写「内存 6.9 GB · 20 个进程」，整机提交内存快用完时状态那一行提一句、点名占最多的那家。
//
// 算的是私有提交字节（PrivateUsage，跟任务管理器「提交大小」一个口径）：Electron 几个进程之间共享的页只算一次，
// 比工作集更接近「关掉它能省多少」。扫进程树要枚举全机进程，跟 QoS 一趟（每 5 秒、后台线程）顺带做，界面线程只读结果。
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

internal static class MemStats
{
    public sealed class Tree
    {
        public long Bytes;        // 私有提交
        public long WorkingSet;   // 真占着物理内存的
        public int Procs;
    }

    static readonly Dictionary<int, Tree> _byRoot = new Dictionary<int, Tree>();
    static readonly object _lock = new object();
    // 整机：物理内存还剩多少、提交内存用了多少 / 上限多少（GlobalMemoryStatusEx）
    static long _physAvail, _physTotal, _commitUsed, _commitLimit;
    static DateTime _sampledAt = DateTime.MinValue;

    // 后台线程调：roots = 每格窗口所属的进程号
    public static void Sample(IEnumerable<int> roots)
    {
        Dictionary<int, List<int>> kids = CpuStates.ChildrenMap();
        var fresh = new Dictionary<int, Tree>();
        foreach (int root in roots)
        {
            if (fresh.ContainsKey(root)) continue;
            var t = new Tree();
            var seen = new HashSet<int>();
            var stack = new Stack<int>();
            stack.Push(root);
            while (stack.Count > 0 && seen.Count < 512)
            {
                int pid = stack.Pop();
                if (!seen.Add(pid)) continue;
                long ws;
                long b = PrivateBytes(pid, out ws);
                if (b >= 0) { t.Bytes += b; t.WorkingSet += ws; t.Procs++; }
                List<int> l;
                if (kids.TryGetValue(pid, out l)) foreach (int ch in l) stack.Push(ch);
            }
            fresh[root] = t;
        }
        var ms = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX)) };
        bool sys = GlobalMemoryStatusEx(ref ms);
        lock (_lock)
        {
            _byRoot.Clear();
            foreach (var kv in fresh) _byRoot[kv.Key] = kv.Value;
            if (sys)
            {
                _physAvail = (long)ms.ullAvailPhys; _physTotal = (long)ms.ullTotalPhys;
                _commitLimit = (long)ms.ullTotalPageFile; _commitUsed = _commitLimit - (long)ms.ullAvailPageFile;
            }
            _sampledAt = DateTime.Now;
        }
    }

    public static Tree Of(int rootPid)
    {
        lock (_lock) { Tree t; return _byRoot.TryGetValue(rootPid, out t) ? t : null; }
    }

    // 悬停提示那一行；这一格的窗口还没扫到就没有这一行
    public static string Line(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return null;
        uint pid;
        Native.GetWindowThreadProcessId(hwnd, out pid);
        Tree t = pid == 0 ? null : Of((int)pid);
        if (t == null || t.Procs == 0) return null;
        string line = L.F("内存 {0}（在用 {1}）· {2} 个进程", Fmt(t.Bytes), Fmt(t.WorkingSet), t.Procs);
        bool trimmed;
        lock (_lock) { trimmed = _trimmedAt.ContainsKey((int)pid); }
        return trimmed ? line + L.T(" · 闲着，已收紧") : line;
    }

    // ---- 闲着的格子收紧内存 ----
    //
    // 用户 2026-10-21：「灯灰着说明没在工作，这时候可以选择性地降低内存消耗，正常工作的时候再分配合适的内存」。
    // 做法是把它整棵进程树的工作集清空（SetProcessWorkingSetSizeEx(-1, -1)，就是任务管理器看到的那个「内存」）：
    // 页面退到待机列表 / 页面文件，物理内存当场腾出来；它一干活，系统按需再调回来，宿主不用管「分配」。
    // 提交内存（它申请了多少）不变 —— 那只有它自己或关掉它才能减。
    // 谁算闲着、闲多久才收，由调用方（心跳）定；这里只管一棵树收一次、十分钟内不重复收。
    const int TrimAgainMinutes = 10;
    static readonly Dictionary<int, DateTime> _trimmedAt = new Dictionary<int, DateTime>();
    static int _trimming;

    // 界面线程调：roots = 该收的几格（进程号 → 名字，只用来写日志）。后台线程做，立刻返回
    public static void TrimAsync(Dictionary<int, string> roots)
    {
        var due = new Dictionary<int, string>();
        lock (_lock)
        {
            foreach (var kv in roots)
            {
                DateTime at;
                if (_trimmedAt.TryGetValue(kv.Key, out at) && DateTime.Now < at.AddMinutes(TrimAgainMinutes)) continue;
                due[kv.Key] = kv.Value;
            }
            // 不再该收的（干活了、换到中间了）从记录里去掉：悬停提示不再写「已收紧」，下次闲下来再收
            var drop = new List<int>();
            foreach (int pid in _trimmedAt.Keys) if (!roots.ContainsKey(pid)) drop.Add(pid);
            foreach (int pid in drop) _trimmedAt.Remove(pid);
        }
        if (due.Count == 0) return;
        if (System.Threading.Interlocked.Exchange(ref _trimming, 1) == 1) return;
        System.Threading.ThreadPool.QueueUserWorkItem(delegate
        {
            try
            {
                Dictionary<int, List<int>> kids = CpuStates.ChildrenMap();
                foreach (var kv in due)
                {
                    long before = 0, after = 0;
                    int n = 0;
                    foreach (int pid in TreeOf(kv.Key, kids))
                    {
                        long ws;
                        if (PrivateBytes(pid, out ws) < 0) continue;
                        before += ws;
                        if (EmptyWorkingSet(pid)) n++;
                        if (PrivateBytes(pid, out ws) >= 0) after += ws;
                    }
                    lock (_lock) { _trimmedAt[kv.Key] = DateTime.Now; }
                    Log.W("mem trim " + kv.Value + ": 工作集 " + Fmt(before) + " -> " + Fmt(after) + "，" + n + " 个进程");
                }
            }
            catch { }
            finally { System.Threading.Interlocked.Exchange(ref _trimming, 0); }
        });
    }

    static List<int> TreeOf(int root, Dictionary<int, List<int>> kids)
    {
        var order = new List<int>();
        var seen = new HashSet<int>();
        var stack = new Stack<int>();
        stack.Push(root);
        while (stack.Count > 0 && seen.Count < 512)
        {
            int pid = stack.Pop();
            if (!seen.Add(pid)) continue;
            order.Add(pid);
            List<int> l;
            if (kids.TryGetValue(pid, out l)) foreach (int ch in l) stack.Push(ch);
        }
        return order;
    }

    // 整机紧不紧：提交内存用掉九成以上，或者物理内存只剩不到 1.5 GB。界面线程调，只读上一次扫的结果
    public static bool Tight(out string left)
    {
        lock (_lock)
        {
            left = null;
            if (_sampledAt == DateTime.MinValue || _commitLimit <= 0) return false;
            bool tight = _commitUsed * 10 >= _commitLimit * 9 || (_physTotal > 0 && _physAvail < 1536L * 1024 * 1024);
            if (!tight) return false;
            left = _commitUsed * 10 >= _commitLimit * 9 ? Fmt(_commitLimit - _commitUsed) : Fmt(_physAvail);
            return true;
        }
    }

    // 1.9 GB / 640 MB
    public static string Fmt(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024) return string.Format("{0:0.0} GB", bytes / (1024.0 * 1024 * 1024));
        return string.Format("{0:0} MB", bytes / (1024.0 * 1024));
    }

    // ---- Win32 ----

    [StructLayout(LayoutKind.Sequential)]
    struct PROCESS_MEMORY_COUNTERS_EX
    {
        public uint cb, PageFaultCount;
        public UIntPtr PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage,
            QuotaPeakNonPagedPoolUsage, QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage, PrivateUsage;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buf);
    [DllImport("kernel32.dll", EntryPoint = "K32GetProcessMemoryInfo")]
    static extern bool GetProcessMemoryInfo(IntPtr h, out PROCESS_MEMORY_COUNTERS_EX counters, uint size);
    [DllImport("kernel32.dll")] static extern bool SetProcessWorkingSetSizeEx(IntPtr h, IntPtr min, IntPtr max, uint flags);

    // 私有提交和工作集；拿不到（没权限、刚退出）返回 -1
    static long PrivateBytes(int pid, out long workingSet)
    {
        workingSet = 0;
        IntPtr h = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (h == IntPtr.Zero) return -1;
        try
        {
            PROCESS_MEMORY_COUNTERS_EX c;
            uint size = (uint)Marshal.SizeOf(typeof(PROCESS_MEMORY_COUNTERS_EX));
            if (!GetProcessMemoryInfo(h, out c, size)) return -1;
            workingSet = (long)c.WorkingSetSize.ToUInt64();
            return (long)c.PrivateUsage.ToUInt64();
        }
        finally { CloseHandle(h); }
    }

    // 清空工作集（min = max = -1）。要 PROCESS_SET_QUOTA | PROCESS_QUERY_INFORMATION；别家的进程开不了就算了
    static bool EmptyWorkingSet(int pid)
    {
        IntPtr h = OpenProcess(0x0100 | 0x0400, false, pid);
        if (h == IntPtr.Zero) return false;
        try { return SetProcessWorkingSetSizeEx(h, new IntPtr(-1), new IntPtr(-1), 0); }
        finally { CloseHandle(h); }
    }
}
