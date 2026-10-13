using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

// 没有官方钩子的 agent（Antigravity、Hermes、DSH、Grok Bot、自己挑进来的窗口……）靠这个兜底：
// 看它的进程树忙不忙。持续有 CPU 占用 = 在干活；安静满 QuietSecs 秒 = 做完了（等你）。
//
// 会误判的两种情况，调用方传 muted 挡掉：
//   - 你正在它里面打字（它有焦点、你刚动过键盘鼠标）—— 不然一打字就被当成在干活挪到左边去；
//   - 它刚换到中间的头几秒 —— 按新位置重画会冒一阵 CPU。
// 扫进程树要枚举全机进程，放后台线程（界面线程跟四家共用输入队列，见 Qos 那段的教训）。
internal static class CpuStates
{
    // 忙 = 比它自己平时闲着高出 BusyAbove 个核，并且至少 MinBusy。固定阈值不行：
    // 实测闲着时 OpenCode 0.005 核、Cursor 0.12、Codex 桌面端 0.19（2026-09-25）。
    const double BusyAbove = 0.25;
    const double MinBusy = 0.12;
    const int QuietSecs = 10;          // 安静这么久才算做完（模型长思考时 CPU 也可能一时很低）

    sealed class Track
    {
        public int Root;
        public long Cpu;               // 上次采样时整棵树的 CPU 时间（100ns）
        public long At;                // 上次采样的时刻（Stopwatch 毫秒）
        public DateTime LastBusy = DateTime.MinValue;
        public bool EverBusy;
        public string State;
        public int Secs;
        public double Floor = -1;      // 平时闲着大概多少核：往低处立刻跟，往高处慢慢爬
        public int Streak;             // 连着忙了几拍：一次性的尖峰不算干活
    }

    static readonly Dictionary<string, Track> _t = new Dictionary<string, Track>();
    static readonly object _lock = new object();
    static int _running;
    static readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

    // roots：格子 id → 它窗口所属进程的 pid；muted：这一拍不算数的格子
    public static void SampleAsync(Dictionary<string, int> roots, HashSet<string> muted)
    {
        if (roots.Count == 0) return;
        if (Interlocked.Exchange(ref _running, 1) == 1) return;
        Task.Run(delegate
        {
            try { Sample(roots, muted); }
            catch { }
            finally { Interlocked.Exchange(ref _running, 0); }
        });
    }

    public static string StateOf(string id)
    {
        lock (_lock) { Track t; return _t.TryGetValue(id, out t) ? t.State : null; }
    }

    public static int SecsOf(string id)
    {
        lock (_lock) { Track t; return _t.TryGetValue(id, out t) ? t.Secs : 0; }
    }

    static void Sample(Dictionary<string, int> roots, HashSet<string> muted)
    {
        Dictionary<int, List<int>> kids = ChildrenMap();
        long now = _clock.ElapsedMilliseconds;
        foreach (var kv in roots)
        {
            long cpu = TreeCpu(kv.Value, kids);
            lock (_lock)
            {
                Track t;
                if (!_t.TryGetValue(kv.Key, out t) || t.Root != kv.Value)
                {
                    // 新来的 / 换了进程：先记个底，下一拍才有增量可比
                    _t[kv.Key] = new Track { Root = kv.Value, Cpu = cpu, At = now };
                    continue;
                }
                double wallSec = (now - t.At) / 1000.0;
                double cores = wallSec > 0.2 ? (cpu - t.Cpu) / 1e7 / wallSec : 0;
                t.Cpu = cpu;
                t.At = now;
                if (t.Floor < 0 || cores < t.Floor) t.Floor = cores;
                else t.Floor += (cores - t.Floor) * 0.02;
                bool hot = cores >= Math.Max(MinBusy, t.Floor + BusyAbove) && !muted.Contains(kv.Key);
                // 连续两拍（约 4 秒）都忙才算：OpenCode 闲着偶尔冒一下，以前一冒就「干过活、做完了」亮黄灯
                t.Streak = hot ? t.Streak + 1 : 0;
                bool busy = t.Streak >= 2 || (hot && t.State == "working");
                if (busy) { t.LastBusy = DateTime.Now; t.EverBusy = true; }
                double quiet = (DateTime.Now - t.LastBusy).TotalSeconds;
                if (t.EverBusy && quiet < QuietSecs) { t.State = "working"; t.Secs = 0; }
                else if (t.EverBusy) { t.State = "done"; t.Secs = (int)quiet; }
                else { t.State = "idle"; t.Secs = 0; }
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)] struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }
    [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LASTINPUTINFO li);

    // 离上一次键盘鼠标输入多少毫秒
    public static uint InputIdleMs()
    {
        var li = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO)) };
        if (!GetLastInputInfo(ref li)) return uint.MaxValue;
        return unchecked((uint)Environment.TickCount - li.dwTime);
    }

    // ---- 进程树 ----

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
    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll")] static extern bool GetProcessTimes(IntPtr h, out long create, out long exit, out long kernel, out long user);

    static Dictionary<int, List<int>> ChildrenMap()
    {
        var map = new Dictionary<int, List<int>>();
        IntPtr snap = CreateToolhelp32Snapshot(0x2 /* TH32CS_SNAPPROCESS */, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return map;
        try
        {
            var e = new PE32 { dwSize = (uint)Marshal.SizeOf(typeof(PE32)) };
            if (!Process32FirstW(snap, ref e)) return map;
            do
            {
                int parent = (int)e.th32ParentProcessID, pid = (int)e.th32ProcessID;
                if (parent == pid) continue;
                List<int> l;
                if (!map.TryGetValue(parent, out l)) map[parent] = l = new List<int>();
                l.Add(pid);
            } while (Process32NextW(snap, ref e));
        }
        finally { CloseHandle(snap); }
        return map;
    }

    static long TreeCpu(int root, Dictionary<int, List<int>> kids)
    {
        long sum = 0;
        var seen = new HashSet<int>();
        var stack = new Stack<int>();
        stack.Push(root);
        while (stack.Count > 0 && seen.Count < 512)
        {
            int pid = stack.Pop();
            if (!seen.Add(pid)) continue;
            IntPtr h = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
            if (h != IntPtr.Zero)
            {
                long c, x, k, u;
                if (GetProcessTimes(h, out c, out x, out k, out u)) sum += k + u;
                CloseHandle(h);
            }
            List<int> l;
            if (kids.TryGetValue(pid, out l)) foreach (int ch in l) stack.Push(ch);
        }
        return sum;
    }
}
