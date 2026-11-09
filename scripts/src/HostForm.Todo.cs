// 待办：各家对话里「还没做完的事」汇到一处（用户 2026-11-07：他不常归档对话，是因为标题能提醒他还有哪些事没做完）。
// 存取和兜底扫描全在 scripts\hooks\agenthost-todo.cjs（格式见它开头）；长对话的长度在 agenthost-hygiene.cjs。
// 宿主只做三件事：
//   1. 工具条上一个「待办」按钮（不放红点、不放数字：用户嫌过图标上的未读数），点开一张面板列出没做完的事，
//      每件可以「复制接着做」（拼成一句话放进剪贴板，开新对话直接粘）、「做完了」；
//      工具条底下还常驻一行待办条带（TodoStrip，本文件末尾；2026-11-07 用户选的：要一眼看到，不要专门去打开）；
//   2. 每 30 分钟在后台跑一次 `agenthost-todo.cjs scan`（看各家做完闲着的对话最后一段话、推断有没有剩下的事；
//      顺带替没有钩子的 DSH 量一下对话多长）；
//   3. DSH 的对话过了长度线：它没有钩子、送不进话，就在状态那一行提一句（hygiene\nudge.json）。
// 两个脚本跟额度脚本一样由宿主自己放到 bin（嵌在 exe 里，换了 exe 自己跟上，不归「重新安装钩子」管）。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;

internal static class TodoList
{
    internal sealed class Item
    {
        public string Id, Kind, Session, Title, Cwd, Text, Source, State;
        public string Project;   // 长期项目的名字（没有就是一次性待办）
        public string Cont, GoKind;   // DSH 后台任务登出来的：脚本算好的「接着做」那句话、点了切去哪家（同一项目里能打开的那个对话）
        public long Created, Updated;
        public bool LongTerm { get { return !string.IsNullOrEmpty(Project); } }
        // 四象限（用户 2026-11-09）：重要是脚本合好的结果（用户手动 > 兜底表 > 模型，见脚本「四象限」那段），
        // 紧急在这里按时间点现算（时间点 3 天以内、或过了不到 7 天算急；用户手动定过的 Urg 压过算的）
        public bool Important;
        public string ImpBy, ImpWhy;
        public long Due;          // 字里最早的时间点（毫秒），0 = 没有
        public int Urg = -1;      // 用户手动定的急不急：-1 没定 / 0 不急 / 1 急
        public bool Urgent
        {
            get
            {
                if (Urg >= 0) return Urg == 1;
                if (Due <= 0) return false;
                long left = Due - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                return left <= 3 * 86400000L && left >= -7 * 86400000L;
            }
        }
        public int Quad { get { return Important && Urgent ? 1 : Urgent ? 2 : Important ? 3 : 4; } }
        // 四象限之上（用户 11-09：「我提出的这四个象限也只是最基本的」，草案「照这个做」）：卡在谁、状态由脚本合好
        // （用户手动 > 模型，状态再过一道只抬不降的关键词），分组、放了多久在这里现算（跟脚本 groupOf / staleNote 一样）
        public string Wait = "", Who = "", Phase = "";   // decide / act / agent / other / external；active / shelved / timing
        public bool Shelved { get { return Phase == "shelved"; } }
        // 1 等你的 / 2 agent 在做 / 3 等别人或外部（含等时机）/ 4 长期搁置 / 5 可能做完了；没判过卡在谁的当「等你的」
        public int Group
        {
            get
            {
                if (State == "maybe") return 5;
                if (Shelved) return 4;
                if (Wait == "agent") return 2;
                if (Wait == "other" || Wait == "external" || Phase == "timing") return Urgent && Phase != "timing" ? 1 : 3;
                return 1;
            }
        }
        public int IdleDays { get { long t = Updated > 0 ? Updated : Created; return t <= 0 ? 0 : (int)Math.Max(0, (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - t) / 86400000L); } }
        // 放了多久：重要的 7 天没动提一句；不急不重要的 14 天没动问要不要勾掉；搁置的不提（每周另提一次）
        public string StaleNote
        {
            get
            {
                if (State != "open" || Shelved) return "";
                int n = IdleDays;
                if (Important && n >= 7) return L.F("{0} 天没动了", n);
                if (Quad == 4 && n >= 14) return L.F("{0} 天没动，要不要勾掉", n);
                return "";
            }
        }
        // 卡片上那个小标：等谁（等别人 / 等外部写是谁、是什么）
        public string WaitLabel
        {
            get
            {
                if (Wait == "decide") return L.T("等你拍板");
                if (Wait == "act") return L.T("等你动手");
                if (Wait == "agent") return L.T("agent 能接着做");
                if (Wait == "other" || Wait == "external") return string.IsNullOrEmpty(Who) ? (Wait == "other" ? L.T("等别人") : L.T("等外部")) : L.F("等{0}", Who);
                return Phase == "timing" ? L.T("等时机") : "";
            }
        }
    }

    public static string GroupName(int g)
    {
        return g == 1 ? L.T("等你的") : g == 2 ? L.T("agent 在做") : g == 3 ? L.T("等别人或外部") : g == 4 ? L.T("长期搁置") : L.T("可能做完了");
    }

    // 条带上放的：只放「等你的」和急的，搁置的、可能做完了的不放，最多 5 个（草案：「条带只放『等你』+ 急的，最多 5 个，其余在面板里」）
    public const int StripMax = 5;
    public static List<Item> ForStrip()
    {
        var l = Items.FindAll(x => x.State == "open" && !x.Shelved && (x.Group == 1 || x.Urgent));
        l.Sort(delegate(Item a, Item b)
        {
            int c = a.Quad.CompareTo(b.Quad);
            if (c != 0) return c;
            c = (a.Due > 0 ? a.Due : long.MaxValue).CompareTo(b.Due > 0 ? b.Due : long.MaxValue);
            return c != 0 ? c : b.Updated.CompareTo(a.Updated);
        });
        if (l.Count > StripMax) l.RemoveRange(StripMax, l.Count - StripMax);
        return l;
    }

    // 四个档的名字（用户 11-09 的说法：「又着急又重要，着急，重要，不着急不重要」）
    public static string QuadName(int q)
    {
        return q == 1 ? L.T("急且重要") : q == 2 ? L.T("急") : q == 3 ? L.T("重要") : L.T("不急不重要");
    }

    // 「11-08 17:30」「12-06」「11-08 17:30（已过 18 小时）」
    public static string DueLabel(Item x)
    {
        if (x.Due <= 0) return "";
        DateTime t = DateTimeOffset.FromUnixTimeMilliseconds(x.Due).UtcDateTime.AddHours(8);
        string s = t.Month + "-" + t.Day.ToString("00") + (t.Hour == 23 && t.Minute == 59 ? "" : " " + t.ToString("HH:mm"));
        long over = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - x.Due;
        if (over <= 0) return s;
        return s + L.F("（已过 {0}）", over < 86400000L ? L.F("{0} 小时", Math.Max(1, (int)Math.Round(over / 3600000.0))) : L.F("{0} 天", (int)Math.Round(over / 86400000.0)));
    }

    // 归档候选（脚本 archive scan 攒的；只列不归，用户点了才归）
    internal sealed class ArchiveCand { public string Key, Kind, Id, Title, Cwd, Cls, Why; public long Last; }
    public static List<ArchiveCand> Archive = new List<ArchiveCand>();

    static string BaseDir { get { return Path.Combine(Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentHost"); } }
    public static string ScriptPath { get { return Path.Combine(BaseDir, "bin", "agenthost-todo.cjs"); } }
    static string HygienePath { get { return Path.Combine(BaseDir, "bin", "agenthost-hygiene.cjs"); } }
    public static string DataPath { get { return Path.Combine(BaseDir, "todo.json"); } }
    static string NudgePath { get { return Path.Combine(BaseDir, "hygiene", "nudge.json"); } }

    public static List<Item> Items = new List<Item>();
    public static bool Scanning;
    public static long LastScan;
    static DateTime _mtime;

    // 读 todo.json（没变就不读）。只留没做完的：open 在前、maybe（可能做完了）在后，各自新的在前
    public static bool Load()
    {
        try
        {
            if (!File.Exists(DataPath)) { bool had = Items.Count > 0; Items = new List<Item>(); return had; }
            DateTime m = File.GetLastWriteTimeUtc(DataPath);
            if (m == _mtime) return false;
            var list = new List<Item>();
            using (var fs = new FileStream(DataPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (JsonDocument d = JsonDocument.Parse(fs))
            {
                JsonElement arr;
                if (d.RootElement.TryGetProperty("items", out arr) && arr.ValueKind == JsonValueKind.Array)
                    foreach (JsonElement x in arr.EnumerateArray())
                    {
                        string st = Str(x, "state");
                        if (st != "open" && st != "maybe") continue;
                        list.Add(new Item
                        {
                            Id = Str(x, "id"), Kind = Str(x, "kind"), Session = Str(x, "session"), Title = Str(x, "title"),
                            Cwd = Str(x, "cwd"), Text = Str(x, "text"), Source = Str(x, "source"), State = st,
                            Project = Str(x, "project"), Cont = Str(x, "cont"), GoKind = Str(x, "goKind"),
                            Created = Num(x, "created"), Updated = Num(x, "updated"),
                            Important = Bool(x, "important") == 1, ImpBy = Str(x, "impBy"), ImpWhy = Str(x, "impWhy"),
                            Due = Num(x, "due"), Urg = Bool(x, "urg"),
                            Wait = Str(x, "wait"), Who = Str(x, "who"), Phase = Str(x, "phase"),
                        });
                    }
                var cands = new List<ArchiveCand>();
                JsonElement ar, ca;
                if (d.RootElement.TryGetProperty("archive", out ar) && ar.ValueKind == JsonValueKind.Object
                    && ar.TryGetProperty("cand", out ca) && ca.ValueKind == JsonValueKind.Array)
                    foreach (JsonElement x in ca.EnumerateArray())
                        cands.Add(new ArchiveCand { Key = Str(x, "key"), Kind = Str(x, "kind"), Id = Str(x, "id"), Title = Str(x, "title"), Cwd = Str(x, "cwd"),
                            Cls = Str(x, "cls"), Why = Str(x, "why"), Last = Num(x, "last") });
                Archive = cands;
                JsonElement ls;
                if (d.RootElement.TryGetProperty("lastScan", out ls) && ls.TryGetInt64(out long v)) LastScan = v;
            }
            Sort(list);
            Items = list;
            _mtime = m;
            return true;
        }
        catch { return false; }   // 正在被改写：下次再读
    }

    // 先按组（等你的 → agent 在做 → 等别人或外部 → 长期搁置 → 可能做完了），组里按四象限（急且重要 → 急 → 重要 → 不急不重要），
    // 同一档里有时间点的在前（早的在前），再新的在前；最后同一组里同一个项目的挨在一起（项目按它最靠前那件排），面板好折叠。
    // 急不急会随时间变（12-06 到期的 12-03 起才算急），心跳里重读时重排
    public static void Sort(List<Item> list)
    {
        list.Sort(delegate(Item a, Item b)
        {
            int c = a.Group.CompareTo(b.Group);
            if (c != 0) return c;
            c = a.Quad.CompareTo(b.Quad);
            if (c != 0) return c;
            c = (a.Due > 0 ? a.Due : long.MaxValue).CompareTo(b.Due > 0 ? b.Due : long.MaxValue);
            if (c != 0) return c;
            return b.Updated.CompareTo(a.Updated);
        });
        var outp = new List<Item>(list.Count);
        var taken = new HashSet<Item>();
        foreach (Item x in list)
        {
            if (taken.Contains(x)) continue;
            if (string.IsNullOrEmpty(x.Project)) { taken.Add(x); outp.Add(x); continue; }
            foreach (Item y in list)
                if (!taken.Contains(y) && y.Project == x.Project && y.Group == x.Group) { taken.Add(y); outp.Add(y); }
        }
        list.Clear();
        list.AddRange(outp);
    }

    static int Bool(JsonElement o, string k)
    {
        JsonElement v;
        if (!o.TryGetProperty(k, out v)) return -1;
        return v.ValueKind == JsonValueKind.True ? 1 : v.ValueKind == JsonValueKind.False ? 0 : -1;
    }

    static string Str(JsonElement o, string k)
    {
        JsonElement v;
        return o.TryGetProperty(k, out v) && v.ValueKind == JsonValueKind.String ? v.GetString() : "";
    }

    static long Num(JsonElement o, string k)
    {
        JsonElement v;
        return o.TryGetProperty(k, out v) && v.TryGetInt64(out long n) ? n : 0;
    }

    // exe 里嵌的跟 bin 里的不一样（或者还没有）就放出去
    static void Extract(string res, string dest)
    {
        using (Stream s = typeof(TodoList).Assembly.GetManifestResourceStream(res))
        {
            if (s == null) return;
            var ms = new MemoryStream();
            s.CopyTo(ms);
            byte[] want = ms.ToArray();
            try { if (File.Exists(dest) && ((ReadOnlySpan<byte>)File.ReadAllBytes(dest)).SequenceEqual(want)) return; } catch { }
            Directory.CreateDirectory(Path.GetDirectoryName(dest));
            string tmp = dest + ".tmp";
            File.WriteAllBytes(tmp, want);
            File.Move(tmp, dest, true);
        }
    }

    public static void ExtractScripts()
    {
        try { Extract("agenthost-hygiene.cjs", HygienePath); Extract("agenthost-todo.cjs", ScriptPath); }
        catch (Exception ex) { Log.W("todo: 放脚本出错 " + ex.Message); }
    }

    // 跑一次脚本（后台）；跑完在界面线程上回调
    public static void Run(Control ui, string[] args, int timeoutMs, Action done)
    {
        RunOut(ui, args, timeoutMs, done == null ? (Action<string>)null : delegate(string o) { done(); });
    }

    // 同上，回调拿到脚本打出来的东西（归档要拿「交给 Claude 的那句话」）
    public static void RunOut(Control ui, string[] args, int timeoutMs, Action<string> done)
    {
        string node = HookSetup.Node();
        if (node == null) { Log.W("todo: 没找到 node.exe"); return; }
        bool scan = args.Length > 0 && args[0] == "scan";
        if (scan) { if (Scanning) return; Scanning = true; }
        Task.Run(delegate
        {
            string outp = "";
            try
            {
                ExtractScripts();
                var psi = new ProcessStartInfo(node)
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardError = true, RedirectStandardOutput = true,
                    StandardErrorEncoding = Encoding.UTF8, StandardOutputEncoding = Encoding.UTF8,
                };
                psi.ArgumentList.Add(ScriptPath);
                foreach (string a in args) psi.ArgumentList.Add(a);
                using (Process p = Process.Start(psi))
                {
                    Task<string> err = p.StandardError.ReadToEndAsync();
                    outp = p.StandardOutput.ReadToEnd();
                    if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } Log.W("todo: " + args[0] + " 没跑完"); }
                    else if (p.ExitCode != 0) Log.W("todo: " + args[0] + " 出错 " + err.Result.Trim());
                    else if (scan) Log.W("todo: scan " + outp.Trim().Replace("\r", "").Replace("\n", " "));
                }
            }
            catch (Exception ex) { Log.W("todo: " + ex.Message); }
            finally
            {
                if (scan) Scanning = false;
                try { if (!ui.IsDisposed) ui.BeginInvoke((MethodInvoker)delegate { _mtime = DateTime.MinValue; Load(); if (done != null) done(outp); }); } catch { }
            }
        });
    }

    // 「接着做」那句话（跟脚本的 continueText 一样）
    public static string ContinueText(Item x)
    {
        if (!string.IsNullOrEmpty(x.Cont)) return x.Cont;
        AgentKind k = Catalog.ById(x.Kind);
        string who = (k != null ? k.Name : x.Kind) + " 的对话「" + x.Title + "」";
        return (x.LongTerm ? "长期项目「" + x.Project + "」· " : "") + "接着做：" + who + (string.IsNullOrEmpty(x.Cwd) ? "" : "（文件夹 " + x.Cwd + "）")
            + (string.IsNullOrEmpty(x.Session) ? "" : "，会话 " + x.Session)
            + "。剩下的事：" + (x.Text ?? "").TrimEnd('。', '.', '！', '!', '；', ';', '，', ',', ' ', '\n', '\r') + "。先看那个对话最后的交代（交接单、提交、文件）再动手。";
    }

    // DSH 这类没有钩子的家：过了长度线、还没提过的，交给宿主在状态行提一句（取走就算提过）
    public static List<string> TakeNudges(out List<string[]> keys)
    {
        var lines = new List<string>();
        keys = new List<string[]>();
        try
        {
            if (!File.Exists(NudgePath)) return lines;
            using (JsonDocument d = JsonDocument.Parse(File.ReadAllText(NudgePath, Encoding.UTF8)))
            {
                JsonElement arr;
                if (!d.RootElement.TryGetProperty("items", out arr) || arr.ValueKind != JsonValueKind.Array) return lines;
                foreach (JsonElement x in arr.EnumerateArray())
                {
                    string kind = Str(x, "kind"), sid = Str(x, "sessionId"), title = Str(x, "title");
                    long n = Num(x, "tokens");
                    AgentKind k = Catalog.ById(kind);
                    lines.Add(L.F("{0} 的对话「{1}」上下文已约 {2} 万，该交接到新对话了", k != null ? k.Name : kind, title, Math.Round(n / 10000.0)));
                    keys.Add(new[] { kind, sid });
                }
            }
            File.Delete(NudgePath);
        }
        catch { }
        return lines;
    }

    public static void MarkNudged(Control ui, string kind, string sid)
    {
        string node = HookSetup.Node();
        if (node == null) return;
        Task.Run(delegate
        {
            try
            {
                var psi = new ProcessStartInfo(node) { UseShellExecute = false, CreateNoWindow = true };
                psi.ArgumentList.Add(HygienePath);
                psi.ArgumentList.Add("nudged");
                psi.ArgumentList.Add(kind);
                psi.ArgumentList.Add(sid);
                using (Process p = Process.Start(psi)) p.WaitForExit(10000);
            }
            catch { }
        });
    }

    // 条带 / 提示上写的标题：没标题的用文件夹名或家名
    public static string ShortTitle(Item x)
    {
        if (!string.IsNullOrEmpty(x.Title)) return x.Title;
        if (!string.IsNullOrEmpty(x.Cwd)) { try { return Path.GetFileName(x.Cwd.TrimEnd('\\', '/')); } catch { } }
        AgentKind k = Catalog.ById(x.Kind);
        return k != null ? k.Name : x.Kind;
    }

    // 「3 小时前」「昨天」
    public static string Ago(long ms)
    {
        if (ms <= 0) return "";
        double min = (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - ms) / 60000.0;
        if (min < 1) return L.T("刚刚");
        if (min < 60) return L.F("{0} 分钟前", (int)min);
        if (min < 24 * 60) return L.F("{0} 小时前", (int)(min / 60));
        if (min < 48 * 60) return L.T("昨天");
        return L.F("{0} 天前", (int)(min / 60 / 24));
    }
}

internal sealed partial class HostForm
{
    GlassButton _todo;
    TodoPanel _todoPanel;
    Timer _todoTick;

    void CreateTodoButton()
    {
        _todo = MkBtn("待办", 470, 8, 64, 32);
        _todo.Click += delegate { ShowTodo(); };
        Controls.Add(_todo);
        Tip(_todo, "各家对话里还没做完的事：对话归档了也不丢");
        TodoList.Load();
        // 工具条底下常驻的那一行（TodoStrip，本文件末尾）：排版在 LayoutToolbar 末尾，文件变了在心跳里跟
        _todoStrip = new TodoStrip(this) { Font = Font, Visible = false };
        Controls.Add(_todoStrip);
        // 第一次显示时 WinForms 会把它挪到子控件表的最前面（顶层）：一开始就放那儿，显示前后顺序一样（LookTests 比控件清单）
        Controls.SetChildIndex(_todoStrip, 0);
        // 钩子小程序和 OpenCode 插件 require 长对话脚本：开起来就放到 bin（不等第一次扫）
        Task.Run(delegate { TodoList.ExtractScripts(); });
        _todoTick = new Timer();
        _todoTick.Interval = 30 * 60 * 1000;
        _todoTick.Tick += delegate { ScanTodo(); };
        _todoTick.Start();
        // 开起来一分钟后扫一次：别跟启动各家客户端、读额度挤在一起
        var first = new Timer { Interval = 60000 };
        first.Tick += delegate { first.Stop(); first.Dispose(); ScanTodo(); };
        first.Start();
    }

    void ScanTodo()
    {
        if (!Prefs.Get("todo.scan", true)) return;
        TodoList.Run(this, new[] { "scan" }, 15 * 60 * 1000, delegate
        {
            if (_todoPanel != null && !_todoPanel.IsDisposed) _todoPanel.Reflow();
            SyncTodoStrip(true);
            List<string[]> keys;
            List<string> lines = TodoList.TakeNudges(out keys);
            if (lines.Count == 0) { RemindTodoAging(); return; }
            ToastAct(lines.Count == 1 ? lines[0] : L.F("{0} 个没有钩子的对话已经很长了，该交接到新对话", lines.Count), null, 15);
            foreach (string[] k in keys) TodoList.MarkNudged(this, k[0], k[1]);
        });
    }

    // 草案：搁置的「只在面板『长期项目』里，每周提一次」；重要的 7 天没动、不急不重要的 14 天没动「提一句」——
    // 状态那一行提，一次只提一样：搁置的每 7 天一次，放久了的每天最多一次（卡片上一直标着）
    void RemindTodoAging()
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), at;
        List<TodoList.Item> shelved = TodoList.Items.FindAll(i => i.Shelved);
        long.TryParse(Prefs.GetStr("todo.shelvedAt", "0"), out at);
        if (shelved.Count > 0 && now - at > 7 * 86400000L)
        {
            var names = new List<string>();
            foreach (TodoList.Item i in shelved) if (names.Count < 4) names.Add(string.IsNullOrEmpty(i.Project) ? TodoList.ShortTitle(i) : i.Project);
            ToastAct(L.F("长期搁置 {0} 件：{1}。想重新拾起时，在待办面板最底下点「复制接着做」", shelved.Count, string.Join("、", names)), null, 15);
            Prefs.SetStr("todo.shelvedAt", now.ToString());
            return;
        }
        List<TodoList.Item> aging = TodoList.Items.FindAll(i => i.StaleNote.Length > 0);
        long.TryParse(Prefs.GetStr("todo.staleAt", "0"), out at);
        if (aging.Count > 0 && now - at > 86400000L)
        {
            ToastAct(aging.Count == 1 ? L.F("「{0}」{1}", TodoList.ShortTitle(aging[0]), aging[0].StaleNote)
                : L.F("{0} 件待办放了很久没动，待办面板里标着", aging.Count), null, 15);
            Prefs.SetStr("todo.staleAt", now.ToString());
        }
    }

    void ShowTodo()
    {
        if (_todoPanel != null && !_todoPanel.IsDisposed) { _todoPanel.Close(); return; }
        _menuOpen = true;
        IntPtr giveBack = _lastFocused;
        TodoList.Load();
        var p = new TodoPanel(this);
        _todoPanel = p;
        p.FormClosed += delegate
        {
            _menuOpen = false;
            _todoPanel = null;
            GiveBackAfterPopup(giveBack);
        };
        p.Location = _todo.PointToScreen(new Point(0, _todo.Height + D(6)));
        p.Reflow();
        p.Show(this);
        p.Activate();
    }

    // 待办面板：跟额度面板同一种弹出面板。每件一张卡：图标、对话标题、（推断 / 可能做完了）、多久前；下面剩下的事；右边两个按钮
    internal sealed class TodoPanel : Form, IHostPopup
    {
        readonly HostForm _host;
        // Header：这张卡上面那一行写的档名（每一档第一张才有）；A：归档视图里的那个对话
        // Fold：项目折叠那一行（「〔网店运营〕还有 2 件 ›」/「收起 ‹」），这时 X、A 都是空的，FoldKey 是「组|项目」
        sealed class Card { public Rectangle R, TextR; public TodoList.Item X; public TodoList.ArchiveCand A; public Bitmap Icon; public string Header, Text, FoldKey; }
        sealed class Btn { public Rectangle R; public string What, Key; public TodoList.Item X; public TodoList.ArchiveCand A; }
        // 展开了的项目（「组|项目」）；草案：「面板按项目折叠」—— 同一组里同一个项目只露最靠前那件，其余收在「还有 N 件」里
        readonly HashSet<string> _unfold = new HashSet<string>();
        static string FoldKeyOf(TodoList.Item x) { return string.IsNullOrEmpty(x.Project) ? null : x.Group + "|" + x.Project; }
        bool _arch;             // 正在看「可归档」那一页
        bool _archBusy;         // 正在归（Codex 一个一个归，一百来个要几分钟）
        readonly List<Card> _cards = new List<Card>();
        readonly List<Btn> _btns = new List<Btn>();
        Btn _hot;
        bool _closing, _escDown;
        int _more, _footY;
        string _flash;          // 刚做了什么（「已复制」）：标题右边显示一会儿
        DateTime _flashUntil;

        int D(int v) { return _host.D(v); }
        int Pad { get { return D(16); } }
        int W { get { return D(640); } }

        public TodoPanel(HostForm host)
        {
            _host = host;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            KeyPreview = true;
            DoubleBuffered = true;
            BackColor = ShellStyles.Surface;
            Font = host.Font;
        }

        protected override CreateParams CreateParams
        {
            get { CreateParams cp = base.CreateParams; cp.ClassStyle |= 0x00020000; /* CS_DROPSHADOW */ return cp; }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            int round = 2;
            try { Native.DwmSetInt(Handle, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref round, 4); } catch { }
        }

        protected override void OnActivated(EventArgs e) { base.OnActivated(e); Native.SetFocus(Handle); }
        protected override void OnFormClosing(FormClosingEventArgs e) { _closing = true; base.OnFormClosing(e); }
        protected override void OnDeactivate(EventArgs e) { base.OnDeactivate(e); if (!_closing && !IsDisposed) Close(); }
        public void DismissForSlotClick() { if (!_closing && !IsDisposed) Close(); }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape) { _escDown = true; e.Handled = true; Close(); return; }
            base.OnKeyDown(e);
        }
        protected override void OnKeyUp(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape && !_escDown && !_closing) { e.Handled = true; Close(); return; }
            _escDown = false;
            base.OnKeyUp(e);
        }

        static StringFormat TextFormat()
        {
            var sf = new StringFormat(StringFormatFlags.LineLimit);
            sf.Trimming = StringTrimming.EllipsisCharacter;
            return sf;
        }

        public void Reflow()
        {
            _cards.Clear();
            _btns.Clear();
            _hot = null;
            Rectangle work = Screen.FromPoint(Location).WorkingArea;
            int maxBottom = work.Height - D(80);
            int y = D(48);
            // 两个视图共用一套卡：待办（复制接着做 / 做完了）、可归档（别归 / 归档）
            string b1 = _arch ? L.T("别归") : L.T("复制接着做"), b2 = _arch ? L.T("归档") : L.T("做完了");
            int cw = TextRenderer.MeasureText(b1, Font).Width + D(20), dw = TextRenderer.MeasureText(b2, Font).Width + D(20);
            // 字在左、两个按钮在右边竖着居中；字最多三行。量和画用同一套 GDI+ 排字（TextRenderer 量出来的行高比 DrawString 矮，
            // 按它留的框 LineLimit 会把整行藏掉 —— 第一版就是这样大半的字没画出来）
            int btnsW = cw + D(8) + dw;
            int textW = W - Pad * 2 - D(12) - D(30) - D(16) - btnsW - D(12);
            // 待办页先排出要画的行：卡片，和每个折起来 / 展开了的项目后面一行「还有 N 件 ›」/「收起 ‹」
            var rows = new List<object>();   // TodoList.Item / TodoList.ArchiveCand / string[] { 折叠键, 那一行的字 }
            if (_arch) foreach (TodoList.ArchiveCand a0 in TodoList.Archive) rows.Add(a0);
            else
            {
                List<TodoList.Item> all = TodoList.Items;
                for (int i = 0; i < all.Count; i++)
                {
                    TodoList.Item x0 = all[i];
                    string fk = FoldKeyOf(x0);
                    int n = 0, first = -1, lastIx = -1;
                    if (fk != null) for (int j = 0; j < all.Count; j++) if (FoldKeyOf(all[j]) == fk) { n++; if (first < 0) first = j; lastIx = j; }
                    bool folded = n > 1 && !_unfold.Contains(fk);
                    if (!folded || i == first) rows.Add(x0);
                    if (n > 1 && i == lastIx)
                        rows.Add(new[] { fk, folded ? L.F("〔{0}〕还有 {1} 件 ›", x0.Project, n - 1) : L.F("〔{0}〕收起 ‹", x0.Project) });
                }
            }
            int count = rows.Count;
            _more = 0;
            using (var bmp = new Bitmap(1, 1))
            using (Graphics g = Graphics.FromImage(bmp))
            using (var sf = TextFormat())
            {
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                float lineH = g.MeasureString("中", Font, 1000, sf).Height;
                string lastHead = null;
                for (int i = 0; i < count; i++)
                {
                    string[] fold = rows[i] as string[];
                    if (fold != null)
                    {
                        // 折叠那一行：紧贴在这个项目最后一张卡底下，不算新的一档
                        if (y + D(26) > maxBottom && _cards.Count > 0) { _more = count - i; break; }
                        var fr = new Rectangle(Pad, y - D(4), W - Pad * 2, D(26));
                        _cards.Add(new Card { R = fr, Text = fold[1], FoldKey = fold[0] });
                        int fw = TextRenderer.MeasureText(fold[1], Font).Width + D(16);
                        _btns.Add(new Btn { R = new Rectangle(fr.X + D(30), fr.Y, fw, fr.Height), What = "fold", Key = fold[0] });
                        y = fr.Bottom + D(8);
                        continue;
                    }
                    TodoList.Item x = rows[i] as TodoList.Item;
                    TodoList.ArchiveCand a = rows[i] as TodoList.ArchiveCand;
                    // 对话这一层五类（进行中 / 长期·搁置 / 例行 / 了结 / 一次性）里能进候选的只有了结、一次性，和例行的旧期
                    string text = x != null ? (x.Text ?? "")
                        : (a.Cls == "done" ? L.T("了结") : a.Cls == "routine" ? L.T("例行旧期") : L.T("一次性")) + (string.IsNullOrEmpty(a.Why) ? "" : " · " + a.Why) + (string.IsNullOrEmpty(a.Cwd) ? "" : "\n" + a.Cwd);
                    float full = g.MeasureString(text, Font, textW, sf).Height;
                    int th = (int)Math.Ceiling(Math.Min(lineH * 3, Math.Max(lineH, full))) + 1;
                    int body = Math.Max(th, D(30));
                    int h = D(10) + D(26) + D(4) + body + D(12);
                    // 每一组第一张卡上面空一行写组名（等你的 / agent 在做 / 等别人或外部 / 长期搁置 / 可能做完了）；四象限写在卡片的小标上
                    string head = x == null ? null : TodoList.GroupName(x.Group);
                    bool newHead = head != null && head != lastHead;
                    int lead = newHead ? D(22) : 0;
                    if (y + lead + h > maxBottom && _cards.Count > 0) { _more = count - i; break; }
                    y += lead;
                    lastHead = head ?? lastHead;
                    var r = new Rectangle(Pad, y, W - Pad * 2, h);
                    AgentKind k = Catalog.ById(x != null ? x.Kind : a.Kind);
                    var c = new Card { R = r, X = x, A = a, Text = text, Icon = k != null ? AgentIcons.Get(k, D(20)) : null, Header = newHead ? head : null };
                    int bodyTop = r.Y + D(10) + D(26) + D(4);
                    c.TextR = new Rectangle(r.X + D(12) + D(30), bodyTop + (body - th) / 2, textW, th);
                    _cards.Add(c);
                    int by = bodyTop + (body - D(30)) / 2;
                    _btns.Add(new Btn { R = new Rectangle(r.Right - D(12) - dw, by, dw, D(30)), What = _arch ? "archOne" : "done", X = x, A = a });
                    _btns.Add(new Btn { R = new Rectangle(r.Right - D(12) - dw - D(8) - cw, by, cw, D(30)), What = _arch ? "keep" : "copy", X = x, A = a });
                    y += h + D(8);
                }
            }
            int foot = y + D(6);
            // 底下一行：待办页「↻ 重新扫」+ 右边「可归档 N 个对话 ›」；归档页「‹ 回待办」+ 右边「全部归档」
            string left = _arch ? L.T("‹ 回待办") : L.T("↻ 重新扫");
            int aw = TextRenderer.MeasureText(left, Font).Width + D(20);
            _btns.Add(new Btn { R = new Rectangle(Pad - D(6), foot + D(8), aw, D(30)), What = _arch ? "back" : "scan" });
            string right = RightLabel();
            if (right != null)
            {
                int rw = TextRenderer.MeasureText(right, Font).Width + D(20);
                _btns.Add(new Btn { R = new Rectangle(W - Pad + D(6) - rw, foot + D(8), rw, D(30)), What = _arch ? "archAll" : "arch" });
            }
            _btns.Add(new Btn { R = new Rectangle(W - Pad - D(32) + D(6), D(8), D(32), D(32)), What = "close" });
            _footY = foot;
            ClientSize = new Size(W, foot + D(8) + D(30) + D(12));
            if (Bottom > work.Bottom) Top = Math.Max(work.Top, work.Bottom - Height);
            if (Right > work.Right) Left = Math.Max(work.Left, work.Right - Width);
            Invalidate();
        }

        // 底下一行右边那个：待办页有候选才出「可归档 N 个对话 ›」；归档页「全部归档（N）」（正在归时写「正在归…」）
        string RightLabel()
        {
            if (_arch) return TodoList.Archive.Count == 0 ? null : _archBusy ? L.T("正在归…") : L.F("全部归档（{0}）", TodoList.Archive.Count);
            return TodoList.Archive.Count > 0 ? L.F("可归档 {0} 个对话 ›", TodoList.Archive.Count) : null;
        }

        // 归档：Codex 的脚本直接归；Claude 的只有它桌面端的会话工具能归 —— 脚本拼好那句话，放进剪贴板、切到 Claude 的格子
        void DoArchive(string[] keys)
        {
            if (_archBusy) return;
            _archBusy = true;
            var args = new List<string> { "archive", "do" };
            args.AddRange(keys);
            Flash(L.T("正在归…"));
            Reflow();
            TodoList.RunOut(_host, args.ToArray(), 20 * 60 * 1000, delegate(string outp)
            {
                _archBusy = false;
                int done = 0, failed = 0;
                string claude = "";
                try
                {
                    using (JsonDocument j = JsonDocument.Parse(outp))
                    {
                        JsonElement v;
                        if (j.RootElement.TryGetProperty("done", out v) && v.ValueKind == JsonValueKind.Array) done = v.GetArrayLength();
                        if (j.RootElement.TryGetProperty("failed", out v) && v.ValueKind == JsonValueKind.Array) failed = v.GetArrayLength();
                        if (j.RootElement.TryGetProperty("claude", out v) && v.ValueKind == JsonValueKind.String) claude = v.GetString();
                    }
                }
                catch { }
                string said = done > 0 ? L.F("归了 {0} 个", done) : "";
                if (failed > 0) said += (said.Length > 0 ? "，" : "") + L.F("{0} 个没归成", failed);
                if (!string.IsNullOrEmpty(claude))
                {
                    try { Clipboard.SetText(claude); } catch { }
                    said += (said.Length > 0 ? "；" : "") + L.T("Claude 的已复制，粘给 Claude 就归");
                    _host.GoToKind("claude");
                }
                if (!IsDisposed) { Reflow(); Flash(said.Length > 0 ? said : L.T("没有要归的")); }
            });
        }

        Btn Hit(Point p) { foreach (Btn b in _btns) if (b.R.Contains(p)) return b; return null; }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            Btn h = Hit(e.Location);
            if (h != _hot) { _hot = h; Cursor = h != null ? Cursors.Hand : Cursors.Default; Invalidate(); }
            base.OnMouseMove(e);
        }

        void Flash(string s)
        {
            _flash = s;
            _flashUntil = DateTime.Now.AddSeconds(3);
            Invalidate();
            var t = new Timer { Interval = 3100 };
            t.Tick += delegate { t.Stop(); t.Dispose(); if (!IsDisposed) Invalidate(); };
            t.Start();
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            Btn b = Hit(e.Location);
            if (b == null) return;
            switch (b.What)
            {
                case "close": Close(); break;
                case "copy":
                    try { Clipboard.SetText(TodoList.ContinueText(b.X)); Flash(L.T("已复制，开新对话粘进去")); } catch { }
                    break;
                case "done":
                    // 先从面板上拿掉，脚本在后台改文件（它带锁，别跟正在登的那一家抢）
                    TodoList.Items.Remove(b.X);
                    Reflow();
                    if (TodoList.ForStrip().Count == 0) _host.Relayout(true); else _host.LayoutTodoStrip();
                    TodoList.Run(_host, new[] { "done", b.X.Id }, 20000, delegate { if (!IsDisposed) Reflow(); _host.SyncTodoStrip(true); });
                    break;
                case "scan":
                    _host.ScanTodo();
                    Invalidate();
                    break;
                case "fold":
                    if (!_unfold.Remove(b.Key)) _unfold.Add(b.Key);
                    Reflow();
                    break;
                case "arch": _arch = true; Reflow(); break;
                case "back": _arch = false; Reflow(); break;
                case "archOne": DoArchive(new[] { b.A.Key }); break;
                case "archAll": DoArchive(new[] { "all" }); break;
                case "keep":
                    // 别归：记下来，以后不再提（脚本在后台改文件）
                    TodoList.Archive.Remove(b.A);
                    Reflow();
                    TodoList.Run(_host, new[] { "archive", "keep", b.A.Key }, 20000, delegate { if (!IsDisposed) Reflow(); });
                    break;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            try { PaintBody(e); }
            catch (Exception ex) { PaintGuard.Failed(this, ex); }
        }

        void PaintBody(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            using (var pen = new Pen(ShellStyles.Border)) g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
            using (var title = Fonts.Medium(Font.Size * 1.08f))
            using (var bold = Fonts.Medium(Font.Size))
            using (var small = new Font(Font.FontFamily, Font.Size * 0.86f))
            {
                string head = _arch ? L.T("可归档") : L.T("待办");
                int headW = TextRenderer.MeasureText(head, title).Width + D(10);
                Theme.Text(g, head, title, Theme.Ink, new RectangleF(Pad, 0, headW, D(46)), StringAlignment.Near);
                string sub = _flash != null && DateTime.Now < _flashUntil ? _flash
                    : _arch ? L.T("了结、一次性、例行的旧期；点了才归，归了还能找回")
                    : TodoList.Scanning ? L.T("正在扫各家对话…")
                    : TodoList.Items.Count > 0 ? L.F("{0} 件没做完，等你的 {1} 件", TodoList.Items.Count, TodoList.Items.FindAll(i => i.Group == 1).Count) : "";
                Theme.Text(g, sub, small, Theme.Dim, new RectangleF(Pad + headW, 0, W - Pad * 2 - headW - D(40), D(46)), StringAlignment.Near);
                if (_cards.Count == 0)
                    Theme.TextWrap(g, _arch ? L.T("没有可归档的对话。进行中的、长期项目（含搁置的）、例行产出最近几期不会列在这里。")
                        : L.T("没有没做完的事。各家对话收尾时留下的后续会出现在这里，对话归档了也不丢。"), Font, Theme.Dim,
                        new RectangleF(Pad, D(48), W - Pad * 2, D(44)));
                foreach (Card c in _cards) PaintCard(g, c, bold, small);
                using (var pen = new Pen(ShellStyles.Border)) g.DrawLine(pen, Pad, _footY, W - Pad, _footY);
                if (_more > 0)
                    Theme.Text(g, _arch ? L.F("还有 {0} 个没列出来", _more) : L.F("还有 {0} 件没列出来（node agenthost-todo.cjs list）", _more), small, Theme.Dim,
                        new RectangleF(Pad + D(110), _footY + D(8), W - Pad * 2 - D(110) - D(170), D(30)), StringAlignment.Near);
                foreach (Btn b in _btns)
                {
                    if (b.X != null || b.A != null || b.What == "fold") continue;   // 折叠那一行在 PaintCard 里画
                    if (b == _hot)
                        using (GraphicsPath gp = Theme.Round(b.R, D(8)))
                        using (var br = new SolidBrush(ShellStyles.Hover))
                            g.FillPath(br, gp);
                    if (b.What == "close") { Theme.Text(g, "✕", Font, Theme.Dim, b.R, StringAlignment.Center); continue; }
                    if (b.What == "arch" || b.What == "archAll")
                    {
                        Theme.Text(g, RightLabel() ?? "", Font, Theme.Brand, new RectangleF(b.R.X, b.R.Y, b.R.Width - D(6), b.R.Height), StringAlignment.Far);
                        continue;
                    }
                    Theme.Text(g, b.What == "back" ? L.T("‹ 回待办") : L.T("↻ 重新扫"), Font, Theme.Brand, new RectangleF(b.R.X + D(6), b.R.Y, b.R.Width - D(6), b.R.Height), StringAlignment.Near);
                }
            }
        }

        void PaintCard(Graphics g, Card c, Font bold, Font small)
        {
            if (c.Header != null)
                Theme.Text(g, c.Header, small, Theme.Dim, new RectangleF(c.R.X + D(4), c.R.Y - D(22), D(300), D(20)), StringAlignment.Near);
            if (c.FoldKey != null)
            {
                Btn fb = null;
                foreach (Btn b in _btns) if (b.What == "fold" && b.Key == c.FoldKey) fb = b;
                if (fb != null && fb == _hot)
                    using (GraphicsPath gp = Theme.Round(fb.R, D(6)))
                    using (var br = new SolidBrush(ShellStyles.Hover))
                        g.FillPath(br, gp);
                Theme.Text(g, c.Text, small, Theme.Brand, new RectangleF(c.R.X + D(38), c.R.Y, c.R.Width - D(38), c.R.Height), StringAlignment.Near);
                return;
            }
            if (c.A != null) { PaintArchiveCard(g, c, bold, small); return; }
            TodoList.Item x = c.X;
            Rectangle r = c.R;
            bool maybe = x.State == "maybe";
            using (GraphicsPath gp = Theme.Round(r, D(8)))
            using (var br = new SolidBrush(Color.FromArgb(Theme.Light ? 8 : 20, Theme.Light ? Color.Black : Color.White)))
            using (var pen = new Pen(ShellStyles.Border))
            { g.FillPath(br, gp); g.DrawPath(pen, gp); }

            // 头一行：图标 对话标题 ……（推断 / 可能做完了）多久前
            int x0 = r.X + D(12), top = r.Y + D(10);
            if (c.Icon != null) Theme.Icon(g, c.Icon, new Rectangle(x0, top + D(3), D(20), D(20)), D(2), D(4));
            int tx = x0 + D(30);
            // 放了多久：重要的 7 天没动 / 不急不重要的 14 天没动，换成品牌色的那句提醒
            string stale = x.StaleNote;
            string ago = stale.Length > 0 ? stale : TodoList.Ago(x.Updated);
            int agoW = TextRenderer.MeasureText(ago, small).Width + D(4);
            int right = r.Right - D(12) - agoW;
            Theme.Text(g, ago, small, stale.Length > 0 ? Theme.Brand : Theme.Dim, new RectangleF(right, top, agoW, D(26)), StringAlignment.Far);
            // 时间点（急不急就是按它算的）：写在多久前的左边，急的用品牌色
            string due = TodoList.DueLabel(x);
            if (due.Length > 0)
            {
                int dw = TextRenderer.MeasureText(due, small).Width + D(4);
                Theme.Text(g, due, small, x.Urgent && !maybe ? Theme.Brand : Theme.Dim, new RectangleF(right - D(8) - dw, top, dw, D(26)), StringAlignment.Far);
                right -= D(8) + dw;
            }
            // 小标（从右往左）：推断 / 可能做完了、卡在谁（等你拍板、等师傅…）、四象限（不急不重要不写）
            var tags = new List<string[]>();   // { 字, "brand" 用品牌色 }
            if (maybe) tags.Add(new[] { L.T("可能做完了"), "" });
            else if (x.Source == "scan") tags.Add(new[] { L.T("推断"), "" });
            string wl = maybe ? "" : x.WaitLabel;
            if (wl.Length > 0 && x.Group != 4) tags.Add(new[] { wl, "" });
            if (!maybe && x.Quad < 4) tags.Add(new[] { TodoList.QuadName(x.Quad), x.Urgent ? "brand" : "" });
            foreach (string[] tg in tags)
            {
                int tw = TextRenderer.MeasureText(tg[0], small).Width + D(14);
                if (right - D(8) - tw < tx + D(120)) break;   // 标题至少留 120
                var tr = new Rectangle(right - D(8) - tw, top + D(3), tw, D(20));
                bool brand = tg[1] == "brand";
                using (GraphicsPath gp = Theme.Round(tr, D(10)))
                using (var br = new SolidBrush(brand ? Color.FromArgb(Theme.Light ? 22 : 40, Theme.Brand) : Color.FromArgb(28, Theme.Light ? Color.Black : Color.White)))
                    g.FillPath(br, gp);
                Theme.Text(g, tg[0], small, brand ? Theme.Brand : Theme.Dim, tr, StringAlignment.Center);
                right = tr.X - D(8);
            }
            if (x.LongTerm)
            {
                // 项目名的小标在标题前面
                int pw = TextRenderer.MeasureText(x.Project, small).Width + D(12);
                var pr = new Rectangle(tx, top + D(3), pw, D(20));
                using (GraphicsPath gp = Theme.Round(pr, D(6)))
                using (var br = new SolidBrush(Color.FromArgb(Theme.Light ? 22 : 40, Theme.Brand)))
                    g.FillPath(br, gp);
                Theme.Text(g, x.Project, small, maybe ? Theme.Dim : Theme.Brand, pr, StringAlignment.Center);
                tx = pr.Right + D(8);
            }
            string title = string.IsNullOrEmpty(x.Title) ? (string.IsNullOrEmpty(x.Cwd) ? x.Kind : Path.GetFileName(x.Cwd)) : x.Title;
            Theme.Text(g, title, bold, maybe ? Theme.Dim : Theme.Ink, new RectangleF(tx, top, Math.Max(0, right - tx), D(26)), StringAlignment.Near);

            // 剩下的事：最多三行
            using (var br = new SolidBrush(maybe ? Theme.Dim : Theme.Ink))
            using (var sf = TextFormat())
                g.DrawString(x.Text ?? "", Font, br, c.TextR, sf);
            PaintCardButtons(g, c, small);
        }

        void PaintCardButtons(Graphics g, Card c, Font small)
        {
            foreach (Btn b in _btns)
            {
                if (c.X != null ? b.X != c.X : b.A != c.A) continue;
                using (GraphicsPath gp = Theme.Round(b.R, D(6)))
                using (var br = new SolidBrush(b == _hot ? ShellStyles.Hover : Color.Transparent))
                using (var pen = new Pen(ShellStyles.Border))
                { g.FillPath(br, gp); g.DrawPath(pen, gp); }
                string t = b.What == "copy" ? L.T("复制接着做") : b.What == "done" ? L.T("做完了") : b.What == "keep" ? L.T("别归") : L.T("归档");
                Theme.Text(g, t, small, b.What == "done" || b.What == "archOne" ? Theme.Brand : Theme.Ink, b.R, StringAlignment.Center);
            }
        }

        // 归档页的卡：图标、对话标题、闲了几天；下面「做完了 / 一次性 · 理由」和文件夹
        void PaintArchiveCard(Graphics g, Card c, Font bold, Font small)
        {
            TodoList.ArchiveCand a = c.A;
            Rectangle r = c.R;
            using (GraphicsPath gp = Theme.Round(r, D(8)))
            using (var br = new SolidBrush(Color.FromArgb(Theme.Light ? 8 : 20, Theme.Light ? Color.Black : Color.White)))
            using (var pen = new Pen(ShellStyles.Border))
            { g.FillPath(br, gp); g.DrawPath(pen, gp); }
            int x0 = r.X + D(12), top = r.Y + D(10);
            if (c.Icon != null) Theme.Icon(g, c.Icon, new Rectangle(x0, top + D(3), D(20), D(20)), D(2), D(4));
            int tx = x0 + D(30);
            string idle = a.Last > 0 ? L.F("闲了 {0}", TodoList.Ago(a.Last)) : "";
            int iw = TextRenderer.MeasureText(idle, small).Width + D(4);
            Theme.Text(g, idle, small, Theme.Dim, new RectangleF(r.Right - D(12) - iw, top, iw, D(26)), StringAlignment.Far);
            Theme.Text(g, string.IsNullOrEmpty(a.Title) ? a.Kind : a.Title, bold, Theme.Ink, new RectangleF(tx, top, Math.Max(0, r.Right - D(12) - iw - D(8) - tx), D(26)), StringAlignment.Near);
            using (var br = new SolidBrush(Theme.Dim))
            using (var sf = TextFormat())
                g.DrawString(c.Text ?? "", Font, br, c.TextR, sf);
            PaintCardButtons(g, c, small);
        }
    }
}

// 待办条带（2026-11-07 用户选的方案 A：「我肯定是希望能够一眼就看到的而不是需要专门去打开待办」）：
// 工具条底下常驻一行，每件一个胶囊「图标 对话标题 剩下的事… ✓」，放不下的写「还有 N 件 ›」（点开上面那张面板）。
// 点胶囊 = 把「接着做」那句放进剪贴板、切到这家的格子；点 ✓ = 做完了。没有待办时这一行不占地方（高度 0，格子顶上去）。
// 不放红点、不放数字：靠常驻的这一行让人看见，不靠角标（用户三次嫌过图标上的未读数）。
internal sealed partial class HostForm
{
    TodoStrip _todoStrip;
    int _todoStripH;
    bool _todoRelayoutPending;

    // 这一行占的高度（条带 + 到格子的间隔）；全屏和没待办时 0。LayoutToolbar 末尾算
    int TodoStripH { get { return _todoStripH; } }
    int TodoStripRowH { get { return D(30); } }

    void LayoutTodoStrip()
    {
        if (_todoStrip == null) return;
        bool show = !_full && TodoList.ForStrip().Count > 0;
        // 从哪儿起：_toolbarH 是按钮那一排的底；额度那一条（_quota）跟 _pill 一样高，比它低 4，条带又在最上层，
        // 贴着 _toolbarH 起就盖掉环底下那行「5h / 周」小字（用户 11-07 晚：「有点挡住上面的 usage 了」）——
        // 改从 _pill 底下再空 2 起，多出来的算进这一行的高度，格子跟着让
        int y = _toolbarH;
        if (!_pill.IsEmpty) y = Math.Max(y, _pill.Bottom + D(2));
        _todoStripH = show ? (y - _toolbarH) + TodoStripRowH + D(6) : 0;
        if (!show) { _todoStrip.Visible = false; return; }
        int x = GapPx + D(4);
        _todoStrip.Bounds = new Rectangle(x, y, Math.Max(0, ClientSize.Width - x * 2), TodoStripRowH);
        _todoStrip.Visible = true;
        _todoStrip.Reflow();
    }

    // 心跳里：todo.json 变了就重排这一行；要是从没有变成有（或反过来），格子得让 / 顶上去，走一次整体排版 ——
    // 你正在打字就先不动（Relayout 会重新排队，中间那张可能换人），下一拍再看
    void SyncTodoStrip(bool changed)
    {
        if (_todoStrip == null || _closed) return;
        bool show = !_full && TodoList.ForStrip().Count > 0;
        if (!changed && !_todoRelayoutPending) return;
        if (show != _todoStrip.Visible)
        {
            if (_hostActive && CpuStates.InputIdleMs() < 3000) { _todoRelayoutPending = true; return; }
            _todoRelayoutPending = false;
            Relayout(true);
            return;
        }
        _todoRelayoutPending = false;
        if (show) _todoStrip.Reflow();
    }

    // 点了胶囊：「接着做」那句进剪贴板，切到这家的格子（不交焦点：别把正在打的字打进别的窗口）
    void ContinueTodo(TodoList.Item x)
    {
        try { Clipboard.SetText(TodoList.ContinueText(x)); } catch { }
        Toast(L.F("已复制「{0}」接着做的那句话，开新对话粘进去", TodoList.ShortTitle(x)));
        GoToKind(string.IsNullOrEmpty(x.GoKind) ? x.Kind : x.GoKind);
    }

    // 切到这家的格子（不交焦点）：接着做、把「归档这几个」那句交给 Claude 时用
    internal void GoToKind(string go)
    {
        Slot s = null;
        foreach (Slot c in _slots) if (c.Kind.Id == go) { s = c; break; }
        if (s == null) return;
        if (_queueMode) PinFront(s);
        else if (_solo != null && _solo != s) ToggleSolo(s);
    }

    // 点了 ✓：先从条带上拿掉，脚本在后台改文件（它带锁）；没剩下的了就把这一行收掉
    void DoneTodo(TodoList.Item x)
    {
        TodoList.Items.Remove(x);
        Toast(L.F("「{0}」做完了", TodoList.ShortTitle(x)));
        if (TodoList.ForStrip().Count == 0) Relayout(true); else LayoutTodoStrip();
        if (_todoPanel != null && !_todoPanel.IsDisposed) _todoPanel.Reflow();
        TodoList.Run(this, new[] { "done", x.Id }, 20000, delegate { SyncTodoStrip(true); if (_todoPanel != null && !_todoPanel.IsDisposed) _todoPanel.Reflow(); });
    }

    internal sealed class TodoStrip : Control
    {
        readonly HostForm _host;
        internal sealed class Chip { public Rectangle R, CheckR, TagR; public TodoList.Item X; public string Title, Text; public Bitmap Icon; }
        internal Rectangle Divider;   // 第一道竖线（测试看它）；档跟档之间各一道，全在 Dividers 里
        internal readonly List<Rectangle> Dividers = new List<Rectangle>();
        internal readonly List<Chip> Chips = new List<Chip>();
        internal Rectangle MoreR;
        internal int More;
        Chip _hot; bool _hotCheck, _hotMore;
        Font _title, _small;

        public TodoStrip(HostForm host)
        {
            _host = host;
            Theme.Glassy(this);
            BackColor = Color.Transparent;
            TabStop = false;
        }

        int D(int v) { return _host.D(v); }
        Font TitleFont { get { return _title ?? (_title = Fonts.Medium(Font.Size)); } }
        Font SmallFont { get { return _small ?? (_small = Fonts.Regular(Font.Size * 0.9f)); } }
        protected override void OnPaintBackground(PaintEventArgs e) { Theme.PaintUnder(this, e); }
        protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); _title = null; _small = null; }

        static string Cut(Graphics g, string s, Font f, int w)
        {
            if (string.IsNullOrEmpty(s) || w <= 0) return "";
            if (g.MeasureString(s, f).Width <= w) return s;
            int lo = 0, hi = s.Length;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (g.MeasureString(s.Substring(0, mid) + "…", f).Width <= w) lo = mid; else hi = mid - 1;
            }
            return lo == 0 ? "" : s.Substring(0, lo) + "…";
        }

        // 每件一个胶囊：最宽 300、最窄 170；标题最多占 220，剩下的事填余下的；放不下的算进「还有 N 件」。
        // 只放「等你的」和急的、最多 5 个（TodoList.ForStrip，草案「条带只放『等你』+ 急的，最多 5 个，其余在面板里」），
        // 别的（agent 在做、等别人或外部、长期搁置、可能做完了）也算进「还有 N 件」
        public void Reflow()
        {
            Chips.Clear();
            More = 0;
            MoreR = Rectangle.Empty;
            Divider = Rectangle.Empty;
            Dividers.Clear();
            _hot = null;
            int h = Height, pad = D(8), ic = D(18), gap = D(8), chk = D(16);
            List<TodoList.Item> list = TodoList.ForStrip();
            int total = TodoList.Items.Count;
            using (Graphics g = CreateGraphics())
            {
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                string moreS = L.F("还有 {0} 件 ›", 99);
                int moreW = (int)Math.Ceiling(g.MeasureString(moreS, SmallFont).Width) + D(12);
                int x = 0;
                int lastQuad = 0;
                for (int i = 0; i < list.Count; i++)
                {
                    TodoList.Item it = list[i];
                    string title = Cut(g, TodoList.ShortTitle(it), TitleFont, D(220));
                    int tw = (int)Math.Ceiling(g.MeasureString(title, TitleFont).Width);
                    // 长期项目：标题前一个项目名的小标（最多占 120）
                    string tag = it.LongTerm ? Cut(g, it.Project, SmallFont, D(120)) : null;
                    int tagW = tag != null ? (int)Math.Ceiling(g.MeasureString(tag, SmallFont).Width) + D(12) + D(6) : 0;
                    // 有时间点的，剩下的事前面先写时间点（急不急就是按它算的）
                    string due = TodoList.DueLabel(it);
                    int cut = due.IndexOf('（');           // 条带上地方小：「（已过 18 小时）」留给提示和面板
                    if (cut > 0) due = due.Substring(0, cut);
                    string text =(due.Length > 0 ? due + " · " : "") + (it.Text ?? "").Replace("\r", " ").Replace("\n", " ");
                    int fullText = (int)Math.Ceiling(g.MeasureString(text, SmallFont).Width);
                    int w = Math.Max(D(170), Math.Min(D(300) + tagW, pad + ic + D(6) + tagW + tw + D(8) + fullText + D(8) + chk + pad));
                    // 档跟档之间一道竖线（急且重要 | 急 | 重要 | 不急不重要 | 可能做完了）
                    int q = it.State == "maybe" ? 5 : it.Quad;
                    bool divide = Chips.Count > 0 && q != lastQuad;
                    int lead = divide ? D(10) : 0;
                    bool last = i == list.Count - 1 && list.Count == total;
                    // 最后一件放得下、后面也没有别的了，就不留「还有」的位置
                    if (x + lead + w > Width - (last ? 0 : moreW)) break;
                    if (divide) { var dv = new Rectangle(x + D(4), D(7), 1, h - D(14)); if (Dividers.Count == 0) Divider = dv; Dividers.Add(dv); x += lead; }
                    lastQuad = q;
                    int textW = w - (pad + ic + D(6) + tagW + tw + D(8) + D(8) + chk + pad);
                    AgentKind k = Catalog.ById(it.Kind);
                    var chip = new Chip
                    {
                        R = new Rectangle(x, 0, w, h), X = it, Title = title, Icon = k != null ? AgentIcons.Get(k, ic) : null,
                        Text = textW > D(24) ? Cut(g, text, SmallFont, textW) : "",
                        CheckR = new Rectangle(x + w - pad - chk - D(4), 0, chk + D(8), h),
                    };
                    if (tag != null) chip.TagR = new Rectangle(x + pad + ic + D(6), (h - D(20)) / 2, tagW - D(6), D(20));
                    Chips.Add(chip);
                    if (tag != null) _tagText[chip] = tag;
                    x += w + gap;
                }
                More = total - Chips.Count;
                if (More > 0) MoreR = new Rectangle(x, 0, Math.Min(moreW, Math.Max(0, Width - x)), h);
            }
            Invalidate();
        }

        readonly Dictionary<Chip, string> _tagText = new Dictionary<Chip, string>();
        internal Chip ChipAt(Point p) { foreach (Chip c in Chips) if (c.R.Contains(p)) return c; return null; }

        string TipOf(Chip c, bool check)
        {
            if (check) return L.T("做完了：从待办里勾掉");
            AgentKind k = Catalog.ById(c.X.Kind);
            string who = "【" + (c.X.State == "maybe" ? L.T("可能做完了") : TodoList.QuadName(c.X.Quad)) + "】"
                + (c.X.LongTerm ? L.T("长期项目") + "「" + c.X.Project + "」· " : "") + (k != null ? k.Name : c.X.Kind) + " · " + TodoList.ShortTitle(c.X)
                + (c.X.State == "maybe" ? L.T("（可能做完了）") : c.X.Source == "scan" ? L.T("（推断）") : "")
                + (c.X.Updated > 0 ? " · " + TodoList.Ago(c.X.Updated) : "");
            return who + "\n" + (c.X.Text ?? "") + "\n\n" + L.T("点一下：复制「接着做」那句话，切到这家的格子");
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            Chip c = ChipAt(e.Location);
            bool check = c != null && c.CheckR.Contains(e.Location);
            bool more = c == null && MoreR.Contains(e.Location);
            if (c != _hot || check != _hotCheck || more != _hotMore)
            {
                _hot = c; _hotCheck = check; _hotMore = more;
                Cursor = c != null || more ? Cursors.Hand : Cursors.Default;
                _host._tips.SetToolTip(this, c != null ? TipOf(c, check) : more ? L.T("点开待办面板看全部：agent 在做的、等别人或外部的、长期搁置的都在那里") : "");
                Invalidate();
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            if (_hot != null || _hotMore) { _hot = null; _hotMore = false; _hotCheck = false; Invalidate(); }
            Cursor = Cursors.Default;
            base.OnMouseLeave(e);
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (e.Button != MouseButtons.Left) return;
            Chip c = ChipAt(e.Location);
            if (c != null)
            {
                if (c.CheckR.Contains(e.Location)) _host.DoneTodo(c.X);
                else _host.ContinueTodo(c.X);
                return;
            }
            if (MoreR.Contains(e.Location)) _host.ShowTodo();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            try { PaintBody(e); }
            catch (Exception ex) { PaintGuard.Failed(this, ex); }
        }

        void PaintBody(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            int pad = D(8), ic = D(18);
            foreach (Chip c in Chips)
            {
                bool maybe = c.X.State == "maybe";
                bool hot = c == _hot;
                using (GraphicsPath gp = Theme.Round(c.R, D(8)))
                using (var br = new SolidBrush(hot && !_hotCheck ? ShellStyles.Hover : Theme.ChipBg))
                    g.FillPath(br, gp);
                int x = c.R.X + pad;
                if (c.Icon != null) Theme.Icon(g, c.Icon, new Rectangle(x, c.R.Y + (c.R.Height - ic) / 2, ic, ic), D(1), D(4));
                x += ic + D(6);
                string tag;
                if (c.TagR.Width > 0 && _tagText.TryGetValue(c, out tag))
                {
                    using (GraphicsPath gp = Theme.Round(c.TagR, D(6)))
                    using (var br = new SolidBrush(Color.FromArgb(Theme.Light ? 22 : 40, Theme.Brand)))
                        g.FillPath(br, gp);
                    Theme.Text(g, tag, SmallFont, maybe ? Theme.Dim : Theme.Brand, c.TagR, StringAlignment.Center);
                    x = c.TagR.Right + D(6);
                }
                int tw = (int)Math.Ceiling(g.MeasureString(c.Title, TitleFont).Width);
                Theme.Text(g, c.Title, TitleFont, maybe ? Theme.Dim : Theme.Ink, new RectangleF(x, c.R.Y, tw + D(2), c.R.Height), StringAlignment.Near);
                x += tw + D(8);
                int textRight = c.CheckR.X - D(4);
                if (!string.IsNullOrEmpty(c.Text) && textRight > x)
                    Theme.Text(g, c.Text, SmallFont, Theme.Dim, new RectangleF(x, c.R.Y, textRight - x, c.R.Height), StringAlignment.Near);
                if (hot && _hotCheck)
                    using (GraphicsPath gp = Theme.Round(new Rectangle(c.CheckR.X, c.R.Y + D(4), c.CheckR.Width, c.R.Height - D(8)), D(6)))
                    using (var br = new SolidBrush(ShellStyles.Hover))
                        g.FillPath(br, gp);
                Theme.Text(g, "✓", TitleFont, Theme.Brand, c.CheckR, StringAlignment.Center);
            }
            using (var pen = new Pen(Theme.Line))
                foreach (Rectangle dv in Dividers) g.DrawLine(pen, dv.X, dv.Y, dv.X, dv.Bottom);
            if (More > 0 && MoreR.Width > 0)
                Theme.Text(g, L.F("还有 {0} 件 ›", More), SmallFont, _hotMore ? Theme.Ink : Theme.Dim,
                    new RectangleF(MoreR.X + D(6), MoreR.Y, MoreR.Width - D(6), MoreR.Height), StringAlignment.Near);
        }
    }
}
