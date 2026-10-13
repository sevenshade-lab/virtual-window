// 【拆分】进程节流（QoS）：嵌进来的客户端别被当成后台降速。（从原 AgentHost.cs 原样搬出，逻辑未改）

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

// 嵌进来的客户端别被 Windows 当成后台程序降速（2026-09-20 实测定位）。
//
// Win11 在大小核 CPU 上按「是不是前台程序」给进程定 QoS：前台的跑大核、满频，
// 后台的被塞到小核、降频（EcoQoS）。嵌进宿主后，客户端的窗口成了别人窗口的子窗口，
// 系统不再把它当前台，它的渲染进程跟着被降速。
// 实测（i7-14650HX）：Codex 按键到上屏，单独开 53ms，嵌进来 117ms；
// 把它的渲染、GPU 进程设成「不降速」后嵌进来也是 52ms。OpenCode/Cursor 本来就 20ms 上下，影响小。
// 做法：每 5 秒给每一格客户端的主进程及其子进程设 PROCESS_POWER_THROTTLING EXECUTION_SPEED=关。
// 程序自己特意设了省电的（Chromium 给后台标签页设 EcoQoS）不动；
// 移出或宿主关掉时还给系统自动管理。
internal static class Qos
{
    [StructLayout(LayoutKind.Sequential)]
    struct PPT { public uint Version, ControlMask, StateMask; }
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
    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll")] static extern bool GetProcessInformation(IntPtr h, int cls, ref PPT info, int size);
    [DllImport("kernel32.dll")] static extern bool SetProcessInformation(IntPtr h, int cls, ref PPT info, int size);
    [DllImport("kernel32.dll")] static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32FirstW(IntPtr snap, ref PE32 e);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32NextW(IntPtr snap, ref PE32 e);

    const int ProcessPowerThrottling = 4;
    const uint EXECUTION_SPEED = 1;
    // pid → 创建时间（防 pid 复用后误还原别的进程）
    static readonly Dictionary<int, DateTime> _boosted = new Dictionary<int, DateTime>();

    // 记一份「这些是我关的」到盘上。原因：宿主异常退出（或上一版没还原）留下的标记，
    // 新宿主分不清是客户端自己设的还是上一任留的，就会跳过、也不记账，于是永远没人还原。
    // 2026-09-20 实测就是这样：换版之后四家一直留在不降速状态。
    static string OwnPath()
    {
        string b = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        if (string.IsNullOrEmpty(b)) return null;
        return Path.Combine(Path.Combine(b, "AgentHost"), "qos-owned.txt");
    }

    static void SaveOwned()
    {
        string f = OwnPath();
        if (f == null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(f));
            var sb = new StringBuilder();
            foreach (var kv in _boosted) sb.AppendLine(kv.Key + "	" + kv.Value.Ticks);
            File.WriteAllText(f, sb.ToString(), Encoding.UTF8);
        }
        catch { }
    }

    // 开工时先认领上一任留下的：进程还在、启动时间对得上，就当成自己设的，退出时一并还原。
    public static void AdoptLeftovers()
    {
        string f = OwnPath();
        if (f == null || !File.Exists(f)) return;
        try
        {
            foreach (string line in File.ReadAllLines(f))
            {
                string[] parts = line.Split('	');
                if (parts.Length != 2) continue;
                int pid; long ticks;
                if (!int.TryParse(parts[0], out pid) || !long.TryParse(parts[1], out ticks)) continue;
                var started = new DateTime(ticks);
                try { if (Process.GetProcessById(pid).StartTime != started) continue; }
                catch { continue; }
                lock (_gate) { if (!_boosted.ContainsKey(pid)) _boosted[pid] = started; }
            }
        }
        catch { }
    }

    // 一把锁串起「后台扫描」和「退出还原」：_boosted 是普通字典，两边同时动会出事；
    // 而且还原完之后不能再被后台那趟设回去（2026-09-20 Codex 复核指出，原来只等 500ms 不算数）。
    static readonly object _gate = new object();
    static bool _stopping;
    static int _queued;

    // 后台线程跑，别占着界面线程（跟客户端共用输入队列）。同一时刻只排一个。
    public static void SyncAsync(List<IntPtr> windows)
    {
        if (_stopping) return;
        if (System.Threading.Interlocked.CompareExchange(ref _queued, 1, 0) != 0) return;
        System.Threading.ThreadPool.QueueUserWorkItem(delegate
        {
            try
            {
                lock (_gate)
                {
                    if (_stopping) return;
                    Sync(windows);
                }
            }
            catch { }
            finally { System.Threading.Interlocked.Exchange(ref _queued, 0); }
        });
    }

    public static void Sync(List<IntPtr> windows)
    {
        var roots = new HashSet<int>();
        foreach (IntPtr h in windows)
        {
            uint pid;
            Native.GetWindowThreadProcessId(h, out pid);
            if (pid != 0) roots.Add((int)pid);
        }
        var want = new HashSet<int>(roots);
        if (roots.Count > 0)
        {
            IntPtr snap = CreateToolhelp32Snapshot(0x2 /* TH32CS_SNAPPROCESS */, 0);
            if (snap != IntPtr.Zero && snap != (IntPtr)(-1))
            {
                try
                {
                    var e = new PE32();
                    e.dwSize = (uint)Marshal.SizeOf(typeof(PE32));
                    for (bool ok = Process32FirstW(snap, ref e); ok; ok = Process32NextW(snap, ref e))
                        if (roots.Contains((int)e.th32ParentProcessID)) want.Add((int)e.th32ProcessID);
                }
                finally { CloseHandle(snap); }
            }
        }
        // 每轮都重查一遍：Chromium 自己也会来回改渲染进程的这一位（切后台设省电、回前台又交还系统），
        // 只记一次的话它交还系统之后就又被降速了。
        bool changed = false;
        foreach (int pid in want)
        {
            DateTime started;
            if (Boost(pid, out started) && !_boosted.ContainsKey(pid)) { _boosted[pid] = started; changed = true; }
        }
        var drop = new List<int>();
        foreach (var kv in _boosted)
            if (!want.Contains(kv.Key)) drop.Add(kv.Key);
        foreach (int pid in drop)
        {
            Restore(pid, _boosted[pid]);
            _boosted.Remove(pid);
            changed = true;
        }
        if (changed) SaveOwned();
    }

    // 关宿主时调用。先挂停止牌，再进锁 —— 正在跑的那趟扫完就走，之后排队的直接放弃，
    // 所以还原之后不会再有人把节流关掉。
    public static void RestoreAll()
    {
        _stopping = true;
        lock (_gate)
        {
            foreach (var kv in _boosted) Restore(kv.Key, kv.Value);
            _boosted.Clear();
            SaveOwned();
        }
    }

    static bool Boost(int pid, out DateTime started)
    {
        started = DateTime.MinValue;
        IntPtr h = OpenProcess(0x1000 /* QUERY_LIMITED */ | 0x0200 /* SET_INFORMATION */, false, pid);
        if (h == IntPtr.Zero) return false;
        try
        {
            var p = new PPT { Version = 1 };
            if (!GetProcessInformation(h, ProcessPowerThrottling, ref p, Marshal.SizeOf(p))) return false;
            if ((p.ControlMask & EXECUTION_SPEED) != 0) return false;   // 程序自己管着，不动
            // 只动 EXECUTION_SPEED 这一位；别的位（Chromium 给 GPU 进程设的 IGNORE_TIMER_RESOLUTION 等）原样留着
            var q = new PPT { Version = 1, ControlMask = p.ControlMask | EXECUTION_SPEED, StateMask = p.StateMask & ~EXECUTION_SPEED };
            if (!SetProcessInformation(h, ProcessPowerThrottling, ref q, Marshal.SizeOf(q))) return false;
            try { started = Process.GetProcessById(pid).StartTime; } catch { }
            return true;
        }
        finally { CloseHandle(h); }
    }

    static void Restore(int pid, DateTime started)
    {
        try
        {
            if (started != DateTime.MinValue && Process.GetProcessById(pid).StartTime != started) return;
        }
        catch { return; }
        IntPtr h = OpenProcess(0x1000 | 0x0200, false, pid);
        if (h == IntPtr.Zero) return;
        try
        {
            var p = new PPT { Version = 1 };
            // 只还我们设过的那种状态；程序后来自己改成省电了就别碰
            if (GetProcessInformation(h, ProcessPowerThrottling, ref p, Marshal.SizeOf(p))
                && (p.ControlMask & EXECUTION_SPEED) != 0 && (p.StateMask & EXECUTION_SPEED) == 0)
            {
                var q = new PPT { Version = 1, ControlMask = p.ControlMask & ~EXECUTION_SPEED, StateMask = p.StateMask };
                SetProcessInformation(h, ProcessPowerThrottling, ref q, Marshal.SizeOf(q));
            }
        }
        finally { CloseHandle(h); }
    }
}
