// agent 名单：内置预设 + 用户自己加的（%LOCALAPPDATA%\AgentHost\agent-catalog.json）。
// 思路跟 CC Switch 一样：常见的都预置好，没装的照样列着（启动时告诉你去哪装），
// 名单外的自己往 json 里加，不用改代码、不用重新编。
//
// 有官方钩子的几家，桌面端状态看钩子写的会话文件（HookStates）；
// 其余没有官方钩子的一律看进程忙不忙（CpuStates）兜底。CLI 会话里 Claude Code / Codex 走钩子（CliStates）。

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

internal static class Catalog
{
    // 运行中可以增删自定义的（Agent 名单页），所以不是 readonly；读的人拿到的是一份快照数组。
    public static AgentKind[] All { get; private set; } = Load();

    static AgentKind K(string id, string name, string[] procs, string cli, string home)
    {
        var k = new AgentKind(id, name, procs, cli == null ? new string[0] : new string[] { cli });
        k.CliCommand = cli;
        k.Home = home;
        k.Exes = new string[0];
        k.Shortcuts = new string[0];
        k.CliPaths = new string[0];
        return k;
    }

    static AgentKind[] Presets()
    {
        var list = new List<AgentKind>();
        AgentKind k;

        k = K("cursor", "Cursor", new[] { "Cursor" }, "cursor-agent", "https://cursor.com");
        k.Exes = new[] { @"%LOCALAPPDATA%\Programs\cursor\Cursor.exe", @"%ProgramFiles%\cursor\Cursor.exe" };
        k.Shortcuts = new[] { "Cursor" };
        k.CliProcesses = new[] { "cursor-agent", "agent" };
        k.CliPaths = new[] { @"%LOCALAPPDATA%\cursor-agent\agent.cmd" };
        k.DesktopState = "hooks";
        k.Chromium = true;
        // 不带参数启动 Cursor.exe 时，它先联网问 api2.cursor.sh「这次开哪种窗口」，再按 restoreWindows
        // 把上次的 IDE 窗口全恢复 —— 所以 IDE 会跟着一起开，网络慢时 Agents 窗迟迟不出、宿主等到超时
        // （用户 2026-09-27 报「Cursor 容易启动失败、IDE 和桌面端一起启动」）。
        // --glass 算「明确指定首窗」：跳过联网判断、不恢复 IDE（getPathsToOpen 的 glass 分支强制空窗），
        // Cursor 已在跑时会聚焦已有的 Agents 窗而不是新开一个空 IDE。Cursor 3.x out/main.js 里读到的。
        k.DesktopArgs = "--glass";
        list.Add(k);

        // Claude 商店版进程名是小写 claude；NameIn 忽略大小写，这里列全只是给人看。
        k = K("claude", "Claude", new[] { "Claude", "claude" }, "claude", "https://claude.com/download");
        k.AppsFolder = "Claude_pzs8sxrjxfjjc!Claude";
        k.DesktopState = "hooks";
        k.Chromium = true;
        list.Add(k);

        // Codex 桌面版 exe 叫 ChatGPT.exe
        k = K("codex", "Codex", new[] { "Codex", "ChatGPT" }, "codex", "https://developers.openai.com/codex");
        k.AppsFolder = "OpenAI.Codex_2p2nqsd0c76g0!App";
        k.DesktopState = "hooks";
        k.Chromium = true;
        list.Add(k);

        k = K("opencode", "OpenCode", new[] { "OpenCode" }, "opencode", "https://opencode.ai");
        k.Exes = new[] { @"%LOCALAPPDATA%\Programs\@opencode-aidesktop\OpenCode.exe",
                         @"%LOCALAPPDATA%\Programs\@opencode-ai\desktop\OpenCode.exe",
                         @"%LOCALAPPDATA%\Programs\OpenCode\OpenCode.exe" };
        // OpenCode 的钩子是插件（HookSetup.OpenCode）；插件还没加载（没重启过）时退回看 CPU —— 它闲着才 0.005 核，CPU 猜得准
        k.DesktopState = "hooks";
        k.HookFallbackCpu = true;
        k.Chromium = true;
        list.Add(k);

        k = K("antigravity", "Antigravity", new[] { "Antigravity" }, null, "https://antigravity.google");
        k.Exes = new[] { @"%LOCALAPPDATA%\Programs\antigravity\Antigravity.exe" };
        k.Shortcuts = new[] { "Antigravity" };
        // 钩子早就装了（HookSetup.Antigravity），这里却一直没标 hooks，状态全靠 CPU 猜：
        // 它一个长任务里每次等模型回话 CPU 就静下来，10 秒后灯就变「完成」，下一步又变回来（2026-09-28 用户报状态不正常）。
        // 钩子实测每一步都有 PreInvocation / PostToolUse / PostInvocation，整段都是 working。
        k.DesktopState = "hooks";
        k.HookFallbackCpu = true;
        k.Chromium = true;
        list.Add(k);

        k = K("hermes", "Hermes", new[] { "Hermes" }, "hermes", "https://hermes-agent.nousresearch.com");
        k.Exes = new[] { @"%LOCALAPPDATA%\hermes\hermes-agent\apps\desktop\release\win-unpacked\Hermes.exe" };
        k.Shortcuts = new[] { "Hermes" };
        k.CliPaths = new[] { @"%LOCALAPPDATA%\hermes\bin\hermes.exe" };
        k.Chromium = true;
        list.Add(k);


        k = K("grok", "Grok Build", new string[0], "grok", "https://x.ai");
        list.Add(k);


        k = K("pi", "Pi", new string[0], "pi", "https://github.com/badlogic/pi-mono");
        list.Add(k);

        k = K("omp", "Oh My Pi", new string[0], "omp", "https://github.com/can1357/oh-my-pi");
        list.Add(k);

        k = K("gemini", "Gemini CLI", new string[0], "gemini", "https://github.com/google-gemini/gemini-cli");
        list.Add(k);

        k = K("openclaw", "OpenClaw", new[] { "OpenClaw" }, "openclaw", "https://openclaw.ai");
        k.Shortcuts = new[] { "OpenClaw" };
        list.Add(k);

        k = K("codebuddy", "CodeBuddy", new[] { "CodeBuddy" }, "codebuddy", "https://www.codebuddy.ai");
        k.Shortcuts = new[] { "CodeBuddy" };
        list.Add(k);

        k = K("devin", "Devin", new[] { "Devin" }, "devin", "https://devin.ai");
        k.Shortcuts = new[] { "Devin" };
        list.Add(k);

        return list.ToArray();
    }

    public static string UserFile()
    {
        string b = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        return string.IsNullOrEmpty(b) ? null : Path.Combine(b, "AgentHost", "agent-catalog.json");
    }

    // agent-catalog.json：一个数组，每项 {id, name, processes[], exe, shortcut, cli, cliPath, home}。
    // id 跟预设重名 = 覆盖预设里写了的那几项；新 id = 加一家。写坏了就当没有（启动日志里记一句）。
    static AgentKind[] Load()
    {
        var list = new List<AgentKind>(Presets());
        string f = UserFile();
        try
        {
            if (f != null && File.Exists(f))
            {
                using (JsonDocument doc = JsonDocument.Parse(File.ReadAllText(f)))
                    foreach (JsonElement e in doc.RootElement.EnumerateArray())
                    {
                        string id = Str(e, "id");
                        if (string.IsNullOrWhiteSpace(id)) continue;
                        AgentKind k = list.Find(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
                        if (k == null)
                        {
                            k = K(id, Str(e, "name") ?? id, new string[0], null, null);
                            k.Custom = true;
                            list.Add(k);
                        }
                        if (Str(e, "name") != null) { k.Name = Str(e, "name"); k.TitleMark = "虚拟窗口 · " + k.Name; }
                        string[] procs = Arr(e, "processes");
                        if (procs != null) k.Processes = procs;
                        if (Str(e, "exe") != null) k.Exes = new[] { Str(e, "exe") };
                        if (Str(e, "shortcut") != null) k.Shortcuts = new[] { Str(e, "shortcut") };
                        if (Str(e, "cli") != null) { k.CliCommand = Str(e, "cli"); k.CliProcesses = new[] { k.CliCommand }; }
                        if (Str(e, "cliPath") != null) k.CliPaths = new[] { Str(e, "cliPath") };
                        if (Str(e, "home") != null) k.Home = Str(e, "home");
                    }
            }
        }
        catch (Exception ex) { Log.W("agent-catalog.json 读不了，只用内置名单：" + ex.Message); }
        return list.ToArray();
    }

    static string Str(JsonElement e, string name)
    {
        JsonElement v;
        return e.TryGetProperty(name, out v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }

    static string[] Arr(JsonElement e, string name)
    {
        JsonElement v;
        if (!e.TryGetProperty(name, out v) || v.ValueKind != JsonValueKind.Array) return null;
        var r = new List<string>();
        foreach (JsonElement x in v.EnumerateArray()) if (x.ValueKind == JsonValueKind.String) r.Add(x.GetString());
        return r.ToArray();
    }

    // ---- 自定义 agent：写回 agent-catalog.json，并立刻加进名单（不用重启宿主）----

    public sealed class CustomEntry
    {
        public string Id, Name, Process, Exe, Cli, Home;
    }

    public static AgentKind AddCustom(CustomEntry c)
    {
        string f = UserFile();
        if (f == null) throw new InvalidOperationException("找不到 LOCALAPPDATA");
        var arr = new System.Text.Json.Nodes.JsonArray();
        if (File.Exists(f))
        {
            var parsed = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(f)) as System.Text.Json.Nodes.JsonArray;
            if (parsed != null) arr = parsed;
        }
        for (int i = arr.Count - 1; i >= 0; i--)
            if (string.Equals((string)arr[i]?["id"], c.Id, StringComparison.OrdinalIgnoreCase)) arr.RemoveAt(i);
        var o = new System.Text.Json.Nodes.JsonObject { ["id"] = c.Id, ["name"] = c.Name };
        if (!string.IsNullOrWhiteSpace(c.Process)) o["processes"] = new System.Text.Json.Nodes.JsonArray(c.Process.Trim());
        if (!string.IsNullOrWhiteSpace(c.Exe)) o["exe"] = c.Exe.Trim();
        if (!string.IsNullOrWhiteSpace(c.Cli)) o["cli"] = c.Cli.Trim();
        if (!string.IsNullOrWhiteSpace(c.Home)) o["home"] = c.Home.Trim();
        arr.Add(o);
        Directory.CreateDirectory(Path.GetDirectoryName(f));
        File.WriteAllText(f, arr.ToJsonString(new JsonSerializerOptions { WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }), new System.Text.UTF8Encoding(false));

        var k = ById(c.Id);
        if (k == null)
        {
            k = K(c.Id, c.Name, new string[0], null, null);
            k.Custom = true;
            var list = new List<AgentKind>(All); list.Add(k); All = list.ToArray();
        }
        k.Name = c.Name; k.TitleMark = "虚拟窗口 · " + c.Name;
        if (!string.IsNullOrWhiteSpace(c.Process)) k.Processes = new[] { c.Process.Trim() };
        if (!string.IsNullOrWhiteSpace(c.Exe)) k.Exes = new[] { c.Exe.Trim() };
        if (!string.IsNullOrWhiteSpace(c.Cli)) { k.CliCommand = c.Cli.Trim(); k.CliProcesses = new[] { k.CliCommand }; }
        if (!string.IsNullOrWhiteSpace(c.Home)) k.Home = c.Home.Trim();
        _desktop.Remove(k.Id);
        return k;
    }

    // 只删自定义的；预设的删不掉（最多用同 id 覆盖）
    public static void RemoveCustom(AgentKind k)
    {
        if (k == null || !k.Custom) return;
        string f = UserFile();
        if (f != null && File.Exists(f))
        {
            var arr = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(f)) as System.Text.Json.Nodes.JsonArray;
            if (arr != null)
            {
                for (int i = arr.Count - 1; i >= 0; i--)
                    if (string.Equals((string)arr[i]?["id"], k.Id, StringComparison.OrdinalIgnoreCase)) arr.RemoveAt(i);
                File.WriteAllText(f, arr.ToJsonString(new JsonSerializerOptions { WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }), new System.Text.UTF8Encoding(false));
            }
        }
        var list = new List<AgentKind>(All); list.Remove(k); All = list.ToArray();
    }

    // 从名字生成 id：只留字母数字和横线，全是中文就用 custom-xxxx
    public static string MakeId(string name)
    {
        var sb = new System.Text.StringBuilder();
        foreach (char ch in (name ?? "").Trim().ToLowerInvariant())
            if ((ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9')) sb.Append(ch);
            else if (ch == ' ' || ch == '-' || ch == '_') { if (sb.Length > 0 && sb[sb.Length - 1] != '-') sb.Append('-'); }
        string id = sb.ToString().Trim('-');
        if (id.Length == 0) id = "custom-" + Guid.NewGuid().ToString("N").Substring(0, 6);
        return id;
    }

    public static AgentKind ById(string id)
    {
        for (int i = 0; i < All.Length; i++)
            if (string.Equals(All[i].Id, id, StringComparison.OrdinalIgnoreCase))
                return All[i];
        return null;
    }

    // 排在第几个。格子顺序必须跟这里一致 —— Ctrl+Alt+N 按的是 _slots[N-1]，
    // 而以前 _slots 是「谁先被认进来谁在前」，数字对上谁全看那次开机的运气
    // （实测 status.json 里是 opencode/codex/claude/cursor，跟这张表对不上）。
    // 不在名单里的（以后「任意窗口入格」）排最后。
    public static int RankOf(AgentKind k)
    {
        for (int i = 0; i < All.Length; i++)
            if (All[i] == k) return i;
        return All.Length;
    }

    // ---- 装没装 ----

    public static string Expand(string p) { return string.IsNullOrEmpty(p) ? p : Environment.ExpandEnvironmentVariables(p); }

    public static string DesktopExe(AgentKind k)
    {
        if (k.Exes != null)
            foreach (string p in k.Exes) { string x = Expand(p); if (!string.IsNullOrEmpty(x) && File.Exists(x)) return x; }
        return null;
    }

    static readonly Dictionary<string, bool> _desktop = new Dictionary<string, bool>();

    // 桌面端装了没有：有安装文件、开始菜单有快捷方式、或者是商店版。结果缓存（查开始菜单要扫目录）。
    public static bool HasDesktop(AgentKind k)
    {
        bool v;
        if (_desktop.TryGetValue(k.Id, out v)) return v;
        v = !string.IsNullOrEmpty(k.AppsFolder) || DesktopExe(k) != null
            || (k.Shortcuts != null && k.Shortcuts.Length > 0 && Launch.FindShortcut(k.Shortcuts) != null);
        _desktop[k.Id] = v;
        return v;
    }

    public static void WriteReport()
    {
        var sb = new System.Text.StringBuilder();
        foreach (AgentKind k in All)
        {
            string cli = string.IsNullOrEmpty(k.CliCommand) ? "-" : (HasCli(k) ? Launch.CliExe(k) : "未安装(" + k.CliCommand + ")");
            string desk = k.Processes == null || k.Processes.Length == 0 ? "-" : (HasDesktop(k) ? (DesktopExe(k) ?? (k.AppsFolder != null ? "商店版" : "快捷方式")) : "未安装");
            string cliState = string.IsNullOrEmpty(k.CliCommand) ? "-" : (k.DesktopState == "hooks" ? "钩子" : "cpu");
            sb.AppendLine(k.Id + "	" + k.Name + "	桌面:" + desk + "	状态:" + k.DesktopState + "	CLI:" + cli + "	状态:" + cliState + (k.Custom ? "	(自定义)" : ""));
        }
        try
        {
            string f = Path.Combine(Path.GetDirectoryName(UserFile()), "agents-report.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(f));
            File.WriteAllText(f, sb.ToString(), new System.Text.UTF8Encoding(false));
        }
        catch { }
    }

    static readonly Dictionary<string, string> _cli = new Dictionary<string, string>();

    // CLI 在哪（要翻 PATH，结果缓存；名单页一张卡片要问好几次）
    public static string CliPath(AgentKind k)
    {
        if (string.IsNullOrEmpty(k.CliCommand)) return null;
        string key = k.Id + "|" + k.CliCommand;
        string v;
        if (_cli.TryGetValue(key, out v)) return v;
        v = Launch.CliExe(k);
        _cli[key] = v;
        return v;
    }

    public static bool HasCli(AgentKind k) { return CliPath(k) != null; }

    // 名单页打开时清一下，刚装好的能认出来
    public static void ForgetInstallCache() { _cli.Clear(); _desktop.Clear(); }
}
