// 【拆分】seen.txt：你上次点进每一格的时间。（从原 AgentHost.cs 原样搬出，逻辑未改）

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

// 「你上次点进这一格是什么时候」。存盘的理由：黄灯（做完了你还没看）要跨重启，
// 不然换一次版、重启一次宿主，所有黄灯就莫名其妙全灭了。
// 第一次运行没有这个文件时，把所有格子标成"刚看过" —— 否则一上来满屏黄灯。
internal static class Seen
{
    static readonly Dictionary<string, DateTime> _at = new Dictionary<string, DateTime>();
    static bool _loaded;

    static string Path_()
    {
        string b = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        if (string.IsNullOrEmpty(b)) return null;
        return System.IO.Path.Combine(System.IO.Path.Combine(b, "AgentHost"), "seen.txt");
    }

    public static void Load()
    {
        if (_loaded) return;
        _loaded = true;
        string f = Path_();
        try
        {
            if (f == null || !File.Exists(f))
            {
                _at["*"] = DateTime.Now;        // 第一次跑：当作什么都看过
                return;
            }
            foreach (string line in File.ReadAllLines(f))
            {
                string[] p = line.Split('\t');
                long t;
                if (p.Length == 2 && long.TryParse(p[1], out t)) _at[p[0]] = new DateTime(t);
            }
        }
        catch { }
    }

    public static DateTime Of(string id)
    {
        Load();
        DateTime v;
        if (_at.TryGetValue(id, out v)) return v;
        if (_at.TryGetValue("*", out v)) return v;   // 第一次跑时的兜底
        return DateTime.MinValue;
    }

    public static void Touch(string id)
    {
        Load();
        _at[id] = DateTime.Now;
        string f = Path_();
        if (f == null) return;
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(f));
            var sb = new StringBuilder();
            foreach (var kv in _at) sb.Append(kv.Key).Append('\t').Append(kv.Value.Ticks).Append('\n');
            File.WriteAllText(f, sb.ToString(), Encoding.UTF8);
        }
        catch { }
    }
}
