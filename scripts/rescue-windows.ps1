# 把被 AgentHost 改坏样式的客户端主窗口恢复成正常顶层窗口。
# 只动 Cursor / claude / ChatGPT(Codex) / OpenCode 四家、类名 Chrome_WidgetWin_1、
# 可见、且没有标题栏(WS_CAPTION)的窗口。其它一律不碰。
Add-Type @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public class R32 {
  public delegate bool CB(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(CB cb, IntPtr l);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr h, StringBuilder sb, int n);
  [DllImport("user32.dll")] public static extern int GetClassName(IntPtr h, StringBuilder sb, int n);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern IntPtr GetParent(IntPtr h);
  [DllImport("user32.dll")] public static extern IntPtr SetParent(IntPtr c, IntPtr p);
  [DllImport("user32.dll", EntryPoint="GetWindowLongPtr")] public static extern IntPtr GetWindowLongPtr(IntPtr h, int n);
  [DllImport("user32.dll", EntryPoint="SetWindowLongPtr")] public static extern IntPtr SetWindowLongPtr(IntPtr h, int n, IntPtr v);
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint f);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int n);
}
"@
$WS_OVERLAPPEDWINDOW = 0x00CF0000
$WS_CHILD   = 0x40000000
$WS_POPUP   = 0x80000000
$WS_VISIBLE = 0x10000000
$WS_CAPTION = 0x00C00000
$WS_EX_APPWINDOW = 0x00040000
$SWP_FRAMECHANGED = 0x0020; $SWP_SHOWWINDOW = 0x0040; $SWP_NOZORDER = 0x0004
$fixed = New-Object System.Collections.ArrayList
$cb = [R32+CB]{
  param($h,$l)
  if(-not [R32]::IsWindowVisible($h)){ return $true }
  $cn = New-Object System.Text.StringBuilder 256
  [void][R32]::GetClassName($h,$cn,256)
  if($cn.ToString() -ne 'Chrome_WidgetWin_1'){ return $true }
  $wpid=[uint32]0; [void][R32]::GetWindowThreadProcessId($h,[ref]$wpid)
  $proc=''
  try { $proc=(Get-Process -Id $wpid -ErrorAction Stop).ProcessName } catch { return $true }
  if($proc -notmatch '^(Cursor|claude|ChatGPT|OpenCode)$'){ return $true }
  $style=[R32]::GetWindowLongPtr($h,-16).ToInt64()
  $hasCaption = ($style -band $WS_CAPTION) -eq $WS_CAPTION
  $isChild = ($style -band $WS_CHILD) -ne 0
  $parent=[R32]::GetParent($h)
  if($hasCaption -and -not $isChild -and $parent -eq [IntPtr]::Zero){ return $true }   # 本来就正常
  $sb = New-Object System.Text.StringBuilder 512
  [void][R32]::GetWindowText($h,$sb,512)
  # 先清 WS_CHILD、补 WS_POPUP 不要(顶层用 OVERLAPPED)，再 SetParent(0)，最后刷框架
  $new = ($style -band (-bnot $WS_CHILD)) -band (-bnot $WS_POPUP)
  $new = $new -bor $WS_OVERLAPPEDWINDOW -bor $WS_VISIBLE
  [void][R32]::SetWindowLongPtr($h,-16,[IntPtr]$new)
  $ex=[R32]::GetWindowLongPtr($h,-20).ToInt64()
  [void][R32]::SetWindowLongPtr($h,-20,[IntPtr]($ex -bor $WS_EX_APPWINDOW))
  if($parent -ne [IntPtr]::Zero){ [void][R32]::SetParent($h,[IntPtr]::Zero) }
  [void][R32]::SetWindowPos($h,[IntPtr]::Zero,120,80,1280,800,($SWP_FRAMECHANGED -bor $SWP_SHOWWINDOW -bor $SWP_NOZORDER))
  [void][R32]::ShowWindow($h,9)
  [void]$fixed.Add([PSCustomObject]@{Proc=$proc;Title=$sb.ToString();Handle=('0x{0:X}' -f $h.ToInt64());OldStyle=('0x{0:X}' -f $style);Parent=('0x{0:X}' -f $parent.ToInt64())})
  return $true
}
[void][R32]::EnumWindows($cb,[IntPtr]::Zero)
if($fixed.Count -eq 0){ Write-Host 'No broken client window found.' } else { $fixed | Format-Table -AutoSize }
