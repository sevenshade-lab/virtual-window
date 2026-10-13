// 【拆分】config.json（只读）。（从原 AgentHost.cs 原样搬出，逻辑未改）

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Windows.Forms;

internal static class Cfg
{
    public static string Workspace;

    // 每家各自的工作区。一个全局 workspace 对四家就是错的 —— 各家常常各有各的工作目录。
    // config.json 里写平铺的 "workspace.cursor" / "workspace.claude" …，
    // 没写就退回 "workspace"（自己这个 JSON 解析器读不了嵌套对象）。
    static readonly Dictionary<string, string> _ws = new Dictionary<string, string>();

    public static string WorkspaceFor(AgentKind k)
    {
        string v;
        if (k != null && !string.IsNullOrEmpty(k.Workspace)) return k.Workspace;
        if (k != null && _ws.TryGetValue(k.FamilyId, out v) && Directory.Exists(v)) return v;
        return Workspace;
    }

    // 这两个以前是摆设：Header 解析了、校验了范围，全文件零处引用（真正用的是
    // 写死的 D(44)）；Gap 被 max(Cfg.Gap, 18) 兜着，小于 18 的值一律无效。
    // 现在都真的生效了，默认值也从「悬浮卡片」那套宽松值收了一点 ——
    // 田字下宿主自己要吃掉 22.6% 的纵向空间，每家实拿 810×394 逻辑像素。
    public static int Gap = 12;
    public static int Header = 34;
    public static bool Cli;

    public static void Load()
    {
        Workspace = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        try
        {
            string dir = Path.GetDirectoryName(Application.ExecutablePath);
            string p = Path.Combine(dir, "..", "config.json");
            p = Path.GetFullPath(p);
            if (!File.Exists(p)) return;
            string json = File.ReadAllText(p, Encoding.UTF8);
            string ws = JsonStr(json, "workspace");
            if (!string.IsNullOrEmpty(ws) && Directory.Exists(ws)) Workspace = ws;
            // 注意 JsonStr 找的是带引号的 "workspace"，不会误命中 "workspace.cursor"
            for (int i = 0; i < Catalog.All.Length; i++)
            {
                string id = Catalog.All[i].Id;
                string w = JsonStr(json, "workspace." + id);
                if (!string.IsNullOrEmpty(w) && Directory.Exists(w)) _ws[id] = w;
            }
            int g = JsonInt(json, "gap");
            if (g >= 0 && g <= 40) Gap = g;
            int h = JsonInt(json, "header");
            if (h >= 24 && h <= 64) Header = h;
            string surface = JsonStr(json, "surface");
            Cli = string.Equals(surface, "cli", StringComparison.OrdinalIgnoreCase);
        }
        catch { }
    }

    static string JsonStr(string json, string key)
    {
        string k = "\"" + key + "\"";
        int i = json.IndexOf(k, StringComparison.Ordinal);
        if (i < 0) return "";
        i = json.IndexOf(':', i);
        if (i < 0) return "";
        i = json.IndexOf('"', i + 1);
        if (i < 0) return "";
        int j = i + 1;
        var sb = new StringBuilder();
        while (j < json.Length)
        {
            char c = json[j++];
            if (c == '"') break;
            if (c == '\\' && j < json.Length)
            {
                char n = json[j++];
                if (n == 'n') sb.Append('\n');
                else if (n == '\\') sb.Append('\\');
                else sb.Append(n);
            }
            else sb.Append(c);
        }
        return sb.ToString();
    }

    static int JsonInt(string json, string key)
    {
        string k = "\"" + key + "\"";
        int i = json.IndexOf(k, StringComparison.Ordinal);
        if (i < 0) return -1;
        i = json.IndexOf(':', i) + 1;
        while (i < json.Length && json[i] == ' ') i++;
        int j = i;
        while (j < json.Length && (char.IsDigit(json[j]) || json[j] == '-')) j++;
        int v;
        if (j > i && int.TryParse(json.Substring(i, j - i), out v)) return v;
        return -1;
    }
}
