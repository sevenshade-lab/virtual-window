using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

// 表头 / 侧栏的悬停提示：第一行是它在干什么、多久了；停下来的两种再补几句，
// 不切过去就知道它要你做什么（用户 2026-10-12）。
//   在问你 → 是等你批准一步操作（带工具名）、问了你一个问题，还是计划写好了等你看；
//   做完了 → 它最后那段话的开头；末尾是个问句就把那一句也带上（多半在等你回话，不是单纯交货）；
//   这两种都带上它在哪个文件夹 —— 一格合并了这一家的所有会话，停下来的不一定是画面上那个。
// 只进悬停，表头照旧只有一个灯（用户定：界面少加小字）。补的那几句来自钩子顺带记下的（HookNote），
// 状态是看 CPU 猜的那几家没有，只有第一行。
internal static class TipNotes
{
    public static string Tip(string name, string state, int secs, HookNote note)
    {
        string d = Dur(secs);
        if (state == "working") return L.F("{0} 在跑 · 已经 {1}", name, d);
        if (state == "asking") return Join(AskLine(name, note), Lines(state, note, name));
        if (state == "done") return Join(L.F("{0} 做完了 · {1}前", name, d), Lines(state, note, name));
        if (state == "idle") return L.F("{0} 闲着 · {1}", name, d);
        return L.F("{0} 没在跑 · {1}", name, d);
    }

    static string Dur(int secs)
    {
        if (secs < 60) return L.F("{0} 秒", Math.Max(0, secs));
        if (secs < 3600) return L.F("{0} 分钟", secs / 60);
        if (secs < 86400) return L.F("{0} 小时", secs / 3600);
        return L.F("{0} 天", secs / 86400);
    }

    public enum Ask { Unknown, Approve, Question, Plan }

    // 一行多宽（一个汉字算 2）；开头最多两行，末尾那句也是
    const int LineUnits = 56, HeadUnits = 108, TailUnits = 100;

    // 各家「问你」的工具（跟 agenthost-hook.cjs 的 ASK_TOOLS 对齐，ExitPlanMode 单算）
    static readonly HashSet<string> QuestionTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "AskUserQuestion", "request_user_input", "ask_user_question", "ask_user", "ask_question"
    };

    // 先看工具名再看事件：Claude 批计划、出选择题也走 PermissionRequest，只看事件会都说成「等你批准」
    public static Ask AskOf(HookNote n)
    {
        if (n == null) return Ask.Unknown;
        string tool = n.Tool ?? "", ev = n.Event ?? "";
        if (string.Equals(tool, "ExitPlanMode", StringComparison.OrdinalIgnoreCase)) return Ask.Plan;
        if (QuestionTools.Contains(tool) || ev == "question.asked") return Ask.Question;
        if (ev == "PermissionRequest" || ev.StartsWith("permission.", StringComparison.Ordinal)) return Ask.Approve;
        // Claude 弹出批准框后还会来一条 Notification，它自己不带工具名，靠钩子把前一条的沿用过来。
        // 没有工具名（旧钩子写的记录）就分不出是批准、选择题还是批计划：不猜，照旧只说在等你。
        if (ev == "Notification" && tool.Length > 0) return Ask.Approve;
        return Ask.Unknown;
    }

    // 「在问你」那一行
    public static string AskLine(string name, HookNote n)
    {
        switch (AskOf(n))
        {
            case Ask.Plan: return L.F("{0} 计划写好了，等你看", name);
            case Ask.Question: return L.F("{0} 问了你一个问题", name);
            case Ask.Approve:
                string tool = ToolName(n.Tool);
                return tool == null ? L.F("{0} 等你批准", name) : L.F("{0} 要用 {1}，等你批准", name, tool);
            default: return L.F("{0} 在问你，等你选", name);
        }
    }

    // 索引签上的一小句：只说它停下来要你做什么，别的都留给悬停提示。
    // 在问你 → 是批准一步操作（带工具名）、问了个问题，还是计划等你看；做完了你还没看 → 一句「做完了」；其余不写。
    // 写在图标下面，签只有 42 宽：每行两三个汉字 / 五六个字母。等你批准某个工具的分两行（\n），第二行是工具名，
    // 写不下的由画的那边截成省略号。
    public static string Short(string state, bool unseen, HookNote n)
    {
        if (state == "asking")
            switch (AskOf(n))
            {
                case Ask.Plan: return L.T("看计划");
                case Ask.Question: return L.T("问你");
                case Ask.Approve:
                    string tool = ToolName(n.Tool);
                    return tool == null ? L.T("等批准") : L.T("批准") + "\n" + tool;
                default: return L.T("等你选");
            }
        return state == "done" && unseen ? L.T("做完了") : "";
    }

    // 外接工具的名字是 mcp__<服务>__<工具>，只留最后那段
    static string ToolName(string tool)
    {
        if (string.IsNullOrWhiteSpace(tool)) return null;
        string t = tool.Trim();
        int k = t.LastIndexOf("__", StringComparison.Ordinal);
        if (t.StartsWith("mcp__", StringComparison.Ordinal) && k > 4 && k + 2 < t.Length) t = t.Substring(k + 2);
        return Cut(t, 40);
    }

    // 状态那一行下面补的几行；没什么可补就是空的。
    // name：第一行里的名字 —— 终端会话的名字里已经带着项目（「Codex CLI · 项目」）就不再说一遍文件夹
    public static List<string> Lines(string state, HookNote n, string name = null)
    {
        var lines = new List<string>();
        if (n == null || (state != "asking" && state != "done")) return lines;
        // 心跳里调的：提示拼不出来就少说两句，不能让一段怪文字把宿主带倒
        try
        {
            if (state == "done")
            {
                if (!n.Excerpted) { n.Excerpted = true; Excerpt(n.Reply, out n.Head, out n.Tail); }
                if (n.Head != null) lines.AddRange(Wrap(n.Head, LineUnits));
                if (n.Tail != null) lines.AddRange(Wrap(L.F("最后问你：{0}", n.Tail), LineUnits));
            }
            string folder = Folder(n.Cwd);
            if (folder != null && (name == null || !name.EndsWith(" · " + folder, StringComparison.OrdinalIgnoreCase)))
                lines.Add(L.F("文件夹：{0}", folder));
        }
        catch (Exception ex) { Log.W("tip note: " + ex.Message); }
        return lines;
    }

    public static string Join(string first, List<string> more)
    {
        return more.Count == 0 ? first : first + "\r\n" + string.Join("\r\n", more);
    }

    // ---- 回复的摘录 ----

    static readonly Regex Rule = new Regex(@"^([-*_=]\s*){3,}$", RegexOptions.CultureInvariant);
    static readonly Regex Lead = new Regex(@"^(#{1,6}\s+|>\s*|[-*+]\s+|\d{1,2}[.)]\s+|\d{1,2}、)+", RegexOptions.CultureInvariant);
    static readonly Regex Link = new Regex(@"!?\[([^\]]*)\]\([^)]*\)", RegexOptions.CultureInvariant);
    static readonly Regex Spaces = new Regex(@"\s+", RegexOptions.CultureInvariant);

    // 去掉代码块、表格、分隔线和标题 / 列表 / 加粗这些记号，剩下一行行的字
    static List<string> CleanLines(string text)
    {
        var ls = new List<string>();
        if (string.IsNullOrEmpty(text)) return ls;
        bool fence = false;
        foreach (string raw in text.Replace("\r", "").Split('\n'))
        {
            string t = raw.Trim();
            if (t.StartsWith("```", StringComparison.Ordinal) || t.StartsWith("~~~", StringComparison.Ordinal)) { fence = !fence; continue; }
            if (fence || t.Length == 0 || t[0] == '|' || Rule.IsMatch(t)) continue;
            t = Lead.Replace(t, "");
            t = Link.Replace(t, "$1");
            t = t.Replace("**", "").Replace("`", "");
            t = Spaces.Replace(t, " ").Trim();
            if (t.Length > 0) ls.Add(t);
        }
        return ls;
    }

    // head：开头，顺着往下接到两行为止；tail：末尾那句是问句才有（整段都在 head 里了就不重复）
    public static void Excerpt(string reply, out string head, out string tail)
    {
        head = tail = null;
        if (HookNote.Machine(reply)) return;   // 一整段 JSON 不是说给人听的，不念
        List<string> ls = CleanLines(reply);
        if (ls.Count == 0) return;
        var sb = new StringBuilder();
        int i = 0;
        for (; i < ls.Count && Units(sb.ToString()) <= HeadUnits; i++)
        {
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(ls[i]);
        }
        string flow = sb.ToString();
        head = Cut(flow, HeadUnits);
        if (i >= ls.Count && Units(flow) <= HeadUnits) return;
        // 最后两行里靠后的那个问句：问完再补一句「改动都在分支上」的也算
        for (int k = ls.Count - 1; k >= Math.Max(0, ls.Count - 2); k--)
        {
            string last = LastSentence(ls[k]);
            if (IsQuestion(last)) { tail = Cut(last, TailUnits); return; }
        }
    }

    const string Closers = "）)」』】]\"”’'*~ ";

    static int SentenceEnd(string s)
    {
        int end = s.Length - 1;
        while (end >= 0 && Closers.IndexOf(s[end]) >= 0) end--;
        return end;
    }

    static bool IsQuestion(string s)
    {
        int end = SentenceEnd(s);
        return end >= 0 && (s[end] == '?' || s[end] == '？');
    }

    // 一行里的最后一句
    static string LastSentence(string line)
    {
        int k = SentenceEnd(line) - 1;
        for (; k >= 0; k--)
        {
            char c = line[k];
            if (c == '。' || c == '！' || c == '？' || c == '；' || c == '!' || c == '?' || c == ';') break;
            // 英文句号后面跟空格才算一句完了（别在 3.5、a.b 上断）
            if (c == '.' && k + 1 < line.Length && line[k + 1] == ' ') break;
        }
        return line.Substring(Math.Max(0, k + 1)).Trim();
    }

    // ---- 目录 ----

    // 只说最后一段目录名。用户目录下以 . 开头的（~/.gemini/config、~/.codex/memories）是各家自己的配置目录，
    // 不是项目，不说 —— Antigravity 的载荷不带目录，钩子记下的就是它自己的配置目录。
    public static string Folder(string cwd)
    {
        if (string.IsNullOrWhiteSpace(cwd)) return null;
        string p = cwd.Trim().Replace('/', '\\').TrimEnd('\\');
        if (p.Length == 0) return null;
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('\\');
        if (home.Length > 0 && p.StartsWith(home + "\\.", StringComparison.OrdinalIgnoreCase)) return null;
        int k = p.LastIndexOf('\\');
        string name = k < 0 ? p : p.Substring(k + 1);
        return name.Length == 0 ? null : Cut(name, 40);
    }

    // ---- 按显示宽度截断、折行 ----

    static bool Wide(char c)
    {
        return c >= 0x1100 && (c <= 0x115F || (c >= 0x2E80 && c <= 0xA4CF) || (c >= 0xAC00 && c <= 0xD7A3)
            || (c >= 0xF900 && c <= 0xFAFF) || (c >= 0xFE30 && c <= 0xFE4F) || (c >= 0xFF00 && c <= 0xFF60) || (c >= 0xFFE0 && c <= 0xFFE6));
    }

    static int Units(string s)
    {
        int w = 0;
        for (int i = 0; i < s.Length; i++) w += Wide(s[i]) ? 2 : 1;
        return w;
    }

    // 放得下几个字符（至少 1 个；不把一个表情符号从中间劈开）
    static int Fit(string s, int units)
    {
        int w = 0;
        for (int i = 0; i < s.Length; i++)
        {
            w += Wide(s[i]) ? 2 : 1;
            if (w > units) return Math.Max(1, char.IsHighSurrogate(s[i > 0 ? i - 1 : 0]) ? i - 1 : i);
        }
        return s.Length;
    }

    public static string Cut(string s, int units)
    {
        int n = Fit(s, units);
        return n >= s.Length ? s : s.Substring(0, n).TrimEnd() + "…";
    }

    // 行首不放这些
    const string NoLead = "，。！？；：、）」』】”’,.!?;:)]…";

    public static List<string> Wrap(string s, int units)
    {
        var lines = new List<string>();
        s = (s ?? "").Trim();
        while (s.Length > 0)
        {
            int n = Fit(s, units);
            if (n >= s.Length) { lines.Add(s); break; }
            if (NoLead.IndexOf(s[n]) >= 0) n++;
            else if (!Wide(s[n]) && s[n] != ' ' && !Wide(s[n - 1]) && s[n - 1] != ' ')
            {
                // 英文别从词中间断：这一行后半截有空格就退到那儿
                int sp = s.LastIndexOf(' ', n - 1, n - n / 2);
                if (sp > 0) n = sp;
            }
            lines.Add(s.Substring(0, n).TrimEnd());
            s = s.Substring(n).TrimStart();
        }
        return lines;
    }
}
