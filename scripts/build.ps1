#Requires -Version 5.1
param(
    [switch]$Run
)

$ErrorActionPreference = 'Stop'
$ScriptDir = $PSScriptRoot
$proj = Join-Path $ScriptDir 'AgentHost.csproj'
$src = Join-Path $ScriptDir 'src'
$hooks = Join-Path $ScriptDir 'hooks'
$exe = Join-Path $ScriptDir 'AgentHost.exe'
$man = Join-Path $ScriptDir 'AgentHost.manifest'
$ico = Join-Path $ScriptDir 'AgentHost.ico'
# 发布目录在 bin\ 下（.gitignore 已忽略）。发布时正在跑的宿主不受影响 ——
# 它占着的是 scripts\AgentHost.exe，不是这里。
$pubDir = Join-Path $ScriptDir 'bin\publish'
# 记着「现在这个 exe 是拿哪些输入编出来的」，判断要不要重编用（见 Test-NeedBuild）。
$stamp = Join-Path $ScriptDir 'bin\AgentHost.built'
# 发布参数（工程文件和输出目录另拼）。改了它 exe 也该重编，所以算进指纹。
$publishFlags = @('-c', 'Release', '--nologo')

# 收起正在跑的宿主：发 WM_APP_COLLAPSE(0x8052)，**不是** WM_CLOSE。
#
# 2026-09-20 起 WM_CLOSE 的语义是「连四家客户端一起关掉」（closeagents 默认开着），
# 而 CloseMainWindow() 发的正是 WM_CLOSE —— 等于改一行源码重编一次就把四家
# （加起来 7GB）全收了；有 agent 在跑时宿主还会弹模态框问你，这边只能干等超时。
# COLLAPSE 跟外部程序发来的「收起」是同一条路：四家还成独立窗口并最小化，进程留着。
$Collapse = 0x8052
Add-Type -Namespace AgentHostBuild -Name Win -MemberDefinition @'
[DllImport("user32.dll", CharSet = CharSet.Unicode)]
public static extern IntPtr FindWindowW(string cls, string title);
[DllImport("user32.dll")]
public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wp, IntPtr lp);
[DllImport("user32.dll")]
public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
'@

function Send-Collapse([System.Diagnostics.Process]$proc) {
    # 按标题找宿主的主窗口，再核一下是不是这个进程的（跟 verify-common.ps1 的 Get-HostWindow 一个找法）。
    # 以前先用 $proc.MainWindowHandle：.NET 给的是这个进程「第一个可见的顶层窗口」，宿主上正弹着悬停提示时
    # 那是提示框的影子（类名 SysShadow，没有标题），消息发过去没人理，宿主不退，12 秒后报「虚拟窗口还没退」
    #（2026-10-12 换版时撞上：新版编好了却换不上）。
    # 注意这里必须是 [NullString]::Value 而不是 $null —— PowerShell 把 $null 绑给 [string] 形参时给的是空串，
    # FindWindow 会去找类名为 "" 的窗口，永远找不到（实测返回 0，换成 NullString 立刻命中）。
    $h = [AgentHostBuild.Win]::FindWindowW([NullString]::Value, '虚拟窗口')
    if ($h -ne [IntPtr]::Zero) {
        $owner = [uint32]0
        $null = [AgentHostBuild.Win]::GetWindowThreadProcessId($h, [ref]$owner)
        if ($owner -ne [uint32]$proc.Id) { $h = [IntPtr]::Zero }
    }
    # 按标题没找到（窗口还没建好之类）才退回去用 .NET 给的那个
    if ($h -eq [IntPtr]::Zero) { $h = $proc.MainWindowHandle }
    if ($h -eq [IntPtr]::Zero) { return $false }
    return [AgentHostBuild.Win]::PostMessage($h, $Collapse, [IntPtr]::Zero, [IntPtr]::Zero)
}

function Quote-Arg([string]$s) {
    if ($s -match '\s') { return '"' + $s + '"' }
    return $s
}

function Get-Sha256([string]$path) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try { return [System.BitConverter]::ToString($sha.ComputeHash([System.IO.File]::ReadAllBytes($path))).Replace('-', '') }
    finally { $sha.Dispose() }
}

# 输入的指纹：每个文件的相对路径 + 内容的 SHA-256，加发布参数。
function Get-InputsMark {
    $skip = $ScriptDir.TrimEnd('\').Length + 1
    $lines = New-Object System.Collections.Generic.List[string]
    foreach ($f in $inputs) { $lines.Add($f.Substring($skip) + ' ' + (Get-Sha256 $f)) }
    $lines.Sort([System.StringComparer]::Ordinal)
    $lines.Add('publish ' + ($publishFlags -join ' '))
    return ($lines -join "`n")
}

# 记下「这个 exe + 这些输入」。写不进去（目录只读之类）不算失败：下次只是退回去比修改时间。
function Save-Stamp([string]$inputsMark) {
    try {
        $dir = Split-Path -Parent $stamp
        if (-not (Test-Path -LiteralPath $dir)) { $null = New-Item -ItemType Directory -Path $dir -Force }
        [System.IO.File]::WriteAllText($stamp, ('exe ' + (Get-Sha256 $exe) + "`n" + $inputsMark), (New-Object System.Text.UTF8Encoding($false)))
    } catch { }
}

# 需不需要重编：exe 是拿现在这些输入编出来的就不动（也就不会去收起宿主）。
#
# 比内容，不比修改时间。以前比时间，还把本脚本也算作输入，结果：
#   - 改一行 build.ps1（哪怕只动 -Run），所有人手上的 exe 都被判成旧的；
#   - git 切分支、拉新版会把文件时间刷成现在，内容一个字没变也判成旧的。
# 两种都是下一个跑本脚本的人无故重编一遍，把正在用的宿主收起来。
# 所以 touch 一下源码不再触发重编。要强制重编：收起宿主，删掉 scripts\AgentHost.exe 再跑。
function Test-NeedBuild([string]$inputsMark) {
    if (-not (Test-Path -LiteralPath $exe)) { return $true }
    $was = ''
    if (Test-Path -LiteralPath $stamp) { try { $was = [System.IO.File]::ReadAllText($stamp) } catch { } }
    $head = 'exe ' + (Get-Sha256 $exe) + "`n"
    if ($was.StartsWith($head, [System.StringComparison]::Ordinal)) {
        return ($was.Substring($head.Length) -cne $inputsMark)
    }
    # 没有记录，或者记录说的不是现在这个 exe（旧版脚本编的、bin\ 被清过、有人手动换过 exe）：
    # 只好比修改时间。判成不用编就把记录补上，以后都比内容。
    $builtAt = (Get-Item -LiteralPath $exe).LastWriteTimeUtc
    foreach ($f in $inputs) {
        if ((Get-Item -LiteralPath $f).LastWriteTimeUtc -gt $builtAt) { return $true }
    }
    Save-Stamp $inputsMark
    return $false
}

# -Run：经资源管理器拉起，不从这里直接起。
#
# 直接 Start-Process 起的宿主是本脚本的子进程，继承调用方终端的全部环境变量，再原样传给它
# 以后拉起的桌面端（Launch.cs 走 ShellExecute）。2026-10-12 查实：从 agent 的终端跑会多带
# 几十个，里面有会话令牌、用户邮箱、ANTHROPIC_BASE_URL、NO_COLOR、GIT_ASKPASS。
# 交给 explorer.exe，宿主的父进程是资源管理器的工厂进程（explorer.exe /factory,… -Embedding，
# 系统替它起的），环境跟从开始菜单打开的一样，工作目录是 system32。
#
# 资源管理器拉不起来就报错停下，**不退回直接启动** —— 那等于悄悄把上面的问题放回来。
function Start-ViaExplorer([string]$path, [int]$timeoutSeconds = 20) {
    # 文件不在的话 explorer 不报错，而是打开「文档」文件夹。
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw ('没有 ' + $path + '，虚拟窗口没有打开。') }
    $explorer = Join-Path ([System.Environment]::GetFolderPath('Windows')) 'explorer.exe'
    # 路径一律加引号，不加的话空格会把它截断。
    # （实测加了引号之后，中文、逗号、括号、& = ; # % 都能认。）
    try {
        $null = Start-Process -FilePath $explorer -ArgumentList ('"' + $path + '"')
    } catch {
        throw ('没能启动资源管理器（' + $explorer + '）：' + $_.Exception.Message + ' 虚拟窗口没有打开。')
    }
    # explorer.exe 把请求转交出去就退，退出码成功失败都是 1，看不出结果；只能等宿主进程出现。
    # 宿主已经开着的话这里立刻通过：新起的那个把窗口叫到前面就退了（Program.cs 的单实例）。
    $name = [System.IO.Path]::GetFileNameWithoutExtension($path)
    $deadline = (Get-Date).AddSeconds($timeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        if (Get-Process -Name $name -ErrorAction SilentlyContinue) { return }
        Start-Sleep -Milliseconds 200
    }
    throw ('资源管理器没把虚拟窗口拉起来：' + $timeoutSeconds + ' 秒内没等到 ' + $name + ' 进程。' +
           '看看是不是弹了安全提示在等你点，或者资源管理器本身没在跑（任务栏还在不在）。' +
           '也可以直接双击 ' + $path + '。' +
           '这里不改成直接启动：那样宿主会继承这个终端的环境变量，再传给它拉起的桌面端。')
}

# 编进 exe 的东西，跟 AgentHost.csproj 对齐：工程文件、manifest、图标、src\ 下的源码、
# hooks\ 下嵌进去的钩子脚本。csproj 里再加别的输入，这里也要加。
# 本脚本不算：它只有发布参数会影响 exe，那个单独进了指纹。
$inputs = @(@($proj, $man, $ico) | Where-Object { Test-Path -LiteralPath $_ })
if (Test-Path -LiteralPath $src) {
    $inputs += @(Get-ChildItem -LiteralPath $src -Filter '*.cs' -File -Recurse | ForEach-Object { $_.FullName })
}
if (Test-Path -LiteralPath $hooks) {
    $inputs += @(Get-ChildItem -LiteralPath $hooks -File -Recurse | ForEach-Object { $_.FullName })
}
# 指纹在动手编之前算好：编的过程中有人改了源码，下次能看出来。
$inputsMark = Get-InputsMark
$need = Test-NeedBuild $inputsMark

if ($need) {
    $dotnet = Get-Command 'dotnet' -ErrorAction SilentlyContinue
    if (-not $dotnet) {
        throw '没找到 dotnet。编译需要 .NET 10 SDK（winget install Microsoft.DotNet.SDK.10）；只运行的话装 .NET Desktop Runtime 10 就够。'
    }
    $env:DOTNET_NOLOGO = '1'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

    # 1) 先发布，**不碰**正在跑的宿主。编不过就到此为止，宿主和四家原样留着
    #    （以前是先收起宿主再编，编不过也已经收起来了）。
    $out = Join-Path $env:TEMP 'agent-host-build.log'
    if (Test-Path -LiteralPath $pubDir) { Remove-Item -LiteralPath $pubDir -Recurse -Force }
    $dotnetArgs = @('publish', (Quote-Arg $proj)) + $publishFlags + @('-o', (Quote-Arg $pubDir))
    # 不用 -Wait：PowerShell 7 的 -Wait 会连子孙进程一起等，而 dotnet 编完会留一个常驻的编译服务器
    # （VBCSCompiler），脚本就永远卡在这里（2026-09-26 卡了 5 分钟才发现）。只等 dotnet 本身退出。
    $p = Start-Process -FilePath $dotnet.Source -ArgumentList $dotnetArgs -PassThru -WindowStyle Hidden `
        -RedirectStandardOutput $out -RedirectStandardError ($out + '.err')
    # Windows PowerShell 5.1：不先碰一下 Handle，进程退出后 ExitCode 是空的，空 -ne 0 就被当成编译失败
    $null = $p.Handle
    $p.WaitForExit()
    $built = Join-Path $pubDir 'AgentHost.exe'
    if ($p.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $built)) {
        $msg = ''
        if (Test-Path -LiteralPath $out) { $msg = Get-Content -LiteralPath $out -Raw }
        if (Test-Path -LiteralPath ($out + '.err')) { $msg += Get-Content -LiteralPath ($out + '.err') -Raw }
        if (-not $msg) { $msg = "dotnet publish exited $($p.ExitCode)" }
        throw ('build failed: ' + $msg.Trim())
    }

    # 2) 编好了才收起正在跑的宿主，等它自己退、exe 解锁。
    if (Test-Path -LiteralPath $exe) {
        $running = @(Get-Process -Name 'AgentHost' -ErrorAction SilentlyContinue)
        if ($running.Count -gt 0) {
            foreach ($proc in $running) {
                try { Send-Collapse $proc | Out-Null } catch { }
            }
            $deadline = (Get-Date).AddSeconds(12)
            while ((Get-Date) -lt $deadline -and (Get-Process -Name 'AgentHost' -ErrorAction SilentlyContinue)) {
                Start-Sleep -Milliseconds 250
            }
            if (Get-Process -Name 'AgentHost' -ErrorAction SilentlyContinue) {
                throw ('虚拟窗口还没退。多半是有哪家没能还原成独立窗口、宿主自己取消了这次关闭 —— ' +
                       '看它有没有弹提示，按提示点那格的「移出」再试。' +
                       '这里不 Kill 宿主：宿主的 HWND 一销毁，挂在下面的客户端窗口会被系统连带销毁。' +
                       '新版已经编好在 ' + $built + '，宿主退了之后再跑一次本脚本即可换上。')
            }
        }
        $deadline = (Get-Date).AddSeconds(5)
        while ((Get-Date) -lt $deadline) {
            try {
                $fs = [System.IO.File]::Open($exe, 'Open', 'ReadWrite', 'None')
                $fs.Close()
                break
            } catch {
                Start-Sleep -Milliseconds 200
            }
        }
    }

    # 3) 换上。只拷一个 exe（单文件发布），调用方路径不变：scripts\AgentHost.exe。
    Copy-Item -LiteralPath $built -Destination $exe -Force
    # Copy-Item 会保留发布出来那个文件的修改时间；dotnet 增量发布没重写它时，那个时间
    # 可能比源码还旧。这里按「刚换上」记：没有指纹记录的时候还要靠它跟输入比时间。
    (Get-Item -LiteralPath $exe).LastWriteTimeUtc = [DateTime]::UtcNow
    Save-Stamp $inputsMark
}

if ($Run) {
    Start-ViaExplorer $exe
}
