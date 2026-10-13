// 【拆分】启动各家桌面端 / CLI。（从原 AgentHost.cs 原样搬出，逻辑未改）

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

internal static class Launch
{
    // 上一次启动失败时找过哪些地方。以前只回一句「没找到启动入口」，
    // 用户没法知道是该装、该改路径，还是名字对不上。
    public static string LastWhy;

    // 装哪儿都能找到：开始菜单里的快捷方式。
    //
    // 写死安装路径靠不住 —— 用户 2026-09-20 报「Cursor 在虚拟窗口里启动不了」，
    // 因为 Cursor 装在了别的盘，而这里只找了 %LOCALAPPDATA%\Programs\cursor\。
    // 直接 ShellExecute 那个 .lnk，Windows 自己会解析目标，连 Store 应用也覆盖得到。
    // 开始菜单里的 .lnk 扫一次记住（名单十几家，每家都递归扫一遍开始菜单，名单页打开要好几秒）
    static string[] _lnks;
    static DateTime _lnksAt;

    public static string FindShortcut(string[] names)
    {
        if (_lnks == null || (DateTime.Now - _lnksAt).TotalMinutes > 2)
        {
            var all = new List<string>();
            foreach (string root in new string[] {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Windows\Start Menu\Programs"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Microsoft\Windows\Start Menu\Programs") })
            {
                try { if (Directory.Exists(root)) all.AddRange(Directory.GetFiles(root, "*.lnk", SearchOption.AllDirectories)); }
                catch { }
            }
            _lnks = all.ToArray();
            _lnksAt = DateTime.Now;
        }
        for (int n = 0; n < names.Length; n++)
            foreach (string f in _lnks)
                if (string.Equals(Path.GetFileNameWithoutExtension(f), names[n], StringComparison.OrdinalIgnoreCase))
                    return f;
        return null;
    }

    static bool StartShortcut(string[] names, string args)
    {
        string lnk = FindShortcut(names);
        if (lnk == null) return false;
        var psi = new ProcessStartInfo(lnk);
        psi.UseShellExecute = true;          // .lnk 必须走 shell；Arguments 会接在快捷方式自带参数后面
        if (!string.IsNullOrEmpty(args)) psi.Arguments = args;
        Process.Start(psi);
        return true;
    }

    // Chromium 系桌面端嵌进来要带的参数（见 AgentKind.Chromium）。2026-09-26 Codex 实测，同样 4 个对话来回切：
    //   单独开：点击→画面开始变 平均 ~245ms，没有空白帧
    //   嵌入、只关 DirectComposition：~395ms，每次点击后都有一段空白
    //   再关原生窗口遮挡计算 + 不把「被遮挡」的窗口降后台：~229ms，连拍 138 帧 0 空白
    // 嵌成子窗口后，Chromium 按顶层窗口算遮挡，把整个盖住它的宿主当成挡住它的窗口，
    // 于是把页面当隐藏：降速、停画；点一下又重算成可见 —— 用户看到的「不稳定、闪、切对话要等」。
    const string EmbedArgs = "--disable-direct-composition --disable-features=CalculateNativeWinOcclusion --disable-backgrounding-occluded-windows";

    // 商店版带参数启动：explorer shell:AppsFolder 传不了参数，走 IApplicationActivationManager
    [System.Runtime.InteropServices.ComImport, System.Runtime.InteropServices.Guid("2e941141-7f97-4756-ba1d-9decde894a3d"),
     System.Runtime.InteropServices.InterfaceType(System.Runtime.InteropServices.ComInterfaceType.InterfaceIsIUnknown)]
    interface IApplicationActivationManager
    {
        int ActivateApplication([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string appUserModelId,
                                [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string arguments,
                                int options, out uint processId);
    }
    [System.Runtime.InteropServices.ComImport, System.Runtime.InteropServices.Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")]
    class ApplicationActivationManager { }

    static bool ActivatePackaged(string aumid, string args)
    {
        try
        {
            var m = (IApplicationActivationManager)new ApplicationActivationManager();
            uint pid;
            return m.ActivateApplication(aumid, args, 0, out pid) == 0;
        }
        catch { return false; }
    }

    // 按名单启动：商店版 → 安装文件 → 开始菜单快捷方式，哪个有用哪个。
    // （Cursor 桌面端不带文件夹参数：带了会开 IDE 而不是 Cursor Agents；要带 --glass，见 Catalog。）
    public static bool Start(AgentKind k)
    {
        try
        {
            if (k.Cli) return StartCli(k);
            LastWhy = null;
            string args = k.Chromium ? EmbedArgs : null;
            if (!string.IsNullOrEmpty(k.DesktopArgs)) args = args == null ? k.DesktopArgs : k.DesktopArgs + " " + args;
            if (!string.IsNullOrEmpty(k.AppsFolder))
            {
                if (args != null && ActivatePackaged(k.AppsFolder, args)) return true;
                Process.Start(new ProcessStartInfo("explorer.exe", @"shell:AppsFolder\" + k.AppsFolder) { UseShellExecute = true });
                return true;
            }
            string exe = Catalog.DesktopExe(k);
            if (exe != null)
            {
                var psi = new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe) };
                if (args != null) psi.Arguments = args;
                Process.Start(psi);
                return true;
            }
            if (k.Shortcuts != null && k.Shortcuts.Length > 0 && StartShortcut(k.Shortcuts, args)) return true;
            var where = new List<string>();
            if (k.Shortcuts != null) foreach (string n in k.Shortcuts) where.Add(L.F("开始菜单里名叫 {0} 的快捷方式", n));
            if (k.Exes != null) foreach (string e in k.Exes) where.Add(e);
            LastWhy = L.F("{0} 桌面端没装", k.Name) + (where.Count > 0 ? L.F("（找过：{0}）", string.Join(L.Sep, where)) : "")
                    + (string.IsNullOrEmpty(k.Home) ? "" : L.F("。安装：{0}", k.Home));
        }
        catch (Exception ex) { LastWhy = L.F("启动失败：{0}", ex.Message); }
        return false;
    }

    static string FirstExisting(string[] paths)
    {
        for (int i = 0; i < paths.Length; i++)
            if (!string.IsNullOrEmpty(paths[i]) && File.Exists(paths[i])) return paths[i];
        return null;
    }

    internal static string CliExe(AgentKind k)
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string roam = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string name = k.CliCommand;
        if (string.IsNullOrEmpty(name)) return null;
        var cands = new List<string>();
        if (k.CliPaths != null) foreach (string c in k.CliPaths) cands.Add(Catalog.Expand(c));
        cands.Add(Path.Combine(home, @".local\bin\" + name + ".exe"));
        cands.Add(Path.Combine(local, @"cursor-agent\" + name + ".cmd"));
        cands.Add(Path.Combine(roam, @"npm\" + name + ".cmd"));
        cands.Add(Path.Combine(roam, @"npm\" + name + ".ps1"));
        string found = FirstExisting(cands.ToArray());
        if (found != null) return found;
        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            foreach (string ext in new string[] { ".exe", ".cmd", ".ps1" })
            {
                try { string path = Path.Combine(dir.Trim('"'), name + ext); if (File.Exists(path)) return path; }
                catch { }
            }
        }
        return null;
    }

    internal static string CliScript(AgentKind k, string exe, string ws)
    {
        // Escape PowerShell single-quoted literals; CLI runs interactively with
        // its normal trust/approval settings and receives no automatic prompt.
        // AGENTHOST_SLOT：全局钩子（HookSetup 装的）在这个会话里触发时带上它，宿主就知道是哪一格。
        // 以前每次启动再挂一遍 --settings / -c，全局钩子装上以后会重复触发，去掉了。
        string hooks = "";
        return "$Host.UI.RawUI.WindowTitle = '" + k.TitleMark.Replace("'", "''")
            + "'; $env:AGENTHOST_SLOT = '" + k.Id.Replace("'", "''")
            + "'; Set-Location -LiteralPath '" + ws.Replace("'", "''")
            + "'; & '" + exe.Replace("'", "''") + "'" + hooks;
    }

    static bool StartCli(AgentKind k)
    {
        LastWhy = null;
        string exe = CliExe(k);
        if (exe == null)
        {
            LastWhy = L.F("没有找到 {0}（命令 {1}），请先安装或把命令加入 PATH", k.Name, k.CliCommand)
                    + (string.IsNullOrEmpty(k.Home) ? L.T("。") : L.F("。安装：{0}", k.Home));
            return false;
        }
        string ws = Cfg.WorkspaceFor(k);
        if (!Directory.Exists(ws)) { LastWhy = L.F("工作目录不存在：{0}", ws); return false; }
        string wt = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            @"Microsoft\WindowsApps\wt.exe");
        if (!File.Exists(wt))
        {
            LastWhy = L.T("启动 CLI 会话需要 Windows Terminal；也可以先自行打开终端，再从菜单纳入。");
            return false;
        }
        CliStates.Reset(k.Id);   // 同一格上一回会话留下的 done 别让新会话一开就排进队列
        string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(CliScript(k, exe, ws)));
        Process.Start(new ProcessStartInfo(wt)
        {
            WorkingDirectory = ws,
            // Force a separate top-level window even when Terminal prefers tabs.
            Arguments = "-w new new-tab --title \"" + k.TitleMark + "\" --suppressApplicationTitle powershell.exe -NoLogo -NoProfile -NoExit -EncodedCommand " + encoded,
            UseShellExecute = true
        });
        return true;
    }

}
