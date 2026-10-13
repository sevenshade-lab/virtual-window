# 把还挂在宿主名下、但宿主已经不认得的「孤儿」客户端窗口摘回成正常顶层窗口。
# 用法：先 -WhatIf 看要动谁，确认了再不带参数跑。
param([switch]$WhatIf)
Add-Type @"
using System; using System.Text; using System.Collections.Generic; using System.Runtime.InteropServices;
public class RS {
 public delegate bool CB(IntPtr h, IntPtr l);
 [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr p, CB cb, IntPtr l);
 [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
 [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
 [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint p);
 [DllImport("user32.dll")] public static extern IntPtr SetParent(IntPtr c, IntPtr p);
 [DllImport("user32.dll", EntryPoint="GetWindowLongPtr")] public static extern IntPtr GetL(IntPtr h, int n);
 [DllImport("user32.dll", EntryPoint="SetWindowLongPtr")] public static extern IntPtr SetL(IntPtr h, int n, IntPtr v);
 [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint f);
 [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int n);
 [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
 public static List<IntPtr> Kids(IntPtr root){
   var list = new List<IntPtr>();
   EnumChildWindows(root, delegate(IntPtr h, IntPtr l){
     var c = new StringBuilder(80); GetClassName(h, c, 80);
     if (c.ToString() != "Chrome_WidgetWin_1") return true;
     uint pid; GetWindowThreadProcessId(h, out pid);
     string pn = ""; try { pn = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; } catch { return true; }
     if (pn != "Cursor" && pn != "claude" && pn != "ChatGPT" && pn != "OpenCode") return true;
     list.Add(h); return true;
   }, IntPtr.Zero);
   return list;
 }
 public static string Name(IntPtr h){ uint pid; GetWindowThreadProcessId(h, out pid);
   var t = new StringBuilder(120); GetWindowText(h, t, 120);
   string pn=""; try { pn = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; } catch {}
   return pn + " '" + t + "'"; }
}
"@
[void][RS]::SetProcessDPIAware()
$WS_CHILD = 0x40000000; $WS_POPUP = [int64]0x80000000; $WS_OVERLAPPEDWINDOW = 0x00CF0000; $WS_VISIBLE = 0x10000000
$GWL_STYLE = -16; $GWL_EX = -20; $WS_EX_APPWINDOW = 0x00040000
$SWP = 0x0020 -bor 0x0040 -bor 0x0004   # FRAMECHANGED | SHOWWINDOW | NOZORDER

$hostP = Get-Process AgentHost -ErrorAction SilentlyContinue
if (-not $hostP) { "宿主没在跑，不用救"; exit }
$kids = [RS]::Kids($hostP.MainWindowHandle)
if ($kids.Count -eq 0) { "宿主名下没有客户端窗口"; exit }

$i = 0
foreach ($h in $kids) {
  $i++
  "{0}. {1}  hwnd={2:X}" -f $i, [RS]::Name($h), $h.ToInt64()
  if ($WhatIf) { continue }
  $st = [int64][RS]::GetL($h, $GWL_STYLE)
  $new = ($st -band (-bnot $WS_CHILD) -band (-bnot $WS_POPUP)) -bor $WS_OVERLAPPEDWINDOW -bor $WS_VISIBLE
  [void][RS]::SetL($h, $GWL_STYLE, [IntPtr]$new)
  [void][RS]::SetParent($h, [IntPtr]::Zero)
  $ex = [int64][RS]::GetL($h, $GWL_EX)
  [void][RS]::SetL($h, $GWL_EX, [IntPtr]($ex -bor $WS_EX_APPWINDOW))
  $x = 80 + $i * 40; $y = 60 + $i * 40
  [void][RS]::SetWindowPos($h, [IntPtr]::Zero, $x, $y, 1200, 800, $SWP)
  [void][RS]::ShowWindow($h, 1)
  "   已摘回成独立窗口"
}
