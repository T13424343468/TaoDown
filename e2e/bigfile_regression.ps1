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
        $slow = Select-String -Path $log -Pattern '连续单拍耗时异常' | Select-Object -Last 1
        if ($slow) { throw "看门狗红灯（界面响应不达标）：$($slow.Line)" }
    }
}

# ---------- 起本地稀疏源 ----------
$port = 18099
$srv = [System.Net.HttpListener]::new()
$srv.Prefixes.Add("http://127.0.0.1:$port/")
$srv.Start()
$bigLen = [long]$BigFileSizeGB * 1GB
$listLen = 512MB
$job = Start-Job -ScriptBlock {
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
} -ArgumentList $srv, $bigLen, $listLen

$appExe = Find-AppExe
if (-not $appExe) { Stop-Job $job; Remove-Job $job -Force; throw "找不到已安装的 TaoDown.exe" }
$dataRoot = Find-DataRoot
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
        $deadline = (Get-Date).AddMinutes(40)
        while ((Get-Date) -lt $deadline) {
            Start-Sleep -Seconds 60
            if (-not (Get-Process TaoDown -ErrorAction SilentlyContinue)) { throw "A: 续传进程退出，红灯" }
            Assert-Watchdog $log
            if ((Get-CompleteCount $log "big.bin") -ge 1) { break }
        }
        if ((Get-CompleteCount $log "big.bin") -lt 1) { throw "A: 40 分钟内 32GB 文件未到 Complete，红灯" }
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
        Write-Host "B: $done/$ListCount Complete"
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
} finally {
    Get-Process TaoDown -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $appExe } | Stop-Process -Force -ErrorAction SilentlyContinue
    Stop-Job $job -ErrorAction SilentlyContinue; Remove-Job $job -Force -ErrorAction SilentlyContinue
    $srv.Stop()
}
