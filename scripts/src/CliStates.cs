using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

// CLI 会话在干什么：靠各家 CLI 自己的钩子告诉我们（桌面客户端的状态也来自钩子，见 HookStates）。
//
// 旧接口：%LOCALAPPDATA%\AgentHost\cli\<格子 id>.txt，内容是状态一个词。
// 现在的主来源是全局钩子写的会话文件（HookStates，按 AGENTHOST_SLOT 对上格子）；
// agenthost-hook.cjs 仍顺手写这个文件，旧版起的会话也还靠 `AgentHost.exe --cli-state` 写它。
internal static class CliStates
{
    public static string Dir()
    {
        string b = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        if (string.IsNullOrEmpty(b)) return null;
        return Path.Combine(Path.Combine(b, "AgentHost"), "cli");
    }

    static string Safe(string id)
    {
        var sb = new StringBuilder();
        foreach (char c in id ?? "")
            sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_');
        return sb.ToString();
    }

    // 钩子进程里调：只认这三个词，别的忽略
    public static void Write(string state)
    {
        try
        {
            if (state != "working" && state != "asking" && state != "done" && state != "stopped") return;
            string slot = Environment.GetEnvironmentVariable("AGENTHOST_SLOT");
            string dir = Dir();
            if (string.IsNullOrEmpty(slot) || dir == null) return;
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, Safe(slot) + ".txt"), state, Encoding.ASCII);
        }
        catch { }
    }

    // ---- 宿主这边读 ----
    static readonly Dictionary<string, string> _state = new Dictionary<string, string>();
    static readonly Dictionary<string, DateTime> _since = new Dictionary<string, DateTime>();

    // 心跳里调（约 2 秒一次）。只看这几个小文件的修改时间，没变不读。
    public static void Refresh(IEnumerable<string> ids)
    {
        string dir = Dir();
        if (dir == null) return;
        foreach (string id in ids)
        {
            string f = Path.Combine(dir, Safe(id) + ".txt");
            try
            {
                var fi = new FileInfo(f);
                if (!fi.Exists) { _state.Remove(id); _since.Remove(id); continue; }
                DateTime t = fi.LastWriteTime;
                DateTime old;
                if (_since.TryGetValue(id, out old) && old == t) continue;
                _since[id] = t;
                _state[id] = File.ReadAllText(f, Encoding.ASCII).Trim();
            }
            catch { }
        }
    }

    // 给状态灯 / 队列用：working / done / stopped；没有钩子数据就是 null（跟桌面客户端没状态时一样处理）
    public static string StateOf(string id)
    {
        string v;
        return _state.TryGetValue(id, out v) ? v : null;
    }

    public static int SecsOf(string id)
    {
        DateTime t;
        return _since.TryGetValue(id, out t) ? (int)Math.Max(0, (DateTime.Now - t).TotalSeconds) : 0;
    }

    // 新会话开始前清掉同名的旧状态，免得上一回的 done 让新会话一开就排进队列
    public static void Reset(string id)
    {
        try
        {
            string dir = Dir();
            if (dir == null) return;
            string f = Path.Combine(dir, Safe(id) + ".txt");
            if (File.Exists(f)) File.Delete(f);
        }
        catch { }
        _state.Remove(id);
        _since.Remove(id);
    }
}
