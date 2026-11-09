// 桌面端自己的更新安装程序在不在跑。
//
// 2026-10-14 23:19 真机：换版时宿主收起把 Antigravity 一起关了，它早下好了 2.23.1，一退出就 quitAndInstall
// （%LOCALAPPDATA%\antigravity-updater\pending\Antigravity-x64.exe --updated --force-run）。
// 40 秒后新宿主按「打开时一并启动桌面端」把它拉起来，安装还没完，它报 language_server.exe ENOENT，
// 进程在、主窗口一直不出来，格子停在「正在启动…」再变「没打开」。
// 所以拉起前先看一眼，在装就等它装完（有上限）。
//
// 认三种（Electron 系常见的）：
//   electron-updater：路径里有「<名字>-updater\」（pending 目录里的安装程序）
//   Squirrel：<名字>\Update.exe
//   别处的安装程序：命令行带 --updated、文件名里有它的名字，但不是它本体（装完它本体也带 --updated 起来）
// 枚举全机进程，别在界面线程上调。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

internal static class Updaters
{
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageNameW(IntPtr h, int flags, StringBuilder buf, ref int size);
    [DllImport("ntdll.dll")] static extern int NtQueryInformationProcess(IntPtr h, int cls, IntPtr buf, int len, out int ret);
    const uint QueryLimited = 0x1000;

    // 这一家能认的名字（小写、去空格）：名单里的进程名、显示名、安装文件名
    static List<string> Tokens(AgentKind k)
    {
        var t = new List<string>();
        Action<string> add = delegate(string x)
        {
            if (string.IsNullOrEmpty(x)) return;
            x = x.Replace(" ", "").ToLowerInvariant();
            if (x.Length >= 4 && !t.Contains(x)) t.Add(x);   // 太短的名字（「ai」之类）会误中别家
        };
        if (k.Processes != null) foreach (string p in k.Processes) add(p);
        add(k.Name);
        string exe = Catalog.DesktopExe(k);
        if (exe != null) add(Path.GetFileNameWithoutExtension(exe));
        return t;
    }

    static string ImagePath(IntPtr h)
    {
        var sb = new StringBuilder(1024);
        int n = sb.Capacity;
        return QueryFullProcessImageNameW(h, 0, sb, ref n) ? sb.ToString(0, n) : null;
    }

    // ProcessCommandLineInformation（60，Win8.1 起）：回的是一个 UNICODE_STRING，字跟在它后面
    static string CommandLine(IntPtr h)
    {
        int len = 0;
        NtQueryInformationProcess(h, 60, IntPtr.Zero, 0, out len);
        if (len <= 0 || len > 1 << 16) return null;
        IntPtr buf = Marshal.AllocHGlobal(len);
        try
        {
            int ret;
            if (NtQueryInformationProcess(h, 60, buf, len, out ret) != 0) return null;
            int bytes = Marshal.ReadInt16(buf) & 0xFFFF;
            IntPtr str = Marshal.ReadIntPtr(buf, IntPtr.Size);
            return str == IntPtr.Zero ? null : Marshal.PtrToStringUni(str, bytes / 2);
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    // 判一个进程是不是这一家的更新安装程序。path、cmd 可以为 null。
    internal static bool Looks(List<string> tokens, string[] selfNames, string path, string cmd)
    {
        if (string.IsNullOrEmpty(path)) return false;
        string p = path.ToLowerInvariant();
        string file = Path.GetFileNameWithoutExtension(p);
        string dir = Path.GetFileName(Path.GetDirectoryName(p) ?? "") ?? "";
        foreach (string t in tokens)
        {
            if (p.Contains("\\" + t + "-updater\\")) return true;
            if (file == "update" && dir.Replace(" ", "") == t) return true;
        }
        if (cmd == null || cmd.IndexOf("--updated", StringComparison.OrdinalIgnoreCase) < 0) return false;
        foreach (string s in selfNames)
            if (string.Equals(file, s, StringComparison.OrdinalIgnoreCase)) return false;   // 装完它本体也带 --updated 起来
        string f = file.Replace(" ", "");
        foreach (string t in tokens) if (f.Contains(t)) return true;
        return false;
    }

    // 在跑就回「文件名（pid）」，没有回 null
    public static string Running(AgentKind k)
    {
        if (k == null || k.Cli) return null;
        List<string> tokens = Tokens(k);
        if (tokens.Count == 0) return null;
        var self = new List<string>();
        if (k.Processes != null) self.AddRange(k.Processes);
        string exe = Catalog.DesktopExe(k);
        if (exe != null) self.Add(Path.GetFileNameWithoutExtension(exe));
        string[] selfNames = self.ToArray();
        foreach (Process pr in Process.GetProcesses())
        {
            try
            {
                if (pr.Id == Environment.ProcessId) continue;
                IntPtr h = OpenProcess(QueryLimited, false, pr.Id);
                if (h == IntPtr.Zero) continue;
                try
                {
                    string path = ImagePath(h);
                    if (path == null) continue;
                    string cmd = null;
                    try { cmd = CommandLine(h); } catch { }
                    if (Looks(tokens, selfNames, path, cmd)) return Path.GetFileName(path) + "（pid " + pr.Id + "）";
                }
                finally { CloseHandle(h); }
            }
            catch { }
            finally { pr.Dispose(); }
        }
        return null;
    }

    // 这一家的桌面端进程在不在。后台常有同名的命令行进程（claude、codex），按路径刨掉
    public static bool AppAlive(AgentKind k)
    {
        if (k == null || k.Processes == null) return false;
        string cli = null;
        try { cli = Launch.CliExe(k); } catch { }
        foreach (string n in k.Processes)
        {
            Process[] ps = Process.GetProcessesByName(n);
            bool any = false;
            foreach (Process p in ps)
            {
                try
                {
                    if (!any)
                    {
                        IntPtr h = OpenProcess(QueryLimited, false, p.Id);
                        if (h != IntPtr.Zero)
                        {
                            string path = ImagePath(h);
                            CloseHandle(h);
                            if (path != null && !IsCli(path, cli)) any = true;
                        }
                    }
                }
                catch { }
                p.Dispose();
            }
            if (any) return true;
        }
        return false;
    }

    static bool IsCli(string path, string cli)
    {
        if (cli != null && string.Equals(Path.GetFullPath(path), Path.GetFullPath(cli), StringComparison.OrdinalIgnoreCase)) return true;
        string p = path.ToLowerInvariant();
        return p.Contains("\\node_modules\\") || p.Contains("\\.local\\bin\\");
    }
}
