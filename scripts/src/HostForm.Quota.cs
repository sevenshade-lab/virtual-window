// 订阅额度：顶栏按钮那一排右边一条小条，每家一段「名字 + 一截剩余量 + 剩多少」，点开是明细面板。
//   用户 2026-10-27 要的：「看到哪些额度还有、需要赶紧花，哪些已经没有了」；位置他说放顶上那一块（或左侧边）。
//   数从 agenthost-quota.cjs 来（嵌在 exe 里，跑的时候放到 %LOCALAPPDATA%\AgentHost\bin）：
//   它读 Claude / Codex / Cursor 本机已登录的凭据问各家的用量接口，写 quota.json；宿主每 10 分钟跑一次，点开面板时也跑一次。
//   有哪几家、看用量的网址在 quota-accounts.json，面板底下「改账户」打开它（月费不在这里管）。
//   颜色三档：用完了（灰）/ 快清零还剩不少（橙，该赶紧花）/ 其余（绿）。
//   2026-10-27 第二版：六家都读官方的数（OpenCode Go、Antigravity、Grok Bot 原先估的 / 读不到），长条换成圆环，字放大，英文跟着切。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows.Forms;

internal sealed class QuotaWindow
{
    public string Label;
    public string Group, Period; // 官方额度组；同组各窗口都要有余量，不同组可任选
    public string GroupName { get { return (Group ?? "").Replace(" Models", "").Replace(" models", "").Replace(" and ", "/"); } }
    public string DisplayLabel { get { return string.IsNullOrEmpty(Group) ? L.T(Label) : GroupName + " · " + L.T(Label); } }
    public double Used = -1;      // 0–100；读不到是 -1
    public long ResetsAt;         // 毫秒时间戳；0 = 不知道
    public bool Minor;            // 次要的（某个模型单独的限额之类）：只在面板里列，不算进顶栏
    public double Left { get { return Used < 0 ? -1 : Math.Max(0, 100 - Used); } }
}

internal sealed class QuotaAccount
{
    public string Id, Icon, Name, Plan, Currency, Url, Error, Note;
    public double Price = -1;
    public bool Ok, Manual, LimitReached;
    public bool Hidden;     // 用户在面板里点了「隐藏」
    public bool Inactive;   // 脚本读到套餐是免费档：订阅没了，自动收起
    public bool Shown { get { return !Hidden && !Inactive; } }
    public long At;
    public int RenewDay;
    // 按量付费的（DeepSeek）没有限额，只有余额；-1 = 不是这种
    public double Balance = -1, Granted, ToppedUp;
    public string BalanceCurrency;
    // Colab 计算单元（挂在 Antigravity 那张卡上，同属 Google AI Pro）；-1 = 没有
    public double ColabUnits = -1, ColabRate = -1;
    public long ColabAt;
    public bool ColabStale;
    public readonly List<QuotaWindow> Windows = new List<QuotaWindow>();

    // 顶栏看的那一个：最长的那个主窗口（每周 / 每月）——「快清零」说的是它；某个短窗口用完了另算（Exhausted）
    public QuotaWindow Head
    {
        get
        {
            QuotaWindow best = null;
            foreach (QuotaWindow w in Windows)
                if (!w.Minor && w.Used >= 0 && (best == null || w.ResetsAt > best.ResetsAt)) best = w;
            return best;
        }
    }

    // 现在用不了。时间窗口（5 小时 / 周 / 月）是「都得有」：任何一个满了就卡住；
    // 不同额度组是「有一个就行」；Antigravity 同组的周 / 5 小时都得有，不能把两个时间窗口当成可互相替代。
    static bool IsPool(QuotaWindow w) { return w.Label == "本月 Auto" || w.Label == "本月指定模型" || w.Label.EndsWith(" 模型"); }

    public QuotaWindow Blocking
    {
        get
        {
            QuotaWindow b = null;
            var pools = new Dictionary<string, QuotaWindow>();
            bool grouped = false;
            foreach (QuotaWindow w in Windows)
            {
                if (w.Minor || w.Used < 0) continue;
                string pool = !string.IsNullOrEmpty(w.Group) ? w.Group : IsPool(w) ? w.Label : null;
                if (pool != null)
                {
                    if (!string.IsNullOrEmpty(w.Group)) grouped = true;
                    if (!pools.ContainsKey(pool)) pools[pool] = null;
                    if (w.Used >= 98 && (pools[pool] == null || w.ResetsAt > pools[pool].ResetsAt)) pools[pool] = w;
                }
                else if (w.Used >= 98 && (b == null || w.ResetsAt > b.ResetsAt)) b = w;
            }
            if (b != null) return b;
            foreach (QuotaWindow blocked in pools.Values)
            {
                if (blocked == null) return null; // 至少一组还可用
                if (b == null || (grouped ? blocked.ResetsAt < b.ResetsAt : blocked.ResetsAt > b.ResetsAt)) b = blocked;
            }
            return b; // 官方分组全耗尽时，看最早恢复的组；旧格式保留原重置时间选择
        }
    }

    public bool Exhausted { get { return LimitReached || Blocking != null || Balance == 0; } }

    // 该赶紧花：周 / 月额度两天内清零，还剩四分之一以上
    // 该赶紧花：某个按周 / 按月作废的额度两天内清零、还剩四分之一以上。逐个限额看（Cursor 的 Auto 用完了，
    // 「指定模型」那份却几乎没动 —— 用户 10-27 要先把这种小额度用掉）；5 小时一轮的不算，一会儿就回来
    public static bool Expiring(QuotaWindow w)
    {
        if (w.Period != "weekly" && w.Period != "monthly")
            switch (w.Label) { case "每周": case "每月": case "本月": case "本月 Auto": case "本月指定模型": break; default: return false; }
        if (w.Minor || w.Used < 0 || w.Used >= 95 || w.ResetsAt <= 0) return false;
        double hours = (w.ResetsAt - Quota.NowMs) / 3600000.0;
        return hours > 0 && hours <= 48 && w.Left >= 25;
    }

    public bool Hurry
    {
        get
        {
            if (LimitReached || Balance == 0) return false;
            foreach (QuotaWindow w in Windows) if (Expiring(w)) return true;
            return false;
        }
    }
}

internal static class Quota
{
    static string BaseDir { get { return Path.Combine(Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AgentHost"); } }
    public static string ScriptPath { get { return Path.Combine(BaseDir, "bin", "agenthost-quota.cjs"); } }
    public static string DataPath { get { return Path.Combine(BaseDir, "quota.json"); } }
    public static string AccountsPath { get { return Path.Combine(BaseDir, "quota-accounts.json"); } }

    public static long NowMs { get { return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); } }

    public static List<QuotaAccount> Accounts = new List<QuotaAccount>();
    public static long Updated;
    public static bool Running;
    static DateTime _stamp;

    // 读 quota.json（文件没变就不重读）。返回有没有换新的
    public static bool Load()
    {
        try
        {
            if (!File.Exists(DataPath)) return false;
            DateTime t = File.GetLastWriteTimeUtc(DataPath);
            if (t == _stamp) return false;
            var list = new List<QuotaAccount>();
            using (JsonDocument doc = JsonDocument.Parse(File.ReadAllText(DataPath, Encoding.UTF8)))
            {
                JsonElement root = doc.RootElement, arr, v;
                Updated = root.TryGetProperty("updated", out v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;
                if (root.TryGetProperty("accounts", out arr) && arr.ValueKind == JsonValueKind.Array)
                    foreach (JsonElement a in arr.EnumerateArray())
                    {
                        var q = new QuotaAccount
                        {
                            Id = Str(a, "id"), Icon = Str(a, "icon"), Name = Str(a, "name"), Plan = Str(a, "plan"), Currency = Str(a, "currency"),
                            Url = Str(a, "url"), Error = Str(a, "error"), Note = Str(a, "note"),
                            Price = Num(a, "price", -1), Ok = Bool(a, "ok"), Manual = Bool(a, "manual"),
                            LimitReached = Bool(a, "limitReached"), Hidden = Bool(a, "hidden"), Inactive = Bool(a, "inactive"), At = (long)Num(a, "at", 0), RenewDay = (int)Num(a, "renewDay", 0),
                        };
                        if (a.TryGetProperty("windows", out v) && v.ValueKind == JsonValueKind.Array)
                            foreach (JsonElement w in v.EnumerateArray())
                                q.Windows.Add(new QuotaWindow
                                {
                                    Label = Str(w, "label"), Group = Str(w, "group"), Period = Str(w, "period"), Used = Num(w, "used", -1),
                                    ResetsAt = (long)Num(w, "resetsAt", 0), Minor = Bool(w, "minor"),
                                });
                        if (a.TryGetProperty("balance", out v) && v.ValueKind == JsonValueKind.Object)
                        {
                            q.Balance = Num(v, "total", -1); q.Granted = Num(v, "granted", 0); q.ToppedUp = Num(v, "toppedUp", 0);
                            q.BalanceCurrency = Str(v, "currency");
                        }
                        if (a.TryGetProperty("colab", out v) && v.ValueKind == JsonValueKind.Object)
                        {
                            q.ColabUnits = Num(v, "units", -1); q.ColabRate = Num(v, "rate", -1);
                            q.ColabAt = (long)Num(v, "at", 0); q.ColabStale = Bool(v, "stale");
                        }
                        list.Add(q);
                    }
            }
            Accounts = list;
            _stamp = t;
            return true;
        }
        catch (Exception ex) { Log.W("quota: quota.json 读不了 " + ex.Message); return false; }
    }

    static string Str(JsonElement o, string k)
    {
        JsonElement v;
        return o.TryGetProperty(k, out v) && v.ValueKind == JsonValueKind.String ? v.GetString() : "";
    }
    static double Num(JsonElement o, string k, double def)
    {
        JsonElement v;
        return o.TryGetProperty(k, out v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : def;
    }
    static bool Bool(JsonElement o, string k)
    {
        JsonElement v;
        return o.TryGetProperty(k, out v) && v.ValueKind == JsonValueKind.True;
    }

    // exe 里嵌的那份跟 bin 里的不一样（或者还没有）就放出去：这个脚本不归「重新安装钩子」管，换了 exe 自己跟上
    static void Extract()
    {
        using (Stream s = typeof(Quota).Assembly.GetManifestResourceStream("agenthost-quota.cjs"))
        {
            if (s == null) throw new InvalidOperationException("exe 里没有嵌额度脚本");
            var ms = new MemoryStream();
            s.CopyTo(ms);
            byte[] want = ms.ToArray();
            try { if (File.Exists(ScriptPath) && ((ReadOnlySpan<byte>)File.ReadAllBytes(ScriptPath)).SequenceEqual(want)) return; } catch { }
            Directory.CreateDirectory(Path.GetDirectoryName(ScriptPath));
            string tmp = ScriptPath + ".tmp";
            File.WriteAllBytes(tmp, want);
            File.Move(tmp, ScriptPath, true);
        }
    }

    // 用户 2026-11-09：Cursor、Grok Bot 暂时不续了，「说不定以后也会重新订阅」「去记事本里面改的话太麻烦」
    //   「按照额度来判断逻辑」「发现订阅没有了就自动去掉显示」「有新的订阅被发现了就自动加上」。
    // 不在顶栏上的那几家：手动藏的 + 读到没订阅自动收起的（脚本把它们也照读、写进 quota.json，带 hidden / inactive）
    public static List<QuotaAccount> Hidden() { return Accounts.FindAll(delegate(QuotaAccount a) { return !a.Shown; }); }

    static string Text(JsonNode n) { return n != null && n.GetValueKind() == JsonValueKind.String ? n.GetValue<string>() : ""; }

    // 藏 / 放回一家：只动它那一条，别的原样留着；眼前马上跟着变，不等脚本再跑。
    //   藏：hidden=true（去掉 keepShown）。放回：去掉 hidden / lapsedSeen；它要是因为没订阅被自动收起的，记 keepShown，以后不再自动收
    public static bool SetHidden(string id, bool hide)
    {
        try
        {
            if (!File.Exists(AccountsPath)) return false;
            JsonArray arr = JsonNode.Parse(File.ReadAllText(AccountsPath, Encoding.UTF8)) as JsonArray;
            if (arr == null) return false;
            QuotaAccount mem = Accounts.Find(delegate(QuotaAccount a) { return a.Id == id; });
            bool found = false;
            foreach (JsonNode n in arr)
            {
                JsonObject o = n as JsonObject;
                if (o == null || Text(o["id"]) != id) continue;
                found = true;
                if (hide) { o["hidden"] = true; o.Remove("keepShown"); }
                else
                {
                    o.Remove("hidden"); o.Remove("lapsedSeen");
                    if (mem != null && mem.Inactive) o["keepShown"] = true;
                }
            }
            if (!found) return false;
            var opt = new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
            string tmp = AccountsPath + ".tmp";
            File.WriteAllText(tmp, arr.ToJsonString(opt) + "\n", new UTF8Encoding(false));
            File.Move(tmp, AccountsPath, true);
            if (mem != null) { mem.Hidden = hide; if (!hide) mem.Inactive = false; }
            Log.W("quota: " + (hide ? "藏起 " : "放回 ") + id);
            return true;
        }
        catch (Exception ex) { Log.W("quota: 改 quota-accounts.json 失败 " + ex.Message); return false; }
    }

    static bool _again;

    // 在后台跑一次脚本；跑完在界面线程上回调（读到新数才回调 changed=true）
    public static void Refresh(Control ui, Action done)
    {
        if (Running) { _again = true; return; }   // 正在跑的那次可能没赶上刚放回来的那家，跑完再来一次
        _again = false;
        string node = HookSetup.Node();
        if (node == null) { Log.W("quota: 没找到 node.exe"); return; }
        Running = true;
        Task.Run(delegate
        {
            try
            {
                Extract();
                var psi = new ProcessStartInfo(node)
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardError = true, RedirectStandardOutput = true,
                    StandardErrorEncoding = Encoding.UTF8, StandardOutputEncoding = Encoding.UTF8,
                };
                psi.ArgumentList.Add(ScriptPath);
                using (Process p = Process.Start(psi))
                {
                    Task<string> err = p.StandardError.ReadToEndAsync();
                    p.StandardOutput.ReadToEnd();
                    if (!p.WaitForExit(60000)) { try { p.Kill(); } catch { } Log.W("quota: 脚本 60 秒没跑完"); }
                    else if (p.ExitCode != 0) Log.W("quota: 脚本出错 " + err.Result.Trim());
                }
            }
            catch (Exception ex) { Log.W("quota: " + ex.Message); }
            finally
            {
                Running = false;
                try { if (!ui.IsDisposed) ui.BeginInvoke((MethodInvoker)delegate { Load(); done(); if (_again) Refresh(ui, done); }); } catch { }
            }
        });
    }

    // 「还有 1 天 13 小时」/「还有 40 分钟」
    public static string Until(long ms)
    {
        if (ms <= 0) return "";
        TimeSpan d = TimeSpan.FromMilliseconds(ms - NowMs);
        if (d.TotalMinutes < 1) return L.T("马上");
        if (d.TotalHours < 1) return L.F("{0} 分钟", (int)d.TotalMinutes);
        if (d.TotalDays < 1) return L.F("{0} 小时", (int)d.TotalHours);
        return d.Hours > 0 && d.TotalDays < 3 ? L.F("{0} 天 {1} 小时", (int)d.TotalDays, d.Hours) : L.F("{0} 天", (int)Math.Round(d.TotalDays));
    }

    // 「周四 08:00」：一律北京时间（本机时区是太平洋时间，见 TaskList.When 那边的说明）
    public static string At(long ms)
    {
        if (ms <= 0) return "";
        DateTime t = DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime.AddHours(8);
        DateTime today = DateTime.UtcNow.AddHours(8).Date;
        string[] wk = { "周日", "周一", "周二", "周三", "周四", "周五", "周六" };
        string day = t.Date == today ? L.T("今天") : t.Date == today.AddDays(1) ? L.T("明天")
                   : (t.Date - today).TotalDays < 7 ? L.T(wk[(int)t.DayOfWeek]) : t.ToString("MM-dd");
        return day + " " + t.ToString("HH:mm");
    }

    public static string Money(double v, string cur)
    {
        string c = (cur ?? "").ToUpperInvariant();
        string n = v == Math.Floor(v) ? ((int)v).ToString() : v.ToString("0.##");
        return c == "CNY" || c == "RMB" ? "¥" + n : c == "USD" || c == "" ? "$" + n : n + " " + cur;
    }

    public static readonly Color Good = Color.FromArgb(255, 30, 142, 62);
    public static readonly Color Hurry = Color.FromArgb(255, 227, 116, 0);
    // 快用完（剩 15% 以下）和用完：红。以前是灰，用户 10-27 说要红、提醒得更明显
    public static readonly Color Spent = Color.FromArgb(255, 217, 48, 37);
    public const double LowAt = 85;   // 用到这么多就算快用完

    public static Color Tone(QuotaAccount a) { return a.Exhausted ? Spent : a.Hurry ? Hurry : Good; }
}

// 顶栏那一条：每一家都列（用户 2026-10-27：「最好每一家都显示出来」「五小时和周限额都要表达出来」）。
//   每家：图标，后面每个限额一个环 —— 剩多少画多少、环里写剩几成、环下面一行小字「5h / 周 / 月」。
//   （同一天试过「图标外面套内外两圈 + 右边两行字」，用户说环太丑；给了三种圆形写法，他选的这种。）
//   放不下时先收紧家与家之间的空，再放不下的那几家不画（悬停提示里都有）。
internal sealed class QuotaStrip : Control
{
    readonly HostForm _host;
    bool _hot;
    public QuotaStrip(HostForm host)
    {
        _host = host;
        Theme.Glassy(this);
        BackColor = Color.Transparent;
        TabStop = false;
        Cursor = Cursors.Hand;
    }

    int D(int v) { return _host.D(v); }
    protected override void OnMouseEnter(EventArgs e) { _hot = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hot = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnPaintBackground(PaintEventArgs e) { Theme.PaintUnder(this, e); }

    public static List<QuotaAccount> Shown() { return Quota.Accounts.FindAll(delegate(QuotaAccount a) { return a.Shown; }); }

    // 内圈：5 小时；外圈：每周，没有就本月（Cursor）/ 每月
    public static QuotaWindow Short(QuotaAccount a) { return Find(a, "5 小时"); }
    public static QuotaWindow Long(QuotaAccount a) { return Find(a, "每周") ?? Find(a, "本月") ?? Find(a, "每月"); }
    static QuotaWindow Find(QuotaAccount a, string label)
    {
        foreach (QuotaWindow w in a.Windows) if (!w.Minor && w.Used >= 0 && w.Label == label) return w;
        return null;
    }
    // 环下面那一行小字。Cursor 的 Auto / 指定模型（Opus 走这份）、Antigravity 的 Claude / Gemini 是 2026-10-27 拆出来的
    // 用户 11-09「Gemini这种有多个额度的排版能不能专门优化」：组名只留一个词（Claude/GPT → Claude，Gemini 4 Argon → Argon），全名在面板和悬停提示里
    internal static string ShortGroup(string group)
    {
        string n = new QuotaWindow { Group = group }.GroupName.Trim();
        int slash = n.IndexOf('/');
        if (slash > 0) return n.Substring(0, slash);
        string[] words = n.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        return words.Length > 1 && words[0] == "Gemini" ? words[words.Length - 1] : n;
    }

    internal static string Mark(QuotaWindow w)
    {
        if (!string.IsNullOrEmpty(w.Group)) return ShortGroup(w.Group);
        switch (w.Label)
        {
            case "5 小时": return "5h";
            case "每周": return L.T("周");
            case "本月 Auto": return "Auto";
            case "本月指定模型": return L.T("指定");
            case "Claude 模型": return "Claude";
            case "Gemini 模型": return "Gemini";
            default: return L.T("月");
        }
    }
    public static string Units(double v) { return v.ToString("0.##"); }
    static string Val(QuotaWindow w) { return w.Used >= 98 ? L.T("用完") : (int)Math.Round(w.Left) + "%"; }

    // 一家在悬停提示里的一行：「Claude  5 小时剩 84% · 每周剩 4%（周四 08:00 重置）」
    public static string Line(QuotaAccount a)
    {
        var parts = new List<string>();
        foreach (QuotaWindow w in a.Windows.Exists(delegate(QuotaWindow x) { return !string.IsNullOrEmpty(x.Group); }) ? a.Windows : Rings(a))
        {
            if (w.Used < 0) continue;
            string s = w.DisplayLabel + " " + (w.Used >= 98 ? L.T("用完") : L.F("剩 {0}%", (int)Math.Round(w.Left)));
            if (w.ResetsAt > 0 && (w.Used >= 98 || w == a.Head)) s += L.F("（{0} 重置）", Quota.At(w.ResetsAt));
            parts.Add(s);
        }
        if (a.Balance >= 0) parts.Add(L.T("余额") + " " + Quota.Money(a.Balance, a.BalanceCurrency));
        if (a.ColabUnits >= 0) parts.Add(L.F("Colab 剩 {0} 单元", Units(a.ColabUnits)));
        string line = a.Name + "  " + (parts.Count > 0 ? string.Join(" · ", parts)
                    : !string.IsNullOrEmpty(a.Error) ? L.F("这次没读到：{0}", a.Error) : L.T("读不到，点开去官网看"));
        if (a.Hurry) line += L.T("  ← 快清零，赶紧用");
        return line;
    }

    public static Color ToneOf(QuotaAccount a, QuotaWindow w)
    {
        // 剩 5% 以下也画灰：那点量干不了什么，看着就该当「没有了」
        if (w.Used >= Quota.LowAt || a.LimitReached) return Quota.Spent;
        return QuotaAccount.Expiring(w) ? Quota.Hurry : Quota.Good;
    }

    // 一道圆弧：底是淡的整圈，剩多少从 12 点顺时针画多少
    public static void Arc(Graphics g, RectangleF r, float width, double left, Color tone)
    {
        using (var track = new Pen(Color.FromArgb(Theme.Light ? 30 : 55, Theme.Light ? Color.Black : Color.White), width))
            g.DrawEllipse(track, r);
        if (left <= 0) return;
        using (var pen = new Pen(tone, width) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawArc(pen, r, -90, (float)Math.Max(6, 360 * Math.Min(100, left) / 100));
    }

    Font _num, _tag;
    Font Num { get { return _num ?? (_num = Fonts.Medium(8f)); } }
    Font TagFont { get { return _tag ?? (_tag = Fonts.Regular(7.5f)); } }

    Bitmap IconOf(QuotaAccount a, int px)
    {
        AgentKind k = Catalog.ById(string.IsNullOrEmpty(a.Icon) ? a.Id : a.Icon);
        return k != null ? AgentIcons.Get(k, px) : null;
    }

    // 每个限额一个环，环里写剩几成，环下面一行小字「5h / 周 / 月」；环的大小跟着这一条的高度走（宿主给的比按钮高一点）
    int TagH { get { return D(12); } }
    int Ring { get { return Math.Max(D(20), Math.Min(D(28), Height - TagH)); } }
    int IconPx { get { return D(20); } }

    internal static List<QuotaWindow> Rings(QuotaAccount a)
    {
        var list = new List<QuotaWindow>();
        foreach (QuotaWindow w in a.Windows)
        {
            if (w.Minor || w.Used < 0) continue;
            int same = string.IsNullOrEmpty(w.Group) ? -1 : list.FindIndex(delegate(QuotaWindow x) { return x.Group == w.Group; });
            if (same < 0) { if (list.Count < 3) list.Add(w); continue; }
            QuotaWindow old = list[same];
            // 一组一个环：平时看周额度；短时额度用完时改显示它，避免环还绿着却用不了。
            if (w.Used >= 98 ? old.Used < 98 || w.ResetsAt > old.ResetsAt : old.Used < 98 && w.Period == "weekly") list[same] = w;
        }
        return list;
    }

    // 读不到数的：图标后面一个空环，环里一道「—」
    // 一个环占多宽：环和下面那行字取宽的
    int Slot(QuotaWindow w)
    {
        using (Graphics g = CreateGraphics()) return Math.Max(Ring, (int)Math.Ceiling(g.MeasureString(Mark(w), TagFont).Width));
    }

    // 多额度的那家（两个环以上，或带 Colab）：环挨紧、整组垫一层淡底。
    //   11-09 出了三种（A 只缩字收紧 / B 再垫淡底 / C 淡底 + Colab 隔开写成数字签），用户选 B
    static bool Multi(QuotaAccount a) { return Rings(a).Count + (a.ColabUnits >= 0 ? 1 : 0) >= 2; }
    int Pack { get { return D(6); } }   // 淡底四周留的边

    int ColabSlot(QuotaAccount a)
    {
        if (a.ColabUnits < 0) return 0;
        using (Graphics g = CreateGraphics())
        {
            return Math.Max(Ring, (int)Math.Ceiling(g.MeasureString("Colab", TagFont).Width));
        }
    }
    static string ColabText(QuotaAccount a) { return ((int)Math.Floor(a.ColabUnits)).ToString(); }

    int Seg(QuotaAccount a, int ringGap)
    {
        List<QuotaWindow> rings = Rings(a);
        bool multi = Multi(a);
        int gapIn = multi ? Math.Min(ringGap, D(1)) : ringGap;
        int w = IconPx + D(5);
        if (rings.Count == 0 && a.ColabUnits < 0) return w + Ring;
        foreach (QuotaWindow r in rings) w += Slot(r) + gapIn;
        if (a.ColabUnits >= 0) w += ColabSlot(a) + gapIn;
        w -= gapIn;
        return multi ? w + Pack * 2 : w;
    }

    int Total(int gap, int ringGap)
    {
        List<QuotaAccount> list = Shown();
        if (list.Count == 0) return 0;
        int w = D(8);
        foreach (QuotaAccount a in list) w += Seg(a, ringGap) + gap;
        return w - gap + D(8);
    }

    public int Want() { return Total(D(18), D(4)); }

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
        if (_hot)
            using (GraphicsPath gp = Theme.Round(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), D(8)))
            using (var br = new SolidBrush(Color.FromArgb(12, Theme.Light ? Color.Black : Color.White)))
                g.FillPath(br, gp);
        // 放不下就先收紧家与家之间的空
        int gap = D(18), ringGap = D(4);
        if (Total(gap, ringGap) > Width) { gap = D(10); ringGap = D(2); }
        int x = D(8), top = (Height - Ring - TagH) / 2;
        foreach (QuotaAccount a in Shown())
        {
            int w = Seg(a, ringGap);
            if (x + w > Width) break;   // 最挤也放不下的不画半截
            bool multi = Multi(a);
            int gapIn = multi ? Math.Min(ringGap, D(1)) : ringGap;
            int x0 = x;
            if (multi)
            {
                using (GraphicsPath gp = Theme.Round(new RectangleF(x + 0.5f, top - D(3) + 0.5f, w - 1, Ring + TagH + D(5)), D(8)))
                using (var br = new SolidBrush(Color.FromArgb(Theme.Light ? 10 : 22, Theme.Light ? Color.Black : Color.White)))
                using (var pen = new Pen(Color.FromArgb(Theme.Light ? 22 : 34, Theme.Light ? Color.Black : Color.White)))
                { g.FillPath(br, gp); g.DrawPath(pen, gp); }
                x += Pack;
            }
            Bitmap icon = IconOf(a, IconPx);
            var ir = new Rectangle(x, top + (Ring - IconPx) / 2, IconPx, IconPx);
            if (icon != null) Theme.Icon(g, icon, ir, 0, D(5));
            else Theme.Text(g, a.Name.Substring(0, 1), Num, Theme.Dim, ir, StringAlignment.Center);
            int cx = x + IconPx + D(5);
            List<QuotaWindow> rings = Rings(a);
            if (rings.Count == 0 && a.Balance >= 0)
            {
                var rb = new Rectangle(cx, top, Ring, Ring);
                float swb = D(2) + 0.5f;
                Color tb = a.Exhausted ? Quota.Spent : Quota.Good;
                Arc(g, new RectangleF(rb.X + swb / 2, rb.Y + swb / 2, rb.Width - swb, rb.Height - swb), swb, a.Exhausted ? 0 : 100, tb);
                Theme.Text(g, Quota.Money(Math.Round(a.Balance), a.BalanceCurrency), Num, a.Exhausted ? Quota.Spent : Theme.Ink, new RectangleF(rb.X - D(4), rb.Y, rb.Width + D(8), rb.Height), StringAlignment.Center);
                Theme.Text(g, L.T("余额"), TagFont, Theme.Dim, new RectangleF(cx - D(14), top + Ring, Ring + D(28), TagH), StringAlignment.Center);   // 英文「Balance」比环宽，往两边多借一点
            }
            else if (rings.Count == 0 && a.ColabUnits < 0)
            {
                var r0 = new Rectangle(cx, top, Ring, Ring);
                float sw0 = D(2) + 0.5f;
                Arc(g, new RectangleF(r0.X + sw0 / 2, r0.Y + sw0 / 2, r0.Width - sw0, r0.Height - sw0), sw0, 0, Theme.Dim);
                Theme.Text(g, "—", Num, Theme.Dim, r0, StringAlignment.Center);
            }
            foreach (QuotaWindow wd in rings)
            {
                int sl = Slot(wd);
                PaintRing(g, a, wd, new Rectangle(cx + (sl - Ring) / 2, top, Ring, Ring));
                Theme.Text(g, Mark(wd), TagFont, Theme.Dim, new RectangleF(cx - D(2), top + Ring, sl + D(4), TagH), StringAlignment.Center);
                cx += sl + gapIn;
            }
            if (a.ColabUnits >= 0) PaintColab(g, a, cx, top);
            x = x0 + w + gap;
        }
    }

    // Colab：剩多少计算单元。不是百分比，环画满（读不到新数时灰），环里写整数
    void PaintColab(Graphics g, QuotaAccount a, int cx, int top)
    {
        int sl = ColabSlot(a);
        Color tone = a.ColabUnits < 1 ? Quota.Spent : a.ColabStale ? Theme.Dim : Quota.Good;
        Color ink = a.ColabUnits < 1 ? Quota.Spent : Theme.Ink;
        var r = new Rectangle(cx + (sl - Ring) / 2, top, Ring, Ring);
        float sw = D(2) + 0.5f;
        Arc(g, new RectangleF(r.X + sw / 2, r.Y + sw / 2, r.Width - sw, r.Height - sw), sw, a.ColabUnits < 1 ? 0 : 100, tone);
        Theme.Text(g, ColabText(a), Num, ink, new RectangleF(r.X - D(4), r.Y, r.Width + D(8), r.Height), StringAlignment.Center);
        Theme.Text(g, "Colab", TagFont, Theme.Dim, new RectangleF(cx - D(2), top + Ring, sl + D(4), TagH), StringAlignment.Center);
    }

    // 一个环：剩多少画多少，环里写剩几成（用完写 0）
    void PaintRing(Graphics g, QuotaAccount a, QuotaWindow w, Rectangle r)
    {
        bool full = w.Used >= 98;
        Color tone = ToneOf(a, w);
        float sw = D(2) + 0.5f;
        Arc(g, new RectangleF(r.X + sw / 2, r.Y + sw / 2, r.Width - sw, r.Height - sw), sw, full ? 0 : w.Left, tone);
        string v = full ? "0" : ((int)Math.Round(w.Left)).ToString();
        Theme.Text(g, v, Num, tone == Quota.Spent || tone == Quota.Hurry ? tone : Theme.Ink, new RectangleF(r.X - D(2), r.Y, r.Width + D(4), r.Height), StringAlignment.Center);
    }
}

internal sealed partial class HostForm
{
    QuotaStrip _quota;
    QuotaPanel _quotaPanel;
    Timer _quotaTick;

    void CreateQuotaStrip()
    {
        _quota = new QuotaStrip(this);
        _quota.Font = Font;
        _quota.Click += delegate { ShowQuota(); };
        Controls.Add(_quota);
        Quota.Load();
        SyncQuotaTip();
        _quotaTick = new Timer();
        _quotaTick.Interval = 10 * 60 * 1000;
        _quotaTick.Tick += delegate { RefreshQuota(); };
        _quotaTick.Start();
        // 开起来先等一会儿再读：别跟启动各家客户端挤在一起
        var first = new Timer { Interval = 8000 };
        first.Tick += delegate { first.Stop(); first.Dispose(); RefreshQuota(); };
        first.Start();
    }

    void RefreshQuota()
    {
        Quota.Refresh(this, delegate
        {
            SyncQuotaTip();
            Relayout(false);
            if (_quota != null) _quota.Invalidate();
            if (_quotaPanel != null && !_quotaPanel.IsDisposed) _quotaPanel.Reflow();
        });
    }

    // 面板里藏掉一家后：顶栏那一条和悬停提示马上跟上
    void QuotaChanged()
    {
        SyncQuotaTip();
        Relayout(false);
        if (_quota != null) _quota.Invalidate();
    }

    // 悬停提示：每家一行，跟顶栏同一句话；没数时说一声在读
    void SyncQuotaTip()
    {
        if (_quota == null) return;
        var sb = new StringBuilder();
        foreach (QuotaAccount a in QuotaStrip.Shown()) sb.Append(QuotaStrip.Line(a)).Append('\n');
        sb.Append(L.T("点开看明细和每月花费"));
        _tipText[_quota] = sb.ToString();
        _tips.SetToolTip(_quota, sb.ToString());
    }

    void ShowQuota()
    {
        if (_quotaPanel != null && !_quotaPanel.IsDisposed) { _quotaPanel.Close(); return; }
        _menuOpen = true;
        IntPtr giveBack = _lastFocused;
        var p = new QuotaPanel(this);
        _quotaPanel = p;
        p.FormClosed += delegate
        {
            _menuOpen = false;
            _quotaPanel = null;
            GiveBackAfterPopup(giveBack);
        };
        p.Location = _quota.PointToScreen(new Point(0, _quota.Height + D(6)));
        p.Reflow();
        p.Show(this);
        p.Activate();
        RefreshQuota();   // 点开就读一次新的，读完面板自己重排
    }

    // 明细面板：跟 Agent 名单同一种弹出面板。每家一张卡：名字、套餐、月费，下面每个限额一行（小圆 + 剩多少 + 什么时候重置）
    internal sealed class QuotaPanel : Form, IHostPopup
    {
        readonly HostForm _host;
        sealed class Card { public Rectangle R; public QuotaAccount A; public Bitmap Icon; }
        sealed class Item { public Rectangle R; public string What; public QuotaAccount A; public Bitmap Icon; }
        readonly List<Card> _cards = new List<Card>();
        readonly List<Item> _items = new List<Item>();
        Item _hot;
        bool _closing, _escDown;

        int D(int v) { return _host.D(v); }
        int Pad { get { return D(16); } }
        int W { get { return D(640); } }
        int RowH { get { return D(30); } }

        public QuotaPanel(HostForm host)
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
        public void DismissForSlotClick() { if (!_closing && !IsDisposed) Close(); }   // 点进了某一格（HostForm.Popups.cs）
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

        // 顺序：该赶紧花的在前，然后还有的，用完的，最后读不到数的
        static int Rank(QuotaAccount a) { return a.Head == null && a.Balance < 0 ? 3 : a.Hurry ? 0 : a.Exhausted ? 2 : 1; }

        public void Reflow()
        {
            _cards.Clear();
            _items.Clear();
            _hot = null;
            var list = QuotaStrip.Shown();
            list.Sort(delegate(QuotaAccount x, QuotaAccount y)
            {
                int c = Rank(x).CompareTo(Rank(y));
                return c != 0 ? c : list.IndexOf(x).CompareTo(list.IndexOf(y));
            });
            int y = D(48);
            foreach (QuotaAccount a in list)
            {
                int rows = 0;
                foreach (QuotaWindow w in a.Windows) if (w.Used >= 0) rows++;
                if (a.Balance >= 0) rows++;
                if (a.ColabUnits >= 0) rows++;
                bool sub = !string.IsNullOrEmpty(a.Error) || a.Manual && rows == 0 || !string.IsNullOrEmpty(a.Note);
                int h = D(12) + D(26) + rows * RowH + (sub ? D(22) : 0) + D(10);
                var r = new Rectangle(Pad, y, W - Pad * 2, h);
                AgentKind k = Catalog.ById(string.IsNullOrEmpty(a.Icon) ? a.Id : a.Icon);
                _cards.Add(new Card { R = r, A = a, Icon = k != null ? AgentIcons.Get(k, D(22)) : null });
                int bx = r.Right - D(10);
                if (!string.IsNullOrEmpty(a.Url))
                {
                    string t = L.T("看用量");
                    int bw = TextRenderer.MeasureText(t, Font).Width + D(20);
                    bx -= bw;
                    _items.Add(new Item { R = new Rectangle(bx, r.Y + D(9), bw, D(28)), What = "open", A = a });
                    bx -= D(6);
                }
                {
                    // 「隐藏」：暂时不用的那家藏到底下，以后再订了点一下放回来（不用开记事本）
                    int hw = TextRenderer.MeasureText(L.T("隐藏"), Font).Width + D(20);
                    _items.Add(new Item { R = new Rectangle(bx - hw, r.Y + D(9), hw, D(28)), What = "hide", A = a });
                }
                y += h + D(10);
            }
            // 藏起来的几家：一行「已隐藏」后面每家一个签，点了放回来
            List<QuotaAccount> hidden = Quota.Hidden();
            _hiddenY = -1;
            if (hidden.Count > 0)
            {
                _hiddenY = y;
                int hx = Pad + TextRenderer.MeasureText(L.T("已隐藏"), Font).Width + D(12), hy = y;
                foreach (QuotaAccount h in hidden)
                {
                    AgentKind k = Catalog.ById(string.IsNullOrEmpty(h.Icon) ? h.Id : h.Icon);
                    int cw = D(10) + (k != null ? D(18) + D(6) : 0) + TextRenderer.MeasureText(Chip(h), Font).Width + D(12);
                    if (hx + cw > W - Pad && hx > Pad + D(80)) { hx = Pad + TextRenderer.MeasureText(L.T("已隐藏"), Font).Width + D(12); hy += D(36); }
                    _items.Add(new Item { R = new Rectangle(hx, hy, cw, D(30)), What = "unhide", A = h, Icon = k != null ? AgentIcons.Get(k, D(18)) : null });
                    hx += cw + D(8);
                }
                y = hy + D(30) + D(10);
            }
            int foot = y + D(6);
            string edit = L.T("改账户"), again = L.T("↻ 重新读");
            int ew = TextRenderer.MeasureText(edit, Font).Width + D(20), aw = TextRenderer.MeasureText(again, Font).Width + D(20);
            _items.Add(new Item { R = new Rectangle(Pad - D(6), foot + D(8), ew, D(30)), What = "edit" });
            _items.Add(new Item { R = new Rectangle(Pad - D(6) + ew + D(8), foot + D(8), aw, D(30)), What = "refresh" });
            _items.Add(new Item { R = new Rectangle(W - Pad - D(32) + D(6), D(8), D(32), D(32)), What = "close" });
            _footY = foot;
            ClientSize = new Size(W, foot + D(8) + D(30) + D(12));
            Rectangle work = Screen.FromPoint(Location).WorkingArea;
            if (Bottom > work.Bottom) Top = Math.Max(work.Top, work.Bottom - Height);
            if (Right > work.Right) Left = Math.Max(work.Left, work.Right - Width);
            Invalidate();
        }
        int _footY, _hiddenY = -1;
        // 「显示 Cursor」；因为读到没订阅才收起来的后面带一句，跟手动藏的分得开
        static string Chip(QuotaAccount a) { return L.F("显示 {0}", a.Name) + (a.Inactive ? L.T("（没订阅）") : ""); }

        Item Hit(Point p) { foreach (Item it in _items) if (it.R.Contains(p)) return it; return null; }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            Item h = Hit(e.Location);
            if (h != _hot) { _hot = h; Cursor = h != null ? Cursors.Hand : Cursors.Default; Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            Item it = Hit(e.Location);
            if (it == null) return;
            switch (it.What)
            {
                case "close": Close(); break;
                case "open":
                    try { Process.Start(new ProcessStartInfo(it.A.Url) { UseShellExecute = true }); } catch { }
                    break;
                case "edit":
                    // 用记事本开：这份是 JSON，藏掉不想看的那家（"hidden": true）
                    try
                    {
                        if (!File.Exists(Quota.AccountsPath)) { _host.RefreshQuota(); break; }
                        Process.Start(new ProcessStartInfo("notepad.exe", "\"" + Quota.AccountsPath + "\"") { UseShellExecute = true });
                        Close();
                    }
                    catch { }
                    break;
                case "refresh": _host.RefreshQuota(); Invalidate(); break;
                case "hide":
                    if (Quota.SetHidden(it.A.Id, true)) { _host.QuotaChanged(); Reflow(); }
                    break;
                case "unhide":
                    // 放回来的那家要等脚本读一遍才有数：标题那里显示「正在读…」，读完面板自己重排
                    if (Quota.SetHidden(it.A.Id, false)) { _host.QuotaChanged(); Reflow(); }
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
                Theme.Text(g, L.T("订阅额度"), title, Theme.Ink, new RectangleF(Pad, 0, D(200), D(46)), StringAlignment.Near);
                string when = Quota.Running ? L.T("正在读…")
                            : Quota.Updated > 0 ? L.F("{0} 读的", Quota.At(Quota.Updated)) : L.T("还没读过");
                // 跟着标题实际多宽往后排（英文「Subscription quota」比 90 宽，原来叠在一起）
                int tx0 = Pad + Math.Max(D(90), TextRenderer.MeasureText(L.T("订阅额度"), title).Width + D(16));
                Theme.Text(g, when, small, Theme.Dim, new RectangleF(tx0, 0, W - Pad - D(40) - tx0, D(46)), StringAlignment.Near);
                if (_cards.Count == 0)
                    Theme.Text(g, L.T("还没有数，正在读…"), Font, Theme.Dim, new RectangleF(Pad, D(48), W - Pad * 2, D(30)), StringAlignment.Near);
                foreach (Card c in _cards) PaintCard(g, c, bold, small);

                if (_hiddenY >= 0)
                    Theme.Text(g, L.T("已隐藏"), Font, Theme.Dim, new RectangleF(Pad, _hiddenY, D(120), D(30)), StringAlignment.Near);
                foreach (Item it in _items)
                {
                    if (it.What != "unhide") continue;
                    using (GraphicsPath gp = Theme.Round(it.R, D(15)))
                    using (var br = new SolidBrush(it == _hot ? ShellStyles.Hover : Color.Transparent))
                    using (var pen = new Pen(ShellStyles.Border))
                    { g.FillPath(br, gp); g.DrawPath(pen, gp); }
                    int ix = it.R.X + D(10);
                    if (it.Icon != null) { Theme.Icon(g, it.Icon, new Rectangle(ix, it.R.Y + (it.R.Height - D(18)) / 2, D(18), D(18)), 0, D(4)); ix += D(18) + D(6); }
                    Theme.Text(g, Chip(it.A), Font, Theme.Ink, new RectangleF(ix, it.R.Y, it.R.Right - ix, it.R.Height), StringAlignment.Near);
                }

                // 底下：一条线 + 两个入口（月费不在这里管：用户 2026-10-27 说有学习月报管）
                using (var pen = new Pen(ShellStyles.Border)) g.DrawLine(pen, Pad, _footY, W - Pad, _footY);
                foreach (Item it in _items)
                {
                    if (it.What == "open" || it.What == "hide" || it.What == "unhide") continue;
                    if (it == _hot)
                        using (GraphicsPath gp = Theme.Round(it.R, D(8)))
                        using (var br = new SolidBrush(ShellStyles.Hover))
                            g.FillPath(br, gp);
                    if (it.What == "close") { Theme.Text(g, "✕", Font, Theme.Dim, it.R, StringAlignment.Center); continue; }
                    string t = it.What == "edit" ? L.T("改账户") : L.T("↻ 重新读");
                    Theme.Text(g, t, Font, Theme.Brand, new RectangleF(it.R.X + D(6), it.R.Y, it.R.Width - D(6), it.R.Height), StringAlignment.Near);
                }
            }
        }

        void PaintCard(Graphics g, Card c, Font bold, Font small)
        {
            QuotaAccount a = c.A;
            Rectangle r = c.R;
            Color tone = Quota.Tone(a);
            using (GraphicsPath gp = Theme.Round(r, D(8)))
            using (var br = new SolidBrush(Color.FromArgb(Theme.Light ? 8 : 20, Theme.Light ? Color.Black : Color.White)))
            using (var pen = new Pen(ShellStyles.Border))
            { g.FillPath(br, gp); g.DrawPath(pen, gp); }

            // 头一行：图标 名字 套餐 …… 右边一个状态签 + 「看用量」
            int x = r.X + D(12), top = r.Y + D(10);
            if (c.Icon != null) { Theme.Icon(g, c.Icon, new Rectangle(x, top + D(2), D(22), D(22)), D(2), D(4)); x += D(22) + D(8); }
            int nw = TextRenderer.MeasureText(a.Name, bold).Width;
            Theme.Text(g, a.Name, bold, Theme.Ink, new RectangleF(x, top, nw + D(4), D(26)), StringAlignment.Near);
            x += nw + D(6);
            var meta = new List<string>();
            if (!string.IsNullOrEmpty(a.Plan)) meta.Add(a.Plan);
            if (a.RenewDay > 0) meta.Add(L.F("每月 {0} 号续费", a.RenewDay));
            int right = r.Right - D(12);
            foreach (Item it in _items) if (it.A == a && (it.What == "open" || it.What == "hide")) right = Math.Min(right, it.R.X - D(8));

            string tag = a.Manual && a.Windows.Count == 0 ? null : a.Exhausted ? L.T("用完了") : a.Hurry ? L.T("快清零，赶紧用") : null;
            if (tag != null)
            {
                int tw = TextRenderer.MeasureText(tag, small).Width + D(14);
                var tr = new Rectangle(right - tw, top + D(3), tw, D(20));
                using (GraphicsPath gp = Theme.Round(tr, D(10)))
                using (var br = new SolidBrush(Color.FromArgb(a.Exhausted ? 30 : 40, tone)))
                    g.FillPath(br, gp);
                Theme.Text(g, tag, small, tone, tr, StringAlignment.Center);
                right = tr.X - D(8);
            }
            if (meta.Count > 0)
                Theme.Text(g, string.Join(" · ", meta), small, Theme.Dim, new RectangleF(x, top, Math.Max(0, right - x), D(26)), StringAlignment.Near);

            foreach (Item it in _items)
                if (it.A == a && it.What == "open")
                {
                    using (GraphicsPath gp = Theme.Round(it.R, D(6)))
                    using (var br = new SolidBrush(it == _hot ? ShellStyles.Hover : Color.Transparent))
                    using (var pen = new Pen(ShellStyles.Border))
                    { g.FillPath(br, gp); g.DrawPath(pen, gp); }
                    Theme.Text(g, L.T("看用量"), small, Theme.Ink, it.R, StringAlignment.Center);
                }
                else if (it.A == a && it.What == "hide")
                {
                    // 不加框、灰字：次要动作，悬停才有底色
                    if (it == _hot)
                        using (GraphicsPath gp = Theme.Round(it.R, D(6)))
                        using (var br = new SolidBrush(ShellStyles.Hover))
                            g.FillPath(br, gp);
                    Theme.Text(g, L.T("隐藏"), small, Theme.Dim, it.R, StringAlignment.Center);
                }

            // 每个限额一行：名字 | 一个小圆（剩多少，跟顶栏一样）| 剩多少 · 什么时候重置
            int y = top + D(30);
            int lx = r.X + D(12), lw = D(a.Windows.Exists(delegate(QuotaWindow w) { return !string.IsNullOrEmpty(w.Group); }) ? 180 : 110), cx = lx + lw, tx = cx + D(20) + D(10);
            foreach (QuotaWindow w in a.Windows)
            {
                if (w.Used < 0) continue;
                Theme.Text(g, w.DisplayLabel, Font, Theme.Dim, new RectangleF(lx, y, lw - D(6), RowH), StringAlignment.Near);
                bool full = w.Used >= 98;
                Color wc = w.Minor && !full && w.Used < Quota.LowAt ? Theme.Dim : QuotaStrip.ToneOf(a, w);
                float sw = D(3);
                QuotaStrip.Arc(g, new RectangleF(cx + sw / 2, y + (RowH - D(20)) / 2f + sw / 2, D(20) - sw, D(20) - sw), sw, full ? 0 : w.Left, wc);
                string s = full ? L.T("用完") : L.F("剩 {0}%", (int)Math.Round(w.Left));
                if (w.ResetsAt > 0) s += " · " + L.F("{0} 重置（{1}后）", Quota.At(w.ResetsAt), Quota.Until(w.ResetsAt));
                Theme.Text(g, s, Font, w.Used >= Quota.LowAt ? Quota.Spent : Theme.Ink, new RectangleF(tx, y, r.Right - D(12) - tx, RowH), StringAlignment.Near);
                y += RowH;
            }
            if (a.Balance >= 0)
            {
                Theme.Text(g, L.T("余额"), Font, Theme.Dim, new RectangleF(lx, y, lw - D(6), RowH), StringAlignment.Near);
                float sw = D(3);
                QuotaStrip.Arc(g, new RectangleF(cx + sw / 2, y + (RowH - D(20)) / 2f + sw / 2, D(20) - sw, D(20) - sw), sw, a.Exhausted ? 0 : 100, a.Exhausted ? Quota.Spent : Quota.Good);
                string s = Quota.Money(a.Balance, a.BalanceCurrency);
                if (a.Granted > 0 || a.ToppedUp > 0) s += "  " + L.F("（赠送 {0} · 充值 {1}）", Quota.Money(a.Granted, a.BalanceCurrency), Quota.Money(a.ToppedUp, a.BalanceCurrency));
                Theme.Text(g, s, Font, a.Exhausted ? Quota.Spent : Theme.Ink, new RectangleF(tx, y, r.Right - D(12) - tx, RowH), StringAlignment.Near);
                y += RowH;
            }
            if (a.ColabUnits >= 0)
            {
                Theme.Text(g, L.T("Colab 计算单元"), Font, Theme.Dim, new RectangleF(lx, y, lw - D(6), RowH), StringAlignment.Near);
                float sw = D(3);
                Color ct = a.ColabUnits < 1 ? Quota.Spent : a.ColabStale ? Theme.Dim : Quota.Good;
                QuotaStrip.Arc(g, new RectangleF(cx + sw / 2, y + (RowH - D(20)) / 2f + sw / 2, D(20) - sw, D(20) - sw), sw, a.ColabUnits < 1 ? 0 : 100, ct);
                string s = L.F("剩 {0} 单元", QuotaStrip.Units(a.ColabUnits));
                if (a.ColabRate >= 0) s += " · " + L.F("每小时用 {0}", a.ColabRate.ToString("0.00"));
                if (a.ColabStale && a.ColabAt > 0) s += " · " + L.F("{0} 读的", Quota.At(a.ColabAt));
                Theme.Text(g, s, Font, a.ColabUnits < 1 ? Quota.Spent : Theme.Ink, new RectangleF(tx, y, r.Right - D(12) - tx, RowH), StringAlignment.Near);
                y += RowH;
            }
            string line = !string.IsNullOrEmpty(a.Error)
                ? L.F("这次没读到：{0}", a.Error) + (a.At > 0 ? L.F("（上面是 {0} 的数）", Quota.At(a.At)) : "")
                : a.Manual && a.Windows.Count == 0 ? L.T("没有接口可读，点「看用量」去官网看")
                : a.Note;
            if (!string.IsNullOrEmpty(line))
                Theme.Text(g, line, small, Theme.Dim, new RectangleF(lx, y, r.Width - D(24), D(22)), StringAlignment.Near);
        }
    }
}
