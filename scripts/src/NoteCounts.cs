using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

// 留言：各家 agent 互相留的话。存取和「交给模型看」都在钩子那边（scripts\hooks\agenthost-notes.cjs，格式见它开头）；
// 宿主只做两件事：
//   1. 数每家还有几条没看（new + open 两个目录里的 .json），点在那一格的图标上、写进悬停提示 ——
//      有些家钩子塞不进它的上下文（没有钩子的 Grok Bot、自己挑进来的窗口……），只能靠你看到了跟它说一声；
//   2. 把「现在认得哪几家」写成 notes\peers.json，留言的脚本拿它核对收件人（给不认得的人留言要报错，
//      不能报了成功却没人收）。
// 心跳里每 2 秒来问一次：目录没动过（看两个目录的修改时间）就不读，只是十来次查文件属性。
// 修改时间刚变过的那几秒照读不误：时间戳的精度有限，紧挨着的两次改动可能落在同一个时刻上。
// 只认名字像编号的 .json（跟脚本里的 ID 一样），别的文件不数。
internal static class NoteCounts
{
    sealed class Box
    {
        public int Count;      // 没标已读的：new + open
        public int Fresh;      // 其中还没送到过任何会话的：new
        public string[] From = new string[0];
        public long StampNew, StampOpen;
        public long ReadAt;   // 上一次真读是什么时候（UTC ticks）
    }

    static readonly Dictionary<string, Box> _boxes = new Dictionary<string, Box>(StringComparer.OrdinalIgnoreCase);
    static string _peersLast;

    // 每次现算，不记着：测试里会把 LOCALAPPDATA 指到别处
    internal static string Root
    {
        get
        {
            string b = Environment.GetEnvironmentVariable("LOCALAPPDATA");
            if (string.IsNullOrEmpty(b)) b = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(b, "AgentHost", "notes");
        }
    }

    // 跟脚本里的 NAME 一样：小写字母数字开头，后面可以有横线、下划线，最多 32 个
    static bool IsName(string s)
    {
        if (string.IsNullOrEmpty(s) || s.Length > 32) return false;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            bool alnum = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9');
            if (!(alnum || (i > 0 && (c == '-' || c == '_')))) return false;
        }
        return true;
    }

    // 文件名是不是「编号.json」：编号是 6～32 个小写字母数字（跟脚本里的 ID 一样）
    static bool IsId(string fileName)
    {
        if (fileName == null || !fileName.EndsWith(".json", StringComparison.Ordinal)) return false;
        int n = fileName.Length - 5;
        if (n < 6 || n > 32) return false;
        for (int i = 0; i < n; i++)
        {
            char c = fileName[i];
            if (!((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9'))) return false;
        }
        return true;
    }

    static long Stamp(string dir)
    {
        try { return Directory.Exists(dir) ? Directory.GetLastWriteTimeUtc(dir).Ticks : 0; } catch { return 0; }
    }

    // 重新数一遍。有哪一家的个数（或者是谁留的）变了返回 true
    public static bool Refresh()
    {
        bool changed = false;
        string[] dirs;
        try
        {
            string root = Root;
            dirs = Directory.Exists(root) ? Directory.GetDirectories(root) : new string[0];
        }
        catch { return false; }
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string d in dirs)
        {
            string kind = Path.GetFileName(d);
            if (!IsName(kind)) continue;
            seen.Add(kind);
            long sn = Stamp(Path.Combine(d, "new")), so = Stamp(Path.Combine(d, "open"));
            Box old;
            _boxes.TryGetValue(kind, out old);
            long clock = DateTime.UtcNow.Ticks;
            // 没动过就不读 —— 除非上次读的时候目录才刚改过（3 秒以内）：那一下之后紧跟着的改动可能没让时间戳变
            bool settled = old != null && old.ReadAt - Math.Max(sn, so) > 3 * TimeSpan.TicksPerSecond;
            if (old != null && old.StampNew == sn && old.StampOpen == so && settled) continue;
            Box now = Read(d);
            now.StampNew = sn; now.StampOpen = so; now.ReadAt = clock;
            if (old == null ? now.Count > 0 : old.Count != now.Count || old.Fresh != now.Fresh || string.Join("|", old.From) != string.Join("|", now.From)) changed = true;
            _boxes[kind] = now;
        }
        var gone = new List<string>();
        foreach (var kv in _boxes) if (!seen.Contains(kv.Key)) gone.Add(kv.Key);
        foreach (string k in gone) { if (_boxes[k].Count > 0) changed = true; _boxes.Remove(k); }
        return changed;
    }

    static Box Read(string kindDir)
    {
        var box = new Box();
        var from = new List<string>();
        var files = new List<string>();
        foreach (string st in new[] { "new", "open" })
        {
            try
            {
                string sub = Path.Combine(kindDir, st);
                if (!Directory.Exists(sub)) continue;
                foreach (string f in Directory.GetFiles(sub, "*.json"))
                    if (IsId(Path.GetFileName(f))) files.Add(f);
            }
            catch { }
        }
        box.Count = files.Count;
        string newDir = Path.Combine(kindDir, "new") + Path.DirectorySeparatorChar;
        foreach (string f in files) if (f.StartsWith(newDir, StringComparison.OrdinalIgnoreCase)) box.Fresh++;
        // 是谁留的：按编号（文件名，按时间排的）从早到晚，各家只说一次。读不出来的那条不知道是谁，跳过
        files.Sort((a, b) => string.CompareOrdinal(Path.GetFileName(a), Path.GetFileName(b)));
        int read = 0;
        foreach (string f in files)
        {
            if (++read > 60) break;   // 积了一大堆的时候不挨个读，名字说前面那些就够了
            try
            {
                using (JsonDocument d = JsonDocument.Parse(File.ReadAllText(f)))
                {
                    JsonElement v;
                    if (!d.RootElement.TryGetProperty("from", out v) || v.ValueKind != JsonValueKind.String) continue;
                    string who = (v.GetString() ?? "").Trim().ToLowerInvariant();
                    if (IsName(who) && !from.Contains(who)) from.Add(who);
                }
            }
            catch { }   // 正在被挪走、写坏了：不知道是谁留的，个数照算
        }
        box.From = from.ToArray();
        return box;
    }

    // 这一家还有几条没看
    public static int Unread(string kind)
    {
        Box b;
        return kind != null && _boxes.TryGetValue(kind, out b) ? b.Count : 0;
    }

    // 这一家还没送到过任何会话的留言有几条（图标上点的就是这个数）。
    // 送到过、只是它还没标已读的不点：那是它自己的事了，每次开工钩子都会再提醒它；点在图标上只会让人看着烦
    //（用户 2026-10-13：迁过来的旧留言让四格都顶着数字，「太多红点了看着烦心」）。
    public static int Undelivered(string kind)
    {
        Box b;
        return kind != null && _boxes.TryGetValue(kind, out b) ? b.Fresh : 0;
    }

    // 悬停提示里的那一行；没有留言返回 null
    public static string Line(string kind)
    {
        Box b;
        if (kind == null || !_boxes.TryGetValue(kind, out b) || b.Count == 0) return null;
        if (b.From.Length == 0) return L.F("{0} 条留言没看", b.Count);
        var names = new List<string>();
        foreach (string id in b.From)
        {
            if (names.Count == 3) { names.Add("…"); break; }
            names.Add(id);
        }
        return L.F("{0} 条留言没看：来自 {1}", b.Count, string.Join(L.Sep, names));
    }

    // 现在认得哪几家（id → 显示的名字）。内容没变不重写
    public static void WritePeers(IEnumerable<KeyValuePair<string, string>> peers)
    {
        try
        {
            var sb = new StringBuilder("{\"peers\":[");
            var had = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in peers)
            {
                string id = (p.Key ?? "").Trim().ToLowerInvariant();
                if (!IsName(id) || !had.Add(id)) continue;
                if (had.Count > 1) sb.Append(',');
                sb.Append("{\"id\":").Append(JsonSerializer.Serialize(id)).Append(",\"name\":").Append(JsonSerializer.Serialize(p.Value ?? id)).Append('}');
            }
            sb.Append("]}");
            string body = sb.ToString();
            if (body == _peersLast) return;
            string root = Root, file = Path.Combine(root, "peers.json");
            // 刚启动：盘上那份跟要写的一样就不动它
            if (_peersLast == null && File.Exists(file) && File.ReadAllText(file) == body) { _peersLast = body; return; }
            Directory.CreateDirectory(root);
            string tmp = file + ".tmp";
            File.WriteAllText(tmp, body, new UTF8Encoding(false));
            File.Move(tmp, file, true);
            _peersLast = body;
        }
        catch { }
    }
}
