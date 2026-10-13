// 【拆分】prefs.txt：宿主自己的开关。（从原 AgentHost.cs 原样搬出，逻辑未改）

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;

// 宿主自己的开关，存 %LOCALAPPDATA%\AgentHost\prefs.txt（一行一个 key=value）。
// 不写进 config.json：那个是用户维护的文件，宿主只读它。
internal static class Prefs
{
    static readonly Dictionary<string, string> _kv = new Dictionary<string, string>();
    static bool _loaded;

    static string Path_()
    {
        string b = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        if (string.IsNullOrEmpty(b)) return null;
        return System.IO.Path.Combine(System.IO.Path.Combine(b, "AgentHost"), "prefs.txt");
    }

    static void Load()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            string f = Path_();
            if (f == null || !File.Exists(f)) return;
            foreach (string line in File.ReadAllLines(f))
            {
                int i = line.IndexOf('=');
                if (i > 0) _kv[line.Substring(0, i).Trim()] = line.Substring(i + 1).Trim();
            }
        }
        catch { }
    }

    public static bool Get(string key, bool dflt)
    {
        Load();
        string v;
        return _kv.TryGetValue(key, out v) ? v == "1" : dflt;
    }

    public static string GetStr(string key, string dflt)
    {
        Load();
        string v;
        return _kv.TryGetValue(key, out v) ? v : dflt;
    }

    public static int GetInt(string key, int dflt)
    {
        int v;
        return int.TryParse(GetStr(key, ""), out v) ? v : dflt;
    }

    public static Rectangle GetRect(string key)
    {
        string[] p = GetStr(key, "").Split(',');
        int x, y, w, h;
        if (p.Length == 4 && int.TryParse(p[0], out x) && int.TryParse(p[1], out y)
            && int.TryParse(p[2], out w) && int.TryParse(p[3], out h))
            return new Rectangle(x, y, w, h);
        return Rectangle.Empty;
    }

    public static void SetInt(string key, int v) { SetStr(key, v.ToString()); }

    public static void SetRect(string key, Rectangle r)
    {
        SetStr(key, r.X + "," + r.Y + "," + r.Width + "," + r.Height);
    }

    public static void SetStr(string key, string value)
    {
        Load();
        _kv[key] = value ?? "";
        if (!_hold) Save();
    }

    // 连着改好几项时先 Hold，改完 Release 写一次盘（存窗口位置要改两三项）。
    static bool _hold;
    public static void Hold() { Load(); _hold = true; }
    public static void Release() { _hold = false; Save(); }

    public static void Set(string key, bool on)
    {
        SetStr(key, on ? "1" : "0");
    }

    static void Save()
    {
        try
        {
            string f = Path_();
            if (f == null) return;
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(f));
            var sb = new StringBuilder();
            foreach (var p in _kv) sb.Append(p.Key).Append('=').Append(p.Value).Append('\n');
            File.WriteAllText(f, sb.ToString(), Encoding.UTF8);
        }
        catch { }
    }
}
