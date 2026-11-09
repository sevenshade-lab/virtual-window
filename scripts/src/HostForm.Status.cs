// 【拆分】外部接口：--claim 转进来的纳入、status.json。（从原 AgentHost.cs 原样搬出，逻辑未改）

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows.Forms;

internal sealed partial class HostForm
{
    public void ClaimFromOutside()
    {
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Maximized;
        if (_restoreOnFirstClaim)
        { _restoreOnFirstClaim = false; Attach(HostWins.Visible()); Relayout(true); }
        else ClaimRunning(true);
    }

    // 给外面的程序看的状态：%LOCALAPPDATA%\AgentHost\status.json。
    // 只在 Relayout 时写 —— 心跳已经改成状态变了才 Relayout，不会每 0.7 秒写盘。
    public static string StatusPath
    {
        get
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AgentHost", "status.json");
        }
    }

    static string J(string v)
    {
        var sb = new StringBuilder("\"");
        foreach (char c in v ?? "")
        {
            if (c == '"' || c == '\\') sb.Append('\\').Append(c);
            else if (c < 0x20) sb.AppendFormat("\\u{0:x4}", (int)c);
            else sb.Append(c);
        }
        return sb.Append('"').ToString();
    }

    // 留言认得哪几家（写给留言的脚本看，见 NoteCounts.WritePeers）：格子里放着的，加上装了钩子的那几家 ——
    // 没放进格子、它自己开着的会话也收得到。光是装在电脑上、既没进格子又没钩子的不算：给它留了也没人看见。
    // 钩子装没装每次现查（读各家的配置文件，五个小文件）：只在格子有增减、装卸钩子之后才走到这里，
    // 记着不查的话，用命令行（--install-hooks）装的、启动那一下碰巧没读到的，就一直不在名单里。

    void SyncPeers()
    {
        try
        {
            var peers = new List<KeyValuePair<string, string>>();
            foreach (Slot s in _slots)
            {
                if (s.Custom) continue;
                AgentKind fam = Catalog.ById(s.Kind.FamilyId);
                peers.Add(new KeyValuePair<string, string>(s.Kind.FamilyId, fam != null ? fam.Name : s.Kind.Name));
            }
            foreach (AgentKind k in Catalog.All)
                if (k.DesktopState == "hooks" && HookSetup.Installed(k.Id) == true)
                    peers.Add(new KeyValuePair<string, string>(k.Id, k.Name));
            NoteCounts.WritePeers(peers);
        }
        catch { }
    }

    // 上一次写出去的内容（不含时间戳）。Relayout 每次都走到这里，而 SizeChanged
    // 在拖动改窗口大小时一秒来几十下 —— 以前就是一秒把 status.json 删掉重写几十次。
    // README 里写的是「只在布局变化时写」，得让它真是这样。
    string _statusLast;
    const string TsMark = "TS";

    void WriteStatus(bool running)
    {
        try
        {
            var sb = new StringBuilder();
            int n = _slots.Count;
            sb.Append("{\"running\":").Append(running ? "true" : "false");
            sb.Append(",\"pid\":").Append(Process.GetCurrentProcess().Id);
            sb.Append(",\"updated\":").Append(TsMark);
            sb.Append(",\"surface\":").Append(J(Surface()));
            sb.Append(",\"layout\":").Append(J(n > 0 ? Layouts.KeyAt(n, _layoutIx) : ""));
            sb.Append(",\"solo\":").Append(J(_solo != null ? _solo.Kind.Id : ""));
            sb.Append(",\"slots\":[");
            for (int i = 0; i < n; i++)
            {
                Slot s = _slots[i];
                if (i > 0) sb.Append(',');
                bool live = s.Hwnd != IntPtr.Zero && Native.IsWindow(s.Hwnd);
                sb.Append("{\"id\":").Append(J(s.Kind.Id))
                  .Append(",\"name\":").Append(J(s.Kind.Name))
                  .Append(",\"family\":").Append(J(s.Kind.FamilyId))
                  .Append(",\"surface\":").Append(J(s.Custom ? "window" : s.Kind.Cli ? "cli" : "desktop"))
                  .Append(",\"embedded\":").Append(running && s.Embedded && live ? "true" : "false")
                  .Append(",\"state\":").Append(J(running ? s.StateLbl.Text : "已还原"))
                  .Append(",\"agent\":").Append(J(running && s.AgentState != null ? s.AgentState : ""))
                  // 别的 agent 留给这一家、它还没标已读的话有几条（自己挑进来的窗口没有信箱）
                  .Append(",\"notes\":").Append(s.Custom ? 0 : NoteCounts.Unread(s.Kind.FamilyId))
                  .Append('}');
            }
            sb.Append("]}");
            // 时间戳留成占位符再比：不然每次都不一样，去重就没意义了。
            string body = sb.ToString();
            if (body == _statusLast) return;
            _statusLast = body;
            if (running) SyncPeers();   // 格子有增减才会走到这儿：顺手把留言认得的名单对一遍
            string json = body.Replace(TsMark, J(DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss")));
            string path = StatusPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, json, new UTF8Encoding(false));
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }
        catch { }
    }
}
