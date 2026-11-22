# 六家浏览器下载接管实测（老板 2026-11-21 附加核验令）：
# 挂载成功 ≠ 接管成功。本脚本对自动挂载清单内的全部 Chromium 内核浏览器——
# Edge、Chrome、Chromium、Brave、Vivaldi、Yandex——逐家真启动、真触发一条下载，
# 断言两件事同时成立才算接管：
#   ① 浏览器侧下载被取消：Downloads 目录没有成品文件（接管失败的浏览器会自己下完）；
#   ② 接管链路收到转交：收件箱出现含测试 URL 的 JSON（宿主 SaveToInbox 落盘）
#      且 host.log 有「收到插件请求」记录。
# 证据全部落盘 evidence\；任何一家不过整步报红，不允许静默跳过。
# 夸克/360 为手动挂载内核（下载监听停用，接管走右键菜单），不在本自动覆盖口径内。
$ErrorActionPreference = 'Stop'
$Id = 'dchnmdakoleaofgpkhddhmohcoomlimp'
$Port = 18098
$Url = "http://127.0.0.1:$Port/takeover.bin"

# ---- 数据根（与 TaoDown.Core.DataRoot 同口径：D 盘首选，回退本地）----
$InboxDirs = @('D:\TaoDownData\TaoDown\inbox', "$env:LOCALAPPDATA\TaoDownData\TaoDown\inbox")
$HostLogs = @('D:\TaoDownData\TaoDown\logs\host.log', "$env:LOCALAPPDATA\TaoDownData\TaoDown\logs\host.log")

Add-Type -AssemblyName System.Windows.Forms; Add-Type -AssemblyName System.Drawing
New-Item -ItemType Directory -Force -Path evidence | Out-Null
function Snap($path) {
  $b = [System.Windows.Forms.SystemInformation]::VirtualScreen
  $bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.CopyFromScreen($b.Left, $b.Top, 0, 0, $bmp.Size)
  $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
  $g.Dispose(); $bmp.Dispose()
}
Add-Type -Namespace Native -Name Win32TakeoverSnap -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
[DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
'@
function Prep-Snap([string]$ProcName) {
  try {
    $p = Get-Process -Name $ProcName -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
    if ($p) {
      [Native.Win32TakeoverSnap]::ShowWindow($p.MainWindowHandle, 9) | Out-Null
      [Native.Win32TakeoverSnap]::SetForegroundWindow($p.MainWindowHandle) | Out-Null
      Start-Sleep -Milliseconds 800
    }
  } catch { }
}

# ---- 本地下载源：2MB 附件直链（Content-Disposition 强制下载）----
$payload = New-Object byte[] (2MB)
(New-Object Random).NextBytes($payload)
$srv = [System.Net.HttpListener]::new()
$srv.Prefixes.Add("http://127.0.0.1:$Port/")
$srv.Start()
$srcRs = [runspacefactory]::CreateRunspace()
$srcRs.Open()
$srcPs = [powershell]::Create()
$srcPs.Runspace = $srcRs
[void]$srcPs.AddScript({
    param($s, $body)
    while ($s.IsListening) {
      $ctx = $s.GetContext()
      $ctx.Response.ContentType = 'application/octet-stream'
      $ctx.Response.ContentLength64 = $body.Length
      $ctx.Response.AddHeader('Content-Disposition', 'attachment; filename="takeover.bin"')
      $ctx.Response.OutputStream.Write($body, 0, $body.Length)
      $ctx.Response.OutputStream.Close()
    }
  }).AddArgument($srv).AddArgument($payload)
$srcHandle = $srcPs.BeginInvoke()

function Wait-ExtensionMounted([string]$ProfileDir, [int]$Rounds) {
  for ($i = 0; $i -lt $Rounds; $i++) {
    Start-Sleep -Seconds 2
    foreach ($file in @('Secure Preferences', 'Preferences')) {
      $pf = Join-Path $ProfileDir "Default\$file"
      if (-not (Test-Path $pf)) { continue }
      try { $json = Get-Content $pf -Raw -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop } catch { continue }
      $settings = $json.extensions.settings
      if ($settings -and ($settings.PSObject.Properties.Name -contains $Id)) { return $true }
    }
  }
  return $false
}

function Test-InboxHit {
  foreach ($d in $InboxDirs) {
    if (-not (Test-Path $d)) { continue }
    $hit = Get-ChildItem $d -Filter *.json -ErrorAction SilentlyContinue |
           Where-Object { (Get-Content $_.FullName -Raw -ErrorAction SilentlyContinue) -match [regex]::Escape($Url) }
    if ($hit) { return $hit | Select-Object -First 1 }
  }
  return $null
}

function Test-HostLogHit {
  foreach ($l in $HostLogs) {
    if (-not (Test-Path $l)) { continue }
    if (Select-String -Path $l -Pattern ([regex]::Escape($Url)) -ErrorAction SilentlyContinue) { return $true }
  }
  return $false
}

function Test-BrowserKeptFile([string]$ProcName) {
  # 接管失败时浏览器会把文件下进系统 Downloads（新资料目录默认下载目录）
  $hits = @(Get-ChildItem "$env:USERPROFILE\Downloads" -Filter 'takeover*' -ErrorAction SilentlyContinue)
  return ($hits.Count -gt 0)
}

# 与 verify_mount.ps1 同套同源的浏览器矩阵（Opera 用户拍板免测；夸克/360 手动内核不在自动口径）
$Browsers = @(
  @{ Name='Edge';     Proc='msedge';  ExeFile='msedge.exe';  Choco=$null;          Winget=$null;                 Exes=@('C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe','C:\Program Files\Microsoft\Edge\Application\msedge.exe') },
  @{ Name='Chrome';   Proc='chrome';  ExeFile='chrome.exe';  Choco='googlechrome'; Winget='Google.Chrome';       Exes=@('C:\Program Files\Google\Chrome\Application\chrome.exe','C:\Program Files (x86)\Google\Chrome\Application\chrome.exe') },
  @{ Name='Chromium'; Proc='chrome';  ExeFile='chrome.exe';  Choco='chromium';     Winget='Hibbiki.Chromium';    Exes=@('C:\Program Files\Chromium\Application\chrome.exe') },
  @{ Name='Brave';    Proc='brave';   ExeFile='brave.exe';   Choco='brave';        Winget='BraveSoftware.BraveBrowser'; Exes=@('C:\Program Files\BraveSoftware\Brave-Browser\Application\brave.exe','C:\Program Files (x86)\BraveSoftware\Brave-Browser\Application\brave.exe') },
  @{ Name='Vivaldi';  Proc='vivaldi'; ExeFile='vivaldi.exe'; Choco='vivaldi';      Winget='Vivaldi.Vivaldi';     Exes=@('C:\Program Files\Vivaldi\Application\vivaldi.exe',"$env:LOCALAPPDATA\Vivaldi\Application\vivaldi.exe") },
  @{ Name='Yandex';   Proc='browser'; ExeFile='browser.exe'; Choco='yandex-browser'; Winget='Yandex.Browser';    Exes=@('C:\Program Files\Yandex\YandexBrowser\browser.exe',"$env:LOCALAPPDATA\Yandex\YandexBrowser\Application\browser.exe") }
)

function Get-BrowserExe($spec) {
  foreach ($p in $spec.Exes) { if (Test-Path $p) { return $p } }
  foreach ($base in @('C:\Program Files', 'C:\Program Files (x86)', $env:LOCALAPPDATA)) {
    if (-not (Test-Path $base)) { continue }
    $hit = Get-ChildItem $base -Recurse -Depth 3 -Filter $spec.ExeFile -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($hit) { return $hit.FullName }
  }
  return $null
}

$failed = New-Object System.Collections.Generic.List[string]
$report = [ordered]@{}
foreach ($spec in $Browsers) {
  Write-Host ("=== 下载接管实测（{0}） ===" -f $spec.Name)
  $exe = Get-BrowserExe $spec
  if (-not $exe) { $failed.Add("$($spec.Name)：浏览器不在位，未覆盖"); continue }

  # 每家独立资料目录：扩展按登记自动挂载（与 verify_mount 同机制）
  $prof = Join-Path $PWD ("evidence\takeover-{0}-profile" -f $spec.Name.ToLower())
  Remove-Item $prof -Recurse -Force -ErrorAction SilentlyContinue
  Start-Process $exe -ArgumentList "--no-first-run", "--no-default-browser-check", "--user-data-dir=`"$prof`"", "about:blank"
  $mounted = Wait-ExtensionMounted $prof 45
  if (-not $mounted) {
    Get-Process $spec.Proc -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    $failed.Add("$($spec.Name)：扩展未进用户资料，接管无从谈起")
    continue
  }
  Write-Host ("  {0} 扩展已挂载，触发下载: {1}" -f $spec.Name, $Url)

  $inboxBefore = @(Test-InboxHit).Count
  Start-Process $exe -ArgumentList "--user-data-dir=`"$prof`"", $Url
  $taken = $null
  for ($i = 0; $i -lt 30; $i++) {
    Start-Sleep -Seconds 2
    $hit = Test-InboxHit
    if ($hit) { $taken = $hit; break }
  }
  Start-Sleep -Seconds 3
  Prep-Snap $spec.Proc
  Snap ("evidence\takeover-{0}.png" -f $spec.Name.ToLower())
  Get-Process $spec.Proc -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
  Start-Sleep -Seconds 2

  $kept = Test-BrowserKeptFile $spec.Proc
  $logHit = Test-HostLogHit
  if ($taken -and -not $kept) {
    Write-Host ("  {0} 接管成立：收件箱 {1}；浏览器无成品文件；host.log 命中={2}" -f $spec.Name, $taken.Name, $logHit)
    $report[$spec.Name] = @{ inbox = $taken.Name; browserKeptFile = $false; hostLog = $logHit }
  } else {
    $why = @()
    if (-not $taken) { $why += '收件箱无转交记录' }
    if ($kept) { $why += '浏览器侧文件已落盘（未被取消）' }
    $failed.Add("$($spec.Name)：接管不成立（$($why -join '；')）")
    Write-Host ("  {0} 接管不成立：{1}" -f $spec.Name, ($why -join '；'))
  }
}

# 失败取证 (run 44988339377 教训——六家全不接管却无宿主侧第一手证据, 归因断粮):
# 宿主日志尾部 + NativeMessagingHosts 登记键实况落盘, 接管链断在哪一环当场可查
$diag = New-Object System.Collections.Generic.List[string]
foreach ($l in $HostLogs) {
  if (Test-Path $l) {
    $diag.Add("=== host.log: $l ===")
    Get-Content $l -Tail 40 -ErrorAction SilentlyContinue | ForEach-Object { $diag.Add($_) }
  } else { $diag.Add("=== host.log 不存在: $l ===") }
}
$diag.Add('=== NativeMessagingHosts 登记键实况 ===')
foreach ($root in @('Software\Google\Chrome\NativeMessagingHosts','Software\Microsoft\Edge\NativeMessagingHosts','Software\Chromium\NativeMessagingHosts','Software\BraveSoftware\Brave\NativeMessagingHosts','Software\Vivaldi\NativeMessagingHosts','Software\Yandex\YandexBrowser\NativeMessagingHosts')) {
  $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey("$root\$Id")
  $val = if ($key) { $key.GetValue($null) } else { '<键缺失>' }
  $manifestOk = ($val -and (Test-Path $val))
  $diag.Add("$root\$Id = $val (清单文件存在=$manifestOk)")
}
$diag | Set-Content evidence\takeover-diag.txt -Encoding UTF8

$report | ConvertTo-Json -Depth 5 | Set-Content evidence\takeover-report.json -Encoding UTF8
# 清理下载源（先关监听再停 Runspace，防 GetContext 死锁——human_sim v2 同口径）
$srv.Stop(); Start-Sleep -Seconds 2
try { $srcPs.Stop(); $srcRs.Close() } catch { }
# 清理测试下载残留，不污染后续班次
Get-ChildItem "$env:USERPROFILE\Downloads" -Filter 'takeover*' -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue

if ($failed.Count -gt 0) { throw ("下载接管实测不过（六家逐家口径）：{0}" -f ($failed -join '；')) }
Write-Host '下载接管实测通过：六家浏览器逐家真触发下载、转交进收件箱、浏览器侧零成品文件'
