# requires -Version 7.0
<#
  TaoDown 超大文件 + 满列表并发下载回归（2026-11-15）
  口径：单文件 32GB 级（几十 G）+ 满列表 40 任务并发，含崩溃续传与快照恢复。
  断言失败即抛错退出（红灯），证据落 evidence\bigfile\。
  用法：pwsh -File e2e/bigfile_regression.ps1 [-BigFileSizeGB 32] [-ListCount 40] [-SkipResume]
#>
param(
    [int]$BigFileSizeGB = 32,
    [int]$ListCount = 40,
    [switch]$SkipResume
)
$ErrorActionPreference = 'Stop'

# ---------- 工具 ----------
function Find-AppExe {
    $regKey = Get-ItemProperty "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*",
        "HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*" -ErrorAction SilentlyContinue |
        Where-Object { $_.DisplayName -like "*TaoDown*" } | Select-Object -First 1
    if ($regKey -and $regKey.InstallLocation -and (Test-Path (Join-Path $regKey.InstallLocation "TaoDown.exe"))) {
        return (Join-Path $regKey.InstallLocation "TaoDown.exe")
    }
    foreach ($d in (Get-PSDrive -PSProvider FileSystem | Where-Object { $_.Free }).Root) {
        $cand = Join-Path $d "Program Files\TaoDown\TaoDown.exe"
        if (Test-Path $cand) { return $cand }
    }
    return $null
}
function Snap($path) {
    Add-Type -AssemblyName System.Windows.Forms, System.Drawing
    $b = [System.Windows.Forms.SystemInformation]::VirtualScreen
    $bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($b.Left, $b.Top, 0, 0, $bmp.Size)
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    if ((Get-Item $path).Length -lt 20000) { throw "截图疑似空白（$path）" }
}
function Find-DataRoot {
    foreach ($p in "D:\TaoDownData\TaoDown", "$env:USERPROFILE\TaoDownData\TaoDown") {
        if (Test-Path $p) { return $p }
    }
    throw "找不到 TaoDown 数据根（D:\TaoDownData\TaoDown）"
}
function Get-CompleteCount($log, $name) {
    if (-not (Test-Path $log)) { return 0 }
    return (Select-String -Path $log -Pattern "→Complete" | Where-Object { $_.Line -like "*$name*" }).Count
}
function Assert-Watchdog($log) {
    if (Test-Path $log) {
        $slow = Select-String -Path $log -Pattern '界面心跳连续卡顿' | Select-Object -Last 1
        if ($slow) { throw "看门狗红灯（界面响应不达标）：$($slow.Line)" }
    }
}

function Collect-EngineLogs($evd, $dataRoot) {
    # v8 修红：失败路径先抢救引擎日志（startup.log/engine-main.log/refresh-diag.log），红灯可定谳不再盲猜
    $logDir = Join-Path $dataRoot "logs"
    if (-not (Test-Path $logDir)) { Write-Host "引擎日志目录不存在：$logDir"; return }
    try {
        New-Item -ItemType Directory -Force -Path (Join-Path $evd "enginelogs") | Out-Null
        foreach ($name in "startup.log", "engine-main.log", "engine-ftp.log", "refresh-diag.log") {
            $p = Join-Path $logDir $name
            if (Test-Path $p) { Copy-Item $p (Join-Path $evd "enginelogs" $name) -Force; Write-Host "引擎日志已抢救：$name" }
        }
        # v1.0.12 黑匣子：进程无声退出时，事件日志与 WER dump 是唯一现场
        try {
            $evts = Get-WinEvent -FilterHashtable @{ LogName = 'Application'; StartTime = (Get-Date).AddHours(-2) } -ErrorAction SilentlyContinue |
                Where-Object { $_.ProviderName -in 'Application Error', '.NET Runtime', 'Windows Error Reporting' } |
                Select-Object -First 20
            $evts | ForEach-Object { "[$($_.TimeCreated)] [$($_.ProviderName)] $($_.Message)`n" } |
                Set-Content (Join-Path $evd "enginelogs\crash-events.txt")
            if ($evts) { Write-Host "事件日志已抢救：$($evts.Count) 条（crash-events.txt）" }
        } catch { Write-Host "事件日志抢救异常：$($_.Exception.Message)" }
        $dumpDir = Join-Path $env:LOCALAPPDATA "TaoDown\dumps"
        if (Test-Path $dumpDir) {
            Get-ChildItem $dumpDir -Filter "*.dmp" -ErrorAction SilentlyContinue | Select-Object -First 5 |
                ForEach-Object { Copy-Item $_.FullName (Join-Path $evd "enginelogs") -Force }
            Write-Host "崩溃转储已抢救（如有）"
        }
        foreach ($name in "startup.log", "engine-main.log", "refresh-diag.log") {
            $p = Join-Path $logDir $name
            if (Test-Path $p) {
                Write-Host "===== $name 尾部 ====="
                Get-Content $p -Tail 200 | ForEach-Object { Write-Host $_ }
            }
        }
    } catch { Write-Host "引擎日志抢救异常：$($_.Exception.Message)" }
}

# ---------- 起本地稀疏源 ----------
$port = 18099
$srv = [System.Net.HttpListener]::new()
$srv.Prefixes.Add("http://127.0.0.1:$port/")
$srv.Start()
$bigLen = [long]$BigFileSizeGB * 1GB
$listLen = 512MB
# v2 修红：Start-Job 跨进程传 HttpListener 活对象会被反序列化成死壳，源不回数据；
# 改同进程 Runspace，活对象直传（human_sim.ps1 v2 同口径）。
$srcRs = [runspacefactory]::CreateRunspace()
$srcRs.Open()
$srcPs = [powershell]::Create()
$srcPs.Runspace = $srcRs
[void]$srcPs.AddScript({
    param($s, $bigLen, $listLen)
    $buf = New-Object byte[] (1MB)
    while ($s.IsListening) {
        $ctx = $s.GetContext()
        $ctx.Response.ContentType = 'application/octet-stream'
        if ($ctx.Request.Url.AbsolutePath -eq '/big.bin') {
            $ctx.Response.ContentLength64 = $bigLen
            for ($i = 0; $i -lt ($bigLen / 1MB); $i++) { $ctx.Response.OutputStream.Write($buf, 0, $buf.Length) }
        } else {
            $ctx.Response.ContentLength64 = $listLen
            for ($i = 0; $i -lt ($listLen / 1MB); $i++) { $ctx.Response.OutputStream.Write($buf, 0, $buf.Length) }
        }
        $ctx.Response.OutputStream.Close()
    }
}).AddArgument($srv).AddArgument($bigLen).AddArgument($listLen)
$srcHandle = $srcPs.BeginInvoke()

$appExe = Find-AppExe
if (-not $appExe) { try { $srv.Stop(); Start-Sleep -Seconds 2; $srcPs.Stop(); $srcRs.Close() } catch { }; throw "找不到已安装的 TaoDown.exe" }
$dataRoot = Find-DataRoot

# v1.0.12 环境隔离（云批 44042676045 实锤）：上一个步骤遗留的死源任务（看门狗闸的
# 18099/big.bin、真人模拟的 18100/m*）会从快照复活，重试循环污染本轮断言；
# 还有孤儿 aria2c 子进程占着 RPC 端口。开跑前一律清场。
Get-Process TaoDown, aria2c, aria2-next -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 2
# 修红（run 44061254563 M3 实锤）：tasks.json/snapshot 在 state\ 子目录，非递归删不到，
# 看门狗遗留的 18099/big.bin 从快照复活——清场必须 -Recurse
Get-ChildItem $dataRoot -Filter "*.json" -Recurse -ErrorAction SilentlyContinue | Where-Object { $_.Name -match 'task|snapshot' } | Remove-Item -Force -ErrorAction SilentlyContinue
Get-ChildItem (Join-Path $dataRoot "downloads"), $dataRoot -Filter "big*.bin*" -Recurse -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue

$log = Join-Path $dataRoot "logs\startup.log"
$evd = Join-Path $PSScriptRoot "..\evidence\bigfile"
New-Item -ItemType Directory -Force -Path $evd | Out-Null
Write-Host "目标: $appExe | 数据根: $dataRoot | 源: http://127.0.0.1:$port/"

try {
    # ---------- 场景 A：几十 G 单文件 ----------
    $t0 = Get-Date
    Start-Process $appExe -ArgumentList "`"http://127.0.0.1:$port/big.bin`""
    Start-Sleep -Seconds 120
    Get-Process TaoDown -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $appExe } | Stop-Process -Force
    Snap (Join-Path $evd "A-kill-mid-download.png")
    Write-Host "A: 下载推进 120 秒后强制崩溃，落证据。"

    Start-Process $appExe -ArgumentList "`"http://127.0.0.1:$port/big.bin`""
    if (-not $SkipResume) {
        # 断言续传：重启后 CompletedBytes 不从零（日志可查），等待完成
        # v6 修红：加落盘里程表（每分钟报进度和实时速率），红灯时不再两眼一抹黑；预算放宽到 60 分钟
        $deadline = (Get-Date).AddMinutes(60)
        $prevBytes = [long]-1
        $prevTick = Get-Date
        while ((Get-Date) -lt $deadline) {
            Start-Sleep -Seconds 60
            if (-not (Get-Process TaoDown -ErrorAction SilentlyContinue)) { throw "A: 续传进程退出，红灯" }
            Assert-Watchdog $log
            if ((Get-CompleteCount $log "big.bin") -ge 1) { break }
            # v7 修红：进度口径改 .aria2 控制文件（记录真实已传输 completedLength）；
            # 稀疏预分配会让文件大小瞬间=32GB，文件大小当进度=假读数
            $ctrl = Get-ChildItem "D:\Program Files\Downloads\TaoDown", (Join-Path $dataRoot "downloads"), $dataRoot -Filter "big.bin.aria2" -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
            $cur = [long]0
            if ($ctrl) {
                $b = [System.IO.File]::ReadAllBytes($ctrl.FullName)
                # aria2 控制文件：offset 8..15 为 totalLength（大端 u64），16..23 为 uploadLength，24..31 为 completedLength（大端 u64）
                if ($b.Length -ge 32) {
                    $cur = 0L
                    for ($k = 24; $k -lt 32; $k++) { $cur = ($cur -shl 8) -bor $b[$k] }
                }
            } else {
                # 控制文件已消失=任务已完成或文件名变化，回落到 Complete 标记判断；里程表照读文件大小兜底
                $part = Get-ChildItem "D:\Program Files\Downloads\TaoDown", (Join-Path $dataRoot "downloads"), $dataRoot -Filter "big.bin" -Recurse -ErrorAction SilentlyContinue | Measure-Object Length -Sum
                $cur = [long]($part.Sum)
            }
            $left = [int]($deadline - (Get-Date)).TotalMinutes
            if ($prevBytes -ge 0 -and $cur -gt $prevBytes) {
                $rate = [math]::Round(($cur - $prevBytes) / 1MB / ((Get-Date) - $prevTick).TotalSeconds, 1)
                Write-Host ("A: 实传 {0:N2} GB / {1} GB，实测 {2} MB/s，剩 {3} 分钟" -f ($cur/1GB), $BigFileSizeGB, $rate, $left)
            } else {
                Write-Host ("A: 实传 {0:N2} GB / {1} GB（无推进），剩 {2} 分钟" -f ($cur/1GB), $BigFileSizeGB, $left)
            }
            $prevBytes = $cur; $prevTick = Get-Date
        }
        if ((Get-CompleteCount $log "big.bin") -lt 1) {
            throw "A: 60 分钟内 32GB 文件未到 Complete，红灯（最后进度 $([math]::Round($prevBytes/1GB,2)) GB）"
        }
        $bigFile = Get-ChildItem "D:\Program Files\Downloads\TaoDown", (Join-Path $dataRoot "downloads") -Filter "big.bin" -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
        if (-not $bigFile) { throw "A: 找不到落盘 big.bin" }
        if ($bigFile.Length -ne $bigLen) { throw "A: 落盘 $([int64]$bigFile.Length) 字节 != ContentLength $bigLen，红灯" }
        Snap (Join-Path $evd "A-complete.png")
        Write-Host "A: 32GB 下载完成 + 崩溃续传通过，耗时 $([int]((Get-Date)-$t0).TotalMinutes) 分钟"
    }

    # ---------- 场景 B：满列表并发 ----------
    for ($i = 1; $i -le $ListCount; $i++) {
        Start-Process $appExe -ArgumentList "`"http://127.0.0.1:$port/file$i.bin`""
        Start-Sleep -Seconds 2
    }
    Start-Sleep -Seconds 90
    Snap (Join-Path $evd "B-concurrent-peak.png")
    if (-not $SkipResume) {
        Get-Process TaoDown -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $appExe } | Stop-Process -Force
        # v1.0.12 修红：强杀是异步终止，旧实例的互斥体/端口/子进程清理需要几秒；
        # 立即重启会让新实例在初始化最早期无声退出（云批 44042676045 实锤，B 段零日志红灯）
        Start-Sleep -Seconds 5
        Write-Host "B: 满列表并发推进 90 秒后强制崩溃（快照恢复口径）。"
        Start-Process $appExe
    }
    $deadline = (Get-Date).AddMinutes(30)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 60
        if (-not (Get-Process TaoDown -ErrorAction SilentlyContinue)) { throw "B: 并发恢复进程退出，红灯" }
        Assert-Watchdog $log
        $done = 0
        for ($i = 1; $i -le $ListCount; $i++) { $done += [int]((Get-CompleteCount $log "file$i.bin") -gt 0) }
        $part = Get-ChildItem "D:\Program Files\Downloads\TaoDown", (Join-Path $dataRoot "downloads"), $dataRoot -Filter "file*.bin*" -Recurse -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -notmatch '\.aria2' } | Measure-Object Length -Sum
        Write-Host ("B: {0}/{1} Complete，已落盘 {2:N1} GB / {3} GB" -f $done, $ListCount, ($part.Sum/1GB), ($ListCount*0.5))
        if ($done -ge $ListCount) { break }
    }
    $done = 0
    for ($i = 1; $i -le $ListCount; $i++) { $done += [int]((Get-CompleteCount $log "file$i.bin") -gt 0) }
    if ($done -lt $ListCount) { throw "B: 30 分钟预算内仅 $done/$ListCount 到终态，存在静默失败，红灯" }
    Snap (Join-Path $evd "B-complete.png")
    Assert-Watchdog $log
    Write-Host "B: 满列表 $ListCount/$ListCount 全部 Complete，看门狗零命中。"

    # 证据归档
    Copy-Item $log (Join-Path $evd "startup.log") -Force
    Write-Host "全部通过：超大文件 32GB + 满列表 $ListCount 并发，证据落 $evd"
} catch {
    # v8 修红：红灯时先抢救引擎日志再抛，确保证据包可定谳
    Collect-EngineLogs $evd $dataRoot
    throw
} finally {
    Get-Process TaoDown -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $appExe } | Stop-Process -Force -ErrorAction SilentlyContinue
    try { $srv.Stop(); Start-Sleep -Seconds 2; if ($srcPs) { $srcPs.Stop() }; if ($srcRs) { $srcRs.Close() } } catch { }
    $srv.Stop()
}
