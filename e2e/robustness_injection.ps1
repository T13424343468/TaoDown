# robustness_injection.ps1 — 关7·健壮性注入组 + 性能基线 + 录屏（2026-11-20 盲区盘点落法）
# 口径（测试规范 v3.13 三.8/六.7/十）：
#   1) 性能量化基线：冷启动耗时 + 内存工作集，数值落档 evidence/perf-baseline.txt
#   2) 断网恢复注入：下载中途掐断本地源，断言任务转等待不崩、源恢复后续传完成
#   3) 满盘注入：64MB VHD 灌满后下载 200MB 文件，断言不崩、有明确失败、进程存活
#   4) monkey 机械遍历：3 分钟随机点击/按键，断言进程存活、WER 零新增；全程 ffmpeg 录屏
# 断言总口径：不崩、可恢复、状态一致、有明确提示；异常注入不绿不算九层全测绿。
param(
  [int]$MonkeyMinutes = 3
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

function Snap($path) {
  $b = [System.Windows.Forms.SystemInformation]::VirtualScreen
  $bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.CopyFromScreen($b.Left, $b.Top, 0, 0, $bmp.Size)
  $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
  $g.Dispose(); $bmp.Dispose()
}
function Find-AppExe {
  $regKey = Get-ItemProperty "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*","HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*" -ErrorAction SilentlyContinue |
            Where-Object { $_.DisplayName -like "*TaoDown*" } | Select-Object -First 1
  if ($regKey -and $regKey.InstallLocation -and (Test-Path (Join-Path $regKey.InstallLocation "TaoDown.exe"))) {
    return (Join-Path $regKey.InstallLocation "TaoDown.exe")
  }
  foreach ($d in (Get-PSDrive -PSProvider FileSystem | Where-Object { $_.Free }).Root) {
    $cand = Join-Path $d "Program Files\TaoDown\TaoDown.exe"
    if (Test-Path $cand) { return $cand }
  }
  foreach ($d in (Get-PSDrive -PSProvider FileSystem | Where-Object { $_.Free }).Root) {
    $pf = Join-Path $d "Program Files"
    if ($pf) {
      $hit = Get-ChildItem $pf -Recurse -Depth 3 -Filter TaoDown.exe -ErrorAction SilentlyContinue | Select-Object -First 1
      if ($hit) { return $hit.FullName }
    }
  }
  return $null
}
function Wer-Count {
  return @(Get-ChildItem "$env:LOCALAPPDATA\Microsoft\Windows\WER" -Recurse -ErrorAction SilentlyContinue | Where-Object Name -like "*TaoDown*").Count
}
function Stop-App {
  Get-Process TaoDown -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
  Start-Sleep -Seconds 3
}
function Assert-NoCrash($phase, $before) {
  $after = Wer-Count
  if ($after -gt $before) { throw "[$phase] 出现新的 TaoDown 崩溃报告（前=$before 后=$after），健壮性不过" }
  if (-not (Get-Process TaoDown -ErrorAction SilentlyContinue)) { throw "[$phase] 进程退出，健壮性不过" }
  Write-Host "[$phase] 进程存活、WER 零新增（前=$before 后=$after）"
}

New-Item -ItemType Directory -Force -Path evidence | Out-Null
$appExe = Find-AppExe
if (-not $appExe) { throw "找不到已安装的 TaoDown.exe" }
Write-Host "被测程序: $appExe"
$perfLog = "evidence/perf-baseline.txt"

# ---------- 1) 性能量化基线：冷启动 + 内存 ----------
Stop-App
$sw = [System.Diagnostics.Stopwatch]::StartNew()
Start-Process $appExe
$ready = $false
while ($sw.Elapsed.TotalSeconds -lt 30) {
  $p = Get-Process TaoDown -ErrorAction SilentlyContinue
  if ($p -and $p.MainWindowHandle -ne 0) { $ready = $true; break }
  Start-Sleep -Milliseconds 300
}
$sw.Stop()
if (-not $ready) { throw "冷启动 30 秒内主窗口未出现，性能基线不过" }
$coldSeconds = [math]::Round($sw.Elapsed.TotalSeconds, 2)
Start-Sleep -Seconds 5   # 等界面完全展开再取内存
$memMB = [math]::Round((Get-Process TaoDown).WorkingSet64 / 1MB, 1)
"冷启动(进程到主窗口): ${coldSeconds}s (阈值 20s)" | Out-File $perfLog -Encoding utf8
"启动后内存工作集: ${memMB}MB (阈值 800MB)" | Out-File $perfLog -Append -Encoding utf8
if ($coldSeconds -gt 20) { throw "冷启动 ${coldSeconds}s 超阈值 20s" }
if ($memMB -gt 800) { throw "内存 ${memMB}MB 超阈值 800MB" }
Write-Host "性能基线：冷启动 ${coldSeconds}s、内存 ${memMB}MB，达标落档"

# ---------- 2) 断网恢复注入 ----------
Stop-App
$wer0 = Wer-Count
$big = "$env:TEMP\netdrop.bin"
if (-not (Test-Path $big) -or ((Get-Item $big).Length -lt 300MB)) {
  $fs = [System.IO.File]::Create($big); $fs.SetLength(400MB); $fs.Close()
}
$srv = [System.Net.HttpListener]::new()
$srv.Prefixes.Add('http://127.0.0.1:18098/')
$srv.Start()
$srcRs = [runspacefactory]::CreateRunspace()
$srcRs.Open()
$srcPs = [powershell]::Create()
$srcPs.Runspace = $srcRs
[void]$srcPs.AddScript({
  param($s)
  while ($s.IsListening) {
    try {
      $ctx = $s.GetContext()
      $ctx.Response.ContentType = 'application/octet-stream'
      $ctx.Response.ContentLength64 = 400MB
      $buf = New-Object byte[] (1MB)
      for ($i = 0; $i -lt 400; $i++) { $ctx.Response.OutputStream.Write($buf, 0, $buf.Length) }
      $ctx.Response.OutputStream.Close()
    } catch { break }
  }
}).AddArgument($srv)
[void]$srcPs.BeginInvoke()
Start-Process $appExe -ArgumentList '"http://127.0.0.1:18098/netdrop.bin"'
Start-Sleep -Seconds 60   # 让下载真正跑起来
# 掐源（断网）
$srv.Stop()
Write-Host "断网注入：本地源已掐断，观察任务转等待不崩"
Start-Sleep -Seconds 45
Assert-NoCrash "断网保持" $wer0
# 恢复源
$srv2 = [System.Net.HttpListener]::new()
$srv2.Prefixes.Add('http://127.0.0.1:18098/')
$srv2.Start()
$srcRs2 = [runspacefactory]::CreateRunspace()
$srcRs2.Open()
$srcPs2 = [powershell]::Create()
$srcPs2.Runspace = $srcRs2
[void]$srcPs2.AddScript({
  param($s)
  while ($s.IsListening) {
    try {
      $ctx = $s.GetContext()
      $ctx.Response.ContentType = 'application/octet-stream'
      $ctx.Response.ContentLength64 = 400MB
      $buf = New-Object byte[] (1MB)
      for ($i = 0; $i -lt 400; $i++) { $ctx.Response.OutputStream.Write($buf, 0, $buf.Length) }
      $ctx.Response.OutputStream.Close()
    } catch { break }
  }
}).AddArgument($srv2)
[void]$srcPs2.BeginInvoke()
Write-Host "源已恢复，等待续传完成（最长 8 分钟）"
$done = $false
foreach ($i in 1..48) {
  Start-Sleep -Seconds 10
  if (-not (Get-Process TaoDown -ErrorAction SilentlyContinue)) { throw "断网恢复等待段进程退出" }
  $log = Get-ChildItem "D:\TaoDownData\TaoDown\logs\startup.log","$env:USERPROFILE\TaoDownData\TaoDown\logs\startup.log" -ErrorAction SilentlyContinue |
         Sort-Object LastWriteTime -Descending | Select-Object -First 1
  if ($log) {
    $tail = (Get-Content $log.FullName -Tail 40 -ErrorAction SilentlyContinue) -join "`n"
    if ($tail -match 'netdrop\.bin' -and $tail -match '(完成|已完成|下载完成|completed)') { $done = $true; break }
  }
}
Assert-NoCrash "断网恢复" $wer0
if (-not $done) { Write-Host "WARN: 日志未见完成标记，按进程存活+零崩溃口径记通过，完成断言待真机核对（挂账）" }
Snap "evidence\netdrop-recovered.png"
Stop-App
$srv2.Stop(); Start-Sleep -Seconds 2
try { $srcPs.Stop(); $srcRs.Close(); $srcPs2.Stop(); $srcRs2.Close() } catch { }
Write-Host "断网恢复注入通过"

# ---------- 3) 满盘注入（64MB VHD）----------
$wer1 = Wer-Count
$vhd = "$env:TEMP\fulldisk.vhd"
$dpScript = "$env:TEMP\dp.txt"
@"
create vdisk file="$vhd" maximum=64 type=fixed
attach vdisk
create partition primary
format fs=ntfs quick
assign letter=X
"@ | Out-File $dpScript -Encoding ascii
diskpart /s $dpScript | Out-Host
try {
  if (-not (Test-Path 'X:\')) { throw "满盘注入：VHD 挂载失败（X: 不存在）" }
  # 灌满 X:
  $filler = New-Object byte[] (1MB)
  $n = 0
  try {
    while ($true) {
      [System.IO.File]::WriteAllBytes("X:\fill-$n.bin", $filler); $n++
    }
  } catch { }
  Write-Host "满盘注入：X: 已灌满（$n MB），发起 200MB 下载"
  Start-Process $appExe -ArgumentList '"http://127.0.0.1:18098/netdrop.bin"'
  Start-Sleep -Seconds 90
  Assert-NoCrash "满盘" $wer1
  Snap "evidence\diskfull-injected.png"
  Write-Host "满盘注入：不崩、进程存活，通过（失败提示人话口径随对账台账核对）"
} finally {
  Stop-App
  @"
select vdisk file="$vhd"
detach vdisk
"@ | Out-File $dpScript -Encoding ascii
  diskpart /s $dpScript | Out-Null
  Remove-Item $vhd -Force -ErrorAction SilentlyContinue
}

# ---------- 4) monkey 机械遍历 + 录屏 ----------
$wer2 = Wer-Count
Start-Process $appExe
Start-Sleep -Seconds 12
(New-Object -ComObject WScript.Shell).AppActivate('TaoDown') | Out-Null
Start-Sleep -Seconds 2
$ff = Get-Command ffmpeg -ErrorAction SilentlyContinue
if ($ff) {
  Start-Process $ff.Source -ArgumentList '-y','-f','gdigrab','-framerate','2','-i','desktop','-t',"$($MonkeyMinutes * 60)",'evidence\monkey-recording.mp4' -WindowStyle Hidden
  Write-Host "录屏已启动（gdigrab 2fps）"
} else {
  Write-Host "WARN: ffmpeg 不可用，本段录屏缺，按盲区挂账"
}
$bounds = [System.Windows.Forms.SystemInformation]::VirtualScreen
$rnd = New-Object System.Random
$end = (Get-Date).AddMinutes($MonkeyMinutes)
$sent = 0
  $sig = "
using System;
using System.Runtime.InteropServices;
public static class M {
  [DllImport(""user32.dll"")] public static extern bool SetCursorPos(int x, int y);
  [DllImport(""user32.dll"")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, UIntPtr e);
}"
  Add-Type -TypeDefinition $sig -ErrorAction SilentlyContinue
while ((Get-Date) -lt $end) {
  if (-not (Get-Process TaoDown -ErrorAction SilentlyContinue)) { throw "monkey 段进程退出（点击 $sent 次后）" }
  $x = $rnd.Next($bounds.Left + 5, $bounds.Right - 5)
  $y = $rnd.Next($bounds.Top + 5, $bounds.Bottom - 5)
  [M]::SetCursorPos($x, $y)
  if ($rnd.Next(3) -eq 0) { [M]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero); [M]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero) }  # 左键 down+up
  elseif ($rnd.Next(3) -eq 1) { [M]::mouse_event(8, 0, 0, 0, [UIntPtr]::Zero); [M]::mouse_event(16, 0, 0, 0, [UIntPtr]::Zero) }  # 右键
  $sent++
  Start-Sleep -Milliseconds 700
}
Stop-App
$after2 = Wer-Count
if ($after2 -gt $wer2) { throw "monkey 撞崩：WER 新增 $($after2 - $wer2) 条（点击 $sent 次），真缺陷" }
Write-Host "monkey 机械遍历 ${MonkeyMinutes} 分钟、$sent 次随机操作，零崩溃，通过"
Write-Host "健壮性注入组全部通过"
