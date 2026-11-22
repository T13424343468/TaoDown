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
      $fname = if ($ctx.Request.Url.AbsolutePath -like '*warmup*') { 'warmup.bin' } else { 'takeover.bin' }
      $ctx.Response.AddHeader('Content-Disposition', "attachment; filename=`"$fname`"")
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

# run 45014250527 实锤 + Chromium 137 源码 (install_verifier.cc) 钉死: 关3 (verify_mount.ps1) 写入的
# HKCU 外部登记键在同一作业内持续生效, 关3.5 全新资料目录里扩展被装了两份——--load-extension 未打包
# (location=LOAD) 与注册表外部安装 (location=EXTERNAL_*) 同 ID 合并, prefs 记录的外部安装实例受安装
# 校验管辖: MustRemainDisabled 对未打包位置直接放行 (IsUnpackedLocation 提前 return false), 但外部
# 位置走 !IsFromStore → verified=false; Google 品牌内核 (Chrome/Chrome-for-testing) 在 Windows 上
# GetExperimentStatus()==ENFORCE → DISABLE_NOT_VERIFIED(256); Edge/Brave/Vivaldi 非 Google 品牌
# (VerifyStatus::NONE) 不 enforce 故一直绿; Yandex 对外部安装打 DISABLE_USER_ACTION(1)。
# force_installed 也救不了: AllowedByEnterprisePolicy 豁免路被 IsForceInstalledInLowTrustEnvironment
# (非商店 update_url) 否掉, 仍落 256。修法: 关3.5 开跑前清掉六家外部登记键, 本班只测未打包加载的
# 接管行为本身 (登记挂载路径由关3 单独覆盖), 未打包位置安装校验天然放行。
$extRegRoots = @(
  'HKCU:\Software\Google\Chrome\Extensions',
  'HKCU:\Software\Chromium\Extensions',
  'HKCU:\Software\Microsoft\Edge\Extensions',
  'HKCU:\Software\BraveSoftware\Brave\Extensions',
  'HKCU:\Software\BraveSoftware\Brave-Browser\Extensions',
  'HKCU:\Software\Vivaldi\Extensions',
  'HKCU:\Software\Yandex\YandexBrowser\Extensions',
  'HKCU:\Software\YandexBrowser\Extensions'
)
foreach ($root in $extRegRoots) {
  try {
    if (Test-Path $root) {
      Remove-Item (Join-Path $root $Id) -Recurse -Force -ErrorAction SilentlyContinue
      Write-Host ("  清外部登记 {0}\{1}" -f $root, $Id)
    }
  } catch { Write-Host ("  外部登记清理失败 {0}: {1}" -f $root, $_.Exception.Message) }
}
# 企业策略显式放行 (run 45004083646 实锤): 逐家写 ExtensionSettings 策略放行扩展 ID, 兜住个别内核
# 对未打包加载的额外拦截; run 45014250527 实锤 force_installed 对非商店来源无效 (低信任强制安装
# 不享校验豁免), 回归 allowed——外部登记已清, allowed 不再触发 8192/256 外部安装禁用路。
$policyRoots = [ordered]@{
  'Chrome'   = 'HKCU:\Software\Policies\Google\Chrome\ExtensionSettings'
  'Chromium' = 'HKCU:\Software\Policies\Chromium\ExtensionSettings'
  'Edge'     = 'HKCU:\Software\Policies\Microsoft\Edge\ExtensionSettings'
  'Brave'    = 'HKCU:\Software\Policies\BraveSoftware\Brave\ExtensionSettings'
  'Vivaldi'  = 'HKCU:\Software\Policies\Vivaldi\ExtensionSettings'
  'Yandex'   = 'HKCU:\Software\Policies\Yandex\Browser\ExtensionSettings'
}
foreach ($k in $policyRoots.Keys) {
  try {
    New-Item -ItemType Directory -Force -Path $policyRoots[$k] | Out-Null
    Set-ItemProperty -Path $policyRoots[$k] -Name $Id -Value '{"installation_mode":"allowed"}' -Type String
    Write-Host ("  企业策略放行 {0}: {1}" -f $k, $policyRoots[$k])
  } catch { Write-Host ("  策略写入失败 {0}: {1}" -f $k, $_.Exception.Message) }
}

foreach ($spec in $Browsers) {
  Write-Host ("=== 下载接管实测（{0}） ===" -f $spec.Name)
  $exe = Get-BrowserExe $spec
  if (-not $exe) { $failed.Add("$($spec.Name)：浏览器不在位，未覆盖"); continue }

  # 每家独立资料目录：扩展按登记自动挂载（与 verify_mount 同机制）
  $prof = Join-Path $PWD ("evidence\takeover-{0}-profile" -f $spec.Name.ToLower())
  Remove-Item $prof -Recurse -Force -ErrorAction SilentlyContinue
  # --enable-logging: SW 控制台与 native messaging 报错落 chrome_debug.log (run 44991534420 六家全不接管却无浏览器侧第一手日志, 归因断粮)
  # --load-extension: run 44999835778 取证实锤——注册表外挂 CRX 在新内核装成禁用态 (disable_reasons 256=DISABLE_NOT_VERIFIED,
  #   8192=DISABLE_EXTERNAL_EXTENSION, 六家全中), SW 从未运行接管无从谈起; 未打包加载必然启用, 本步实测的是接管行为本身,
  #   登记挂载路径由关3 单独覆盖。扩展目录与 verify_mount 手动加载路同源。
  $extDir = (Resolve-Path (Join-Path $PSScriptRoot '..\src\extension')).Path
  Write-Host ("  扩展以未打包方式加载 (注册表外挂在新内核被禁用): " + $extDir)
  Start-Process $exe -ArgumentList "--no-first-run", "--no-default-browser-check", "--enable-logging", "--v=1", "--load-extension=`"$extDir`"", "--user-data-dir=`"$prof`"", "about:blank"
  $mounted = Wait-ExtensionMounted $prof 45
  if (-not $mounted) {
    Get-Process $spec.Proc -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    $failed.Add("$($spec.Name)：扩展未进用户资料，接管无从谈起")
    continue
  }
  # 启用态取证 (run 44999835778 教训): 挂载≠启用, disable_reasons 非空即当场报红
  $dr = $null
  foreach ($file in @('Secure Preferences', 'Preferences')) {
    $pf2 = Join-Path $prof "Default\$file"
    if (-not (Test-Path $pf2)) { continue }
    try {
      $j = Get-Content $pf2 -Raw -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop
      $es = $j.extensions.settings
      if ($es -and ($es.PSObject.Properties.Name -contains $Id)) { $dr = $es.$Id.disable_reasons; break }
    } catch { continue }
  }
  if ($dr -and @($dr).Count -gt 0) {
    Get-Process $spec.Proc -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    $failed.Add("$($spec.Name)：扩展被浏览器禁用 (disable_reasons=$($dr -join ','))，接管无从谈起")
    continue
  }
  Write-Host ("  {0} 扩展已挂载且启用，触发下载: {1}" -f $spec.Name, $Url)

  $inboxBefore = @(Test-InboxHit).Count
  # SW 预热 (run 44991534420 归因待证): 挂载后先触发一条预热下载, 唤醒 Service Worker 并走通监听注册, 再清残留跑正式实测
  Start-Process $exe -ArgumentList "--user-data-dir=`"$prof`"", "--load-extension=`"$extDir`"", "http://127.0.0.1:$Port/warmup.bin"
  Start-Sleep -Seconds 8
  Get-ChildItem "$env:USERPROFILE\Downloads" -Filter 'warmup*' -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
  Start-Process $exe -ArgumentList "--user-data-dir=`"$prof`"", "--load-extension=`"$extDir`"", $Url
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
# run 44921050855 实锤教训: 上一版诊断查 NativeMessagingHosts\<扩展ID> 是错键名, 登记器写的是
# NativeMessagingHosts\com.tao.taodown (HostName), 查错键等于没查。本轮: 正确键实况 + manifest
# 内容 + 宿主直启冒烟 (绕开浏览器直接喂 precheck 帧, 把「宿主能不能跑」与「浏览器找没找到宿主」拆开) + 启动日志尾部。
$HostName = 'com.tao.taodown'
$diag = New-Object System.Collections.Generic.List[string]
foreach ($l in $HostLogs) {
  if (Test-Path $l) {
    $diag.Add("=== host.log: $l ===")
    Get-Content $l -Tail 40 -ErrorAction SilentlyContinue | ForEach-Object { $diag.Add($_) }
  } else { $diag.Add("=== host.log 不存在: $l ===") }
}
foreach ($l in @('D:\TaoDownData\TaoDown\logs\startup.log', "$env:LOCALAPPDATA\TaoDownData\TaoDown\logs\startup.log")) {
  if (Test-Path $l) {
    $diag.Add("=== startup.log: $l ===")
    Get-Content $l -Tail 60 -ErrorAction SilentlyContinue | ForEach-Object { $diag.Add($_) }
  } else { $diag.Add("=== startup.log 不存在: $l ===") }
}
$diag.Add('=== NativeMessagingHosts 登记键实况 (正确键名 com.tao.taodown) ===')
$manifestPath = $null
foreach ($root in @('Software\Google\Chrome\NativeMessagingHosts','Software\Microsoft\Edge\NativeMessagingHosts','Software\Chromium\NativeMessagingHosts','Software\BraveSoftware\Brave\NativeMessagingHosts','Software\Vivaldi\NativeMessagingHosts','Software\Yandex\YandexBrowser\NativeMessagingHosts')) {
  $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey("$root\$HostName")
  $val = if ($key) { $key.GetValue($null) } else { '<键缺失>' }
  if (-not $manifestPath -and $val -and (Test-Path $val)) { $manifestPath = $val }
  $manifestOk = ($val -and (Test-Path $val))
  $diag.Add("$root\$HostName = $val (清单文件存在=$manifestOk)")
}
if ($manifestPath) {
  $diag.Add("=== 宿主 manifest 内容: $manifestPath ===")
  Get-Content $manifestPath -Raw -ErrorAction SilentlyContinue | ForEach-Object { $diag.Add($_) }
  try {
    $mf = Get-Content $manifestPath -Raw | ConvertFrom-Json
    $hostExe = $mf.path
    $diag.Add("宿主 exe 在位=$(Test-Path $hostExe) ($hostExe)")
    # 宿主直启冒烟: 喂一条 precheck 帧, 断言应答 ok——宿主进程本身能不能跑, 与浏览器无关地验证
    $diag.Add('=== 宿主直启冒烟 (precheck 帧) ===')
    $psi = [System.Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = $hostExe
    $psi.UseShellExecute = $false
    $psi.RedirectStandardInput = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $proc = [System.Diagnostics.Process]::Start($psi)
    $payload = [Text.Encoding]::UTF8.GetBytes('{"type":"precheck"}')
    $lenBytes = [BitConverter]::GetBytes([UInt32]$payload.Length)
    $proc.StandardInput.BaseStream.Write($lenBytes, 0, 4)
    $proc.StandardInput.BaseStream.Write($payload, 0, $payload.Length)
    $proc.StandardInput.BaseStream.Flush()
    $proc.StandardInput.Close()
    if (-not $proc.WaitForExit(15000)) { $proc.Kill($true); $diag.Add('冒烟超时 15s 强杀') }
    $outBytes = New-Object byte[] 4
    $n = $proc.StandardOutput.BaseStream.Read($outBytes, 0, 4)
    if ($n -eq 4) {
      $respLen = [BitConverter]::ToUInt32($outBytes, 0)
      $respBuf = New-Object byte[] $respLen
      $off = 0
      while ($off -lt $respLen) { $r = $proc.StandardOutput.BaseStream.Read($respBuf, $off, $respLen - $off); if ($r -le 0) { break }; $off += $r }
      $diag.Add("冒烟应答: " + [Text.Encoding]::UTF8.GetString($respBuf, 0, $off))
    } else {
      $diag.Add("冒烟无应答 (stdout 头 4 字节读到 $n); stderr: " + $proc.StandardError.ReadToEnd())
    }
  } catch { $diag.Add("冒烟异常: $($_.Exception.Message)") }
} else {
  $diag.Add('=== 六家登记键全部缺失或清单文件不在位, 冒烟跳过 ===')
}
$diag.Add('=== DataRoot\native_host 目录实况 ===')
foreach ($d in @('D:\TaoDownData\TaoDown\native_host', "$env:LOCALAPPDATA\TaoDownData\TaoDown\native_host")) {
  if (Test-Path $d) { $diag.Add("$d : " + ((Get-ChildItem $d -ErrorAction SilentlyContinue | ForEach-Object Name) -join ', ')) }
  else { $diag.Add("$d 不存在") }
}
# 浏览器侧第一手日志: 各家 chrome_debug.log 里扩展/native 相关行落盘
$diag.Add('=== 浏览器侧 chrome_debug.log 摘录 (TaoDown/native/extension) ===')
foreach ($b in @('edge','chrome','chromium','brave','vivaldi','yandex')) {
  $dbg = Join-Path $PWD ("evidence\takeover-{0}-profile\chrome_debug.log" -f $b)
  if (Test-Path $dbg) {
    $diag.Add("--- $b ---")
    Get-Content $dbg -ErrorAction SilentlyContinue | Where-Object { $_ -match 'TaoDown|taodown|native|Native|dchnmdakoleaofgpkhddhmohcoomlimp' -and $_ -notmatch 'NativeEvent|native_event' } | Select-Object -Last 40 | ForEach-Object { $diag.Add($_) }
  } else { $diag.Add("--- $b : chrome_debug.log 不存在 ---") }
}
$diag | Set-Content evidence\takeover-diag.txt -Encoding UTF8

$report | ConvertTo-Json -Depth 5 | Set-Content evidence\takeover-report.json -Encoding UTF8
# 清理下载源（先关监听再停 Runspace，防 GetContext 死锁——human_sim v2 同口径）
$srv.Stop(); Start-Sleep -Seconds 2
try { $srcPs.Stop(); $srcRs.Close() } catch { }
# 清理测试下载残留，不污染后续班次
Get-ChildItem "$env:USERPROFILE\Downloads" -Filter 'takeover*' -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
Get-ChildItem "$env:USERPROFILE\Downloads" -Filter 'warmup*' -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue

if ($failed.Count -gt 0) { throw ("下载接管实测不过（六家逐家口径）：{0}" -f ($failed -join '；')) }
Write-Host '下载接管实测通过：六家浏览器逐家真触发下载、转交进收件箱、浏览器侧零成品文件'
