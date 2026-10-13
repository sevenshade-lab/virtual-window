using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

// 给各家 agent 装 / 卸「状态钩子」（照 Que 的外部会话接法）：钩子调 agenthost-hook.cjs，
// 它把每个会话在干什么写进 %LOCALAPPDATA%\AgentHost\sessions\，宿主读（HookStates）。
//
// 规矩：
//   - 只增删我们自己那几条（命令里含 agenthost-hook.cjs 的），用户自己写的钩子一个字不动；
//   - 每个配置文件第一次改之前备份一份 <文件>.agenthost-bak；
//   - 需要回答「放行 / 拦截」的事件（Cursor 的 preToolUse、beforeShellExecution…）不注册，
//     不替用户批准任何东西；Antigravity 的 PreToolUse 也不注册（Que 说那只是猜测，会误报）。
// 装哪些：该家的配置目录在（装过、用过）才装，没装的跳过。
internal static class HookSetup
{
    public const string Marker = "agenthost-hook.cjs";

    static string Home { get { return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile); } }
    static string BaseDir { get { return Path.Combine(Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? Home, "AgentHost"); } }
    public static string ScriptPath { get { return Path.Combine(BaseDir, "bin", "agenthost-hook.cjs"); } }
    public static string SessionsDir { get { return Path.Combine(BaseDir, "sessions"); } }

    static readonly string[] ClaudeEvents = { "SessionStart", "UserPromptSubmit", "PreToolUse", "PermissionRequest", "Notification",
                                              "PostToolUse", "PostToolUseFailure", "Stop", "StopFailure", "SessionEnd" };
    static readonly string[] CodexEvents = { "SessionStart", "UserPromptSubmit", "PreToolUse", "PermissionRequest", "PostToolUse", "Stop" };
    static readonly string[] CursorEvents = { "sessionStart", "beforeSubmitPrompt", "postToolUse", "afterAgentResponse", "stop", "sessionEnd" };
    static readonly string[] AntigravityEvents = { "PreInvocation", "PostInvocation", "PostToolUse", "Stop" };

    static string NodeExe()
    {
        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
        {
            try { string p = Path.Combine(dir.Trim('"'), "node.exe"); if (File.Exists(p)) return p; } catch { }
        }
        string pf = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "node.exe");
        return File.Exists(pf) ? pf : null;
    }

    // 钩子命令。Claude（Git Bash）、Cursor（cmd）认「正斜杠 + 双引号」。
    // PowerShell 不认：开头带引号的一串只是个字符串，整条是语法错误、什么都不执行 ——
    // Codex 在 Windows 上用 PowerShell 跑钩子，装好、信任好了却一次都没上报（2026-09-26 查到）。
    static string Command(string node, string kind, string eventArg = null)
    {
        return "\"" + node.Replace('\\', '/') + "\" \"" + ScriptPath.Replace('\\', '/') + "\" " + kind
               + (eventArg == null ? "" : " " + eventArg);
    }

    // 给 PowerShell 跑的（Codex）：用 8.3 短路径去掉空格、不带引号，cmd / PowerShell / Windows PowerShell 都能直接跑；
    // 拿不到短路径（系统关了 8.3 短名）才退回 PowerShell 的 & 调用写法。
    static string PsCommand(string node, string kind)
    {
        string n = Short(node), s = Short(ScriptPath);
        if (n.IndexOf(' ') < 0 && s.IndexOf(' ') < 0) return n + " " + s + " " + kind;
        return "& \"" + node + "\" \"" + ScriptPath + "\" " + kind;
    }

    // 不带引号的命令（短路径去掉空格）。拿不到短路径就返回 null：宁可不装，也别装一条会把客户端卡死的钩子。
    static string NoQuoteCommand(string node, string kind, string eventArg)
    {
        string n = Short(node), s = Short(ScriptPath);
        if (n.IndexOf(' ') >= 0 || s.IndexOf(' ') >= 0) return null;
        return n + " " + s + " " + kind + " " + eventArg;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern int GetShortPathName(string longPath, StringBuilder shortPath, int size);

    static string Short(string path)
    {
        if (path.IndexOf(' ') < 0) return path;
        try
        {
            var sb = new StringBuilder(520);
            int n = GetShortPathName(path, sb, sb.Capacity);
            if (n > 0 && n < sb.Capacity) return sb.ToString();
        }
        catch { }
        return path;
    }

    static void Backup(string file)
    {
        try
        {
            string bak = file + ".agenthost-bak";
            if (File.Exists(file) && !File.Exists(bak)) File.Copy(file, bak);
        }
        catch { }
    }

    static void WriteAtomic(string file, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file));
        string tmp = file + ".agenthost-tmp";
        File.WriteAllText(tmp, text, new UTF8Encoding(false));
        File.Copy(tmp, file, true);
        File.Delete(tmp);
    }

    static readonly JsonSerializerOptions Pretty = new JsonSerializerOptions
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    static JsonObject ReadJsonObject(string file)
    {
        if (!File.Exists(file)) return new JsonObject();
        string raw = File.ReadAllText(file);
        if (raw.Trim().Length == 0) return new JsonObject();
        var node = JsonNode.Parse(raw, null, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var obj = node as JsonObject;
        if (obj == null) throw new InvalidDataException(file + " 不是一个 JSON 对象，不敢动");
        return obj;
    }

    static bool Owned(JsonNode n) { return n != null && n.ToJsonString().Contains(Marker); }

    // ---- 各家 ----

    // Claude：~/.claude/settings.json 的 hooks.<事件> = [ {hooks:[{type,command,timeout}]} , …用户的 ]
    static string Claude(bool install, string node)
    {
        string dir = Path.Combine(Home, ".claude");
        if (!Directory.Exists(dir)) return "Claude：没装，跳过";
        string file = Path.Combine(dir, "settings.json");
        JsonObject root = ReadJsonObject(file);
        var hooks = root["hooks"] as JsonObject ?? new JsonObject();
        foreach (string ev in ClaudeEvents)
        {
            var arr = hooks[ev] as JsonArray ?? new JsonArray();
            for (int i = arr.Count - 1; i >= 0; i--) if (Owned(arr[i])) arr.RemoveAt(i);
            if (install)
                arr.Add(new JsonObject { ["hooks"] = new JsonArray(new JsonObject
                {
                    ["type"] = "command", ["command"] = Command(node, "claude"), ["timeout"] = 10
                }) });
            if (arr.Count == 0) hooks.Remove(ev); else hooks[ev] = arr;
        }
        if (hooks.Count == 0) root.Remove("hooks"); else root["hooks"] = hooks;
        Backup(file);
        WriteAtomic(file, root.ToJsonString(Pretty));
        return "Claude：" + (install ? "已装（" + ClaudeEvents.Length + " 个事件）" : "已卸") + " · " + file;
    }

    // Cursor：~/.cursor/hooks.json {version:1, hooks:{<事件>:[{command,timeout}]}}
    static string Cursor(bool install, string node)
    {
        string dir = Path.Combine(Home, ".cursor");
        if (!Directory.Exists(dir)) return "Cursor：没装，跳过";
        string file = Path.Combine(dir, "hooks.json");
        JsonObject root = ReadJsonObject(file);
        if (root["version"] == null) root["version"] = 1;
        var hooks = root["hooks"] as JsonObject ?? new JsonObject();
        foreach (string ev in CursorEvents)
        {
            var arr = hooks[ev] as JsonArray ?? new JsonArray();
            for (int i = arr.Count - 1; i >= 0; i--) if (Owned(arr[i])) arr.RemoveAt(i);
            if (install) arr.Add(new JsonObject { ["command"] = Command(node, "cursor"), ["timeout"] = 10 });
            if (arr.Count == 0) hooks.Remove(ev); else hooks[ev] = arr;
        }
        root["hooks"] = hooks;
        Backup(file);
        WriteAtomic(file, root.ToJsonString(Pretty));
        return "Cursor：" + (install ? "已装（" + CursorEvents.Length + " 个事件，不含要回答放行的那几个）" : "已卸") + " · " + file;
    }

    // Antigravity：~/.gemini/config/hooks.json 里自己占一个键 agenthost-session-state = {<事件>: [...]}
    static string Antigravity(bool install, string node)
    {
        string dir = Path.Combine(Home, ".gemini", "config");
        if (!Directory.Exists(Path.Combine(Home, ".gemini"))) return "Antigravity：没装，跳过";
        string file = Path.Combine(dir, "hooks.json");
        JsonObject root = ReadJsonObject(file);
        root.Remove("agenthost-session-state");
        if (install && NoQuoteCommand(node, "antigravity", "Stop") == null)
            return "Antigravity：node 或钩子路径里有空格、又拿不到短路径，没装（装了会让它卡住）";
        if (install)
        {
            var bundle = new JsonObject();
            foreach (string ev in AntigravityEvents)
            {
                // 不能用 Command() 那种带引号的写法：Antigravity 把命令交给 cmd 时引号被转义成 \"，cmd 报
                // 「'\"C:/Program Files/nodejs/node.exe\"' 不是内部或外部命令」→ 钩子失败 → 对话卡在 Working…、
                // 停止按钮报 Executor is not currently running（用户 2026-09-27 踩到）。跟 Codex 一样用短路径、不带引号。
                var entry = new JsonObject { ["type"] = "command", ["command"] = NoQuoteCommand(node, "antigravity", ev), ["timeout"] = 2 };
                bundle[ev] = ev.EndsWith("ToolUse")
                    ? new JsonArray(new JsonObject { ["matcher"] = "*", ["hooks"] = new JsonArray(entry) })
                    : new JsonArray(entry);
            }
            root["agenthost-session-state"] = bundle;
        }
        Backup(file);
        WriteAtomic(file, root.ToJsonString(Pretty));
        return "Antigravity：" + (install ? "已装（" + AntigravityEvents.Length + " 个事件）" : "已卸") + " · " + file;
    }

    // Codex：~/.codex/config.toml，[features] hooks = true + 追加一段带起止标记的 [[hooks.*]]。
    // 新钩子要在 Codex 里信任一次（桌面端在它的钩子管理页，命令行是 /hooks；按命令哈希记，命令不变就一直有效）。
    const string CodexBegin = "# >>> agenthost hooks（虚拟窗口自动生成，卸载会整段删掉，别手改）";
    const string CodexEnd = "# <<< agenthost hooks";

    static string Codex(bool install, string node)
    {
        string dir = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (string.IsNullOrEmpty(dir)) dir = Path.Combine(Home, ".codex");
        if (!Directory.Exists(dir)) return "Codex：没装，跳过";
        string file = Path.Combine(dir, "config.toml");
        string text = File.Exists(file) ? File.ReadAllText(file) : "";
        int a = text.IndexOf(CodexBegin, StringComparison.Ordinal);
        string kept = "";
        if (a >= 0)
        {
            int b = text.IndexOf(CodexEnd, a, StringComparison.Ordinal);
            if (b < 0) return "Codex：config.toml 里我们那段只有开头没有结尾，不敢动，请手动检查";
            // Codex 记信任（[hooks.state.'…'] trusted_hash）时会把表插进我们这段中间 ——
            // 以前整段删掉重写，重装一次就把用户点过的信任全抹了（2026-09-26 查到：00:31 信任、00:40 重装后全没了）。
            // 段里不是我们生成的表（不以 [[hooks. 开头的）原样留下，挪到段外。
            kept = ForeignTables(text.Substring(a + CodexBegin.Length, b - a - CodexBegin.Length));
            b += CodexEnd.Length;
            text = text.Substring(0, a).TrimEnd('\r', '\n') + "\n" + text.Substring(b).TrimStart('\r', '\n');
        }
        if (kept.Length > 0) text = text.TrimEnd('\r', '\n') + "\n\n" + kept;
        if (install)
        {
            text = EnableCodexHooksFeature(text);
            var sb = new StringBuilder();
            sb.Append('\n').Append(CodexBegin).Append('\n');
            string cmd = PsCommand(node, "codex");
            foreach (string ev in CodexEvents)
            {
                sb.Append("[[hooks.").Append(ev).Append("]]\n");
                sb.Append("[[hooks.").Append(ev).Append(".hooks]]\n");
                sb.Append("type = \"command\"\n");
                sb.Append("command = '").Append(cmd).Append("'\n");   // TOML 字面量：里面的双引号原样
                sb.Append("timeout = 10\n");
            }
            sb.Append(CodexEnd).Append('\n');
            text = text.TrimEnd('\r', '\n') + "\n" + sb;
        }
        Backup(file);
        WriteAtomic(file, text);
        return "Codex：" + (install ? "已装（" + CodexEvents.Length + " 个事件；第一次要在 Codex 的钩子管理页（命令行里是 /hooks）信任一下）" : "已卸") + " · " + file;
    }

    // 从我们那段里挑出别人（Codex 自己）插进来的表，按原样拼回去
    static string ForeignTables(string block)
    {
        var outSb = new StringBuilder();
        bool keep = false;
        foreach (string raw in block.Replace("\r\n", "\n").Split('\n'))
        {
            string t = raw.TrimStart();
            if (t.StartsWith("[", StringComparison.Ordinal)) keep = !t.StartsWith("[[hooks.", StringComparison.Ordinal);
            if (keep) outSb.Append(raw).Append('\n');
        }
        return outSb.ToString().Trim('\n').Length == 0 ? "" : outSb.ToString().TrimEnd('\n') + "\n";
    }

    // 用户已有 [features] 就在它里面加 hooks = true（再追加一个 [features] 是非法 TOML）；没有就新建
    static string EnableCodexHooksFeature(string text)
    {
        var lines = new List<string>(text.Replace("\r\n", "\n").Split('\n'));
        int feat = lines.FindIndex(l => l.Trim() == "[features]");
        if (feat < 0) return text.TrimEnd('\n') + "\n\n[features]\nhooks = true\n";
        for (int i = feat + 1; i < lines.Count && !lines[i].TrimStart().StartsWith("["); i++)
        {
            string t = lines[i].Trim();
            if (t.StartsWith("hooks") && t.Substring(5).TrimStart().StartsWith("="))
            {
                lines[i] = "hooks = true";
                return string.Join("\n", lines);
            }
        }
        lines.Insert(feat + 1, "hooks = true");
        return string.Join("\n", lines);
    }

    // OpenCode：没有命令钩子，只有插件 —— 往 ~/.config/opencode/plugin/ 放一个我们自己的 agenthost-state.js，
    // 它在 OpenCode 进程里直接写会话文件（见 hooks/agenthost-opencode.js）。整个文件都是我们的，卸载就删。
    // 新装的要重启一次 OpenCode 才加载。
    const string OpenCodePluginName = "agenthost-state.js";

    static string OpenCodeDir()
    {
        string d = Environment.GetEnvironmentVariable("OPENCODE_CONFIG_DIR");
        if (!string.IsNullOrEmpty(d)) return d;
        string xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        return Path.Combine(string.IsNullOrEmpty(xdg) ? Path.Combine(Home, ".config") : xdg, "opencode");
    }

    static string OpenCode(bool install, string node)
    {
        string dir = OpenCodeDir();
        if (!Directory.Exists(dir)) return "OpenCode：没装，跳过";
        // 老版本认 plugin/，新版本两个都认；用户已经有哪个就用哪个
        string sub = Directory.Exists(Path.Combine(dir, "plugins")) && !Directory.Exists(Path.Combine(dir, "plugin")) ? "plugins" : "plugin";
        foreach (string s in new[] { "plugin", "plugins" })
        {
            string f = Path.Combine(dir, s, OpenCodePluginName);
            if (File.Exists(f)) File.Delete(f);
        }
        if (!install) return "OpenCode：已卸（重启 OpenCode 后生效）";
        string file = Path.Combine(dir, sub, OpenCodePluginName);
        using (Stream s = typeof(HookSetup).Assembly.GetManifestResourceStream("agenthost-opencode.js"))
        {
            if (s == null) throw new InvalidOperationException("exe 里没有嵌 OpenCode 插件");
            using (var r = new StreamReader(s)) WriteAtomic(file, r.ReadToEnd());
        }
        return "OpenCode：已装插件（重启 OpenCode 后生效） · " + file;
    }

    // ---- 对外 ----

    // 设置页用：这家的钩子装没装。null = 这家本身就没装（配置目录不在），不列出来
    public static bool? Installed(string kind)
    {
        try
        {
            switch (kind)
            {
                case "claude":
                    {
                        string d = Path.Combine(Home, ".claude");
                        if (!Directory.Exists(d)) return null;
                        string f = Path.Combine(d, "settings.json");
                        return File.Exists(f) && File.ReadAllText(f).Contains(Marker);
                    }
                case "cursor":
                    {
                        string d = Path.Combine(Home, ".cursor");
                        if (!Directory.Exists(d)) return null;
                        string f = Path.Combine(d, "hooks.json");
                        return File.Exists(f) && File.ReadAllText(f).Contains(Marker);
                    }
                case "codex":
                    {
                        string d = Environment.GetEnvironmentVariable("CODEX_HOME");
                        if (string.IsNullOrEmpty(d)) d = Path.Combine(Home, ".codex");
                        if (!Directory.Exists(d)) return null;
                        string f = Path.Combine(d, "config.toml");
                        return File.Exists(f) && File.ReadAllText(f).Contains(CodexBegin);
                    }
                case "antigravity":
                    {
                        if (!Directory.Exists(Path.Combine(Home, ".gemini"))) return null;
                        string f = Path.Combine(Home, ".gemini", "config", "hooks.json");
                        return File.Exists(f) && File.ReadAllText(f).Contains("agenthost-session-state");
                    }
                case "opencode":
                    {
                        string d = OpenCodeDir();
                        if (!Directory.Exists(d)) return null;
                        return File.Exists(Path.Combine(d, "plugin", OpenCodePluginName)) || File.Exists(Path.Combine(d, "plugins", OpenCodePluginName));
                    }
            }
        }
        catch { return false; }
        return null;
    }

    static void ExtractScript()
    {
        using (Stream s = typeof(HookSetup).Assembly.GetManifestResourceStream("agenthost-hook.cjs"))
        {
            if (s == null) throw new InvalidOperationException("exe 里没有嵌钩子小程序");
            Directory.CreateDirectory(Path.GetDirectoryName(ScriptPath));
            using (var f = File.Create(ScriptPath)) s.CopyTo(f);
        }
    }

    // 装 / 卸全部，返回每家一行结果。任何一家失败不影响别的家。
    public static List<string> Apply(bool install)
    {
        var report = new List<string>();
        string node = NodeExe();
        if (install)
        {
            if (node == null) { report.Add("没找到 Node.js（node.exe），装不了钩子：先装 Node.js 再来"); return report; }
            try { ExtractScript(); report.Add("钩子小程序：" + ScriptPath); }
            catch (Exception ex) { report.Add("钩子小程序释放失败：" + ex.Message); return report; }
        }
        if (node == null) node = "node";
        Func<bool, string, string>[] all = { Claude, Codex, Cursor, Antigravity, OpenCode };
        foreach (var f in all)
        {
            try { report.Add(f(install, node)); }
            catch (Exception ex) { report.Add(f.Method.Name + "：失败，没改 —— " + ex.Message); }
        }
        return report;
    }

    public static List<string> ApplyToFile(bool install)
    {
        List<string> report = Apply(install);
        try
        {
            Directory.CreateDirectory(BaseDir);
            File.WriteAllLines(Path.Combine(BaseDir, "hooks-report.txt"), report, new UTF8Encoding(false));
        }
        catch { }
        return report;
    }
}
