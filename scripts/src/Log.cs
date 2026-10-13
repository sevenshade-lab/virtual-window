// 【拆分】focus.log。（从原 AgentHost.cs 原样搬出，逻辑未改）

using System;
using System.IO;
using System.Text;

// 焦点相关的操作日志。查「点一下就失焦 / 弹窗一闪就没」只能靠它 ——
// 这类问题从截图上看不出是谁动的手。
internal static class Log
{
    static readonly object _lock = new object();

    public static string Path_()
    {
        string b = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        if (string.IsNullOrEmpty(b)) return null;
        return System.IO.Path.Combine(System.IO.Path.Combine(b, "AgentHost"), "focus.log");
    }

    public static void W(string line)
    {
        string f = Path_();
        if (f == null) return;
        try
        {
            lock (_lock)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(f));
                var fi = new FileInfo(f);
                // 满了留一份旧的（focus.log.1）：以前直接删，2026-09-27 查「Cursor 被谁关的」时记录已经没了。
                if (fi.Exists && fi.Length > 200 * 1024) { File.Copy(f, f + ".1", true); fi.Delete(); }
                File.AppendAllText(f, DateTime.Now.ToString("HH:mm:ss.fff") + "  " + line + "\n", Encoding.UTF8);
            }
        }
        catch { }
    }
}
