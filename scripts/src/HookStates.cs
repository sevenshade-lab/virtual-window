using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

// 读钩子小程序（agenthost-hook.cjs）写的会话文件：%LOCALAPPDATA%\AgentHost\sessions\<kind>-<会话>.json。
// 这是虚拟窗口自己的状态来源；外部程序要数据也读这些文件。
//
// 桌面端那一格只有一盏灯，这一家却可以同时开着好几个会话（Claude 桌面端常这样）。灯听谁的：
//   - 有 slot 的是宿主自己起的 CLI 会话，不算；
//   - Claude 只认 entry=claude-desktop 的（你在终端里自己开的 Claude Code 不算桌面端），OpenCode 同理只认 opencode-desktop；
//   - 已退出（stopped）的跳过；
//   - 任何一个会话在问你（asking）就算在问你 —— 那是最急的；
//   - 否则只要还有一个在干活（working）就算在干活；
//   - 都不是，取最近更新的那个。
//   以前后两条是一条「取最近更新的那个」：一个会话长任务跑着，另一个一做完，灯就被它盖成「做完了」（黄），
//   几秒后干活的那个再来一条事件又盖回去 —— 来回跳，队列视图还跟着换人（用户 2026-10-12：
//   「很多次在执行长任务的时候灯就变黄了」；当天日志里 Claude 两个多小时这样跳了 5 回）。
// 「在干活」20 分钟没动静：多半是没收到结束事件（崩了、被杀了、你按了停止），当闲着，也就不再压着别的会话。
internal static class HookStates
{
    sealed class Rec
    {
        public string Kind, SessionId, State, Slot, Entry;
        public long At;
        public DateTime Mtime;
        public HookNote Note;
    }

    static readonly Dictionary<string, Rec> _recs = new Dictionary<string, Rec>(StringComparer.OrdinalIgnoreCase);
    const int StaleWorkingMin = 20;

    public static void Refresh()
    {
        string dir = HookSetup.SessionsDir;
        if (!Directory.Exists(dir)) return;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string[] files;
        try { files = Directory.GetFiles(dir, "*.json"); } catch { return; }
        foreach (string f in files)
        {
            seen.Add(f);
            try
            {
                DateTime m = File.GetLastWriteTimeUtc(f);
                Rec old;
                if (_recs.TryGetValue(f, out old) && old.Mtime == m) continue;
                using (JsonDocument d = JsonDocument.Parse(File.ReadAllText(f)))
                {
                    JsonElement r = d.RootElement;
                    _recs[f] = new Rec
                    {
                        Kind = Str(r, "kind"), SessionId = Str(r, "sessionId"), State = Str(r, "state"),
                        Slot = Str(r, "slot"), Entry = Str(r, "entry"),
                        At = r.TryGetProperty("at", out JsonElement at) && at.TryGetInt64(out long v) ? v : 0,
                        Mtime = m,
                        Note = new HookNote { Event = Str(r, "event"), Tool = Str(r, "tool"), Reply = Str(r, "reply"), Cwd = Str(r, "cwd"),
                                              Session = Str(r, "sessionId"), Review = Str(r, "review") }
                    };
                }
            }
            catch { }   // 正在被改写：下一拍再读
        }
        var gone = new List<string>();
        foreach (string k in _recs.Keys) if (!seen.Contains(k)) gone.Add(k);
        foreach (string k in gone) _recs.Remove(k);
    }

    static string Str(JsonElement e, string name)
    {
        return e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : "";
    }

    static int Age(Rec r) { return (int)Math.Max(0, (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - r.At) / 1000); }

    static string Effective(Rec r)
    {
        if (r.State == "working" && Age(r) > StaleWorkingMin * 60) return "idle";
        return r.State;
    }

    public static string Desktop(string kind, out int secs) { HookNote n; return Desktop(kind, out secs, out n); }

    // 桌面端这一家的状态；没有任何钩子数据返回 null（调用方可以退回 CPU 兜底）。
    // note：被选中的那个会话记下的事件 / 工具 / 回复 / 目录
    public static string Desktop(string kind, out int secs, out HookNote note)
    {
        secs = 0;
        note = null;
        Rec best = null, asking = null, working = null, said = null;
        foreach (Rec r in _recs.Values)
        {
            if (!string.Equals(r.Kind, kind, StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.IsNullOrEmpty(r.Slot)) continue;
            if (kind == "claude" && r.Entry != "claude-desktop") continue;
            if (kind == "opencode" && r.Entry != "opencode-desktop") continue;
            string st = Effective(r);
            if (st == "stopped") continue;
            if (st == "asking" && (asking == null || r.At > asking.At)) asking = r;
            if (st == "working" && (working == null || r.At > working.At)) working = r;
            if (best == null || r.At > best.At) best = r;
            if (Said(r, st) && (said == null || r.At > said.At)) said = r;
        }
        Rec pick = asking ?? working ?? best;
        if (pick == null) return null;
        secs = Age(pick);
        note = NoteFor(pick, said);
        return Effective(pick);
    }

    // 做完了、而且留下了一段说给人听的话
    static bool Said(Rec r, string st)
    {
        return st == "done" && !string.IsNullOrEmpty(r.Note.Reply) && !HookNote.Machine(r.Note.Reply);
    }

    // 提示里要说哪一条的话。Codex 每轮做完会另起一个小会话生成「下一句建议」，回的是一段 JSON，
    // 十来秒后做完、成了这一家最新的那条 —— 它不是你看得到的对话，要说的是它前面那条真回复
    // （2026-10-12 本机 114 条做完的 Codex 记录里 53 条是这种）。隔得太久的不算同一回事。
    static HookNote NoteFor(Rec pick, Rec said)
    {
        if (said != null && said != pick && pick.State == "done" && HookNote.Machine(pick.Note.Reply)
            && pick.At - said.At < 2 * 60 * 1000) return said.Note;
        return pick.Note;
    }

    // 这一家最近一次收到钩子事件的时刻（Unix 毫秒，桌面端 CLI 都算）；从没收到过是 0。设置页看钩子通没通用
    public static long LastAt(string kind)
    {
        long best = 0;
        foreach (Rec r in _recs.Values)
            if (string.Equals(r.Kind, kind, StringComparison.OrdinalIgnoreCase) && r.At > best) best = r.At;
        return best;
    }

    public static string Slot(string slot, out int secs) { HookNote n; return Slot(slot, out secs, out n); }

    // 宿主自己起的 CLI 会话（钩子带着 AGENTHOST_SLOT）
    public static string Slot(string slot, out int secs, out HookNote note)
    {
        secs = 0;
        note = null;
        Rec best = null, said = null;
        foreach (Rec r in _recs.Values)
        {
            if (!string.Equals(r.Slot, slot, StringComparison.OrdinalIgnoreCase)) continue;
            if (best == null || r.At > best.At) best = r;
            if (Said(r, Effective(r)) && (said == null || r.At > said.At)) said = r;
        }
        if (best == null) return null;
        secs = Age(best);
        note = NoteFor(best, said);
        return Effective(best);
    }
}

// 钩子记下的、状态之外的那几样：停在哪个事件 / 工具上、这一轮最后说的话、在哪个目录。
// 悬停提示用（TipNotes）：不切过去就知道它停下来要你做什么。
internal sealed class HookNote
{
    public string Event, Tool, Reply, Cwd;
    // 是哪个会话的记录；Codex 的请求批准归谁批（钩子查到了才有）。只进日志：查「灯怎么变的」用
    public string Session, Review;

    // 日志里「状态为什么变」那一小段：事件名、工具名、会话号的头 8 位。不带正文
    public static string Why(HookNote n)
    {
        if (n == null) return "";
        string s = " [" + (n.Event ?? "");
        if (!string.IsNullOrEmpty(n.Tool)) s += " " + n.Tool;
        if (!string.IsNullOrEmpty(n.Session)) s += " · " + (n.Session.Length > 8 ? n.Session.Substring(0, 8) : n.Session);
        if (!string.IsNullOrEmpty(n.Review)) s += " · 归 " + n.Review + " 批";
        return s + "]";
    }
    // 回复的摘录只算一次（TipNotes.Excerpt）：心跳每 2 秒拼一次提示，别每次都把两千字过一遍
    public bool Excerpted;
    public string Head, Tail;

    // 回复是一整段 JSON：给程序看的，不是说给人听的（Codex 的「下一句建议」回的是 {"suggestion":…}）
    public static bool Machine(string reply)
    {
        if (reply == null) return false;
        int a = 0, b = reply.Length - 1;
        while (a < b && char.IsWhiteSpace(reply[a])) a++;
        while (b > a && char.IsWhiteSpace(reply[b])) b--;
        return b > a && reply[a] == '{' && reply[b] == '}';
    }
}
