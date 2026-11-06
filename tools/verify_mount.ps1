# 云端真机「自动挂载」验证（v2.4.x 全内核覆盖版）：
# ① 登记核验：BrowserCatalog 全家族 Extensions\<扩展ID> 键（HKCU 必查），path 指向真实插件包、version 与清单一致，旧 ID 键必须清干净；
# ② 真挂载核验·全内核覆盖：对自动挂载清单内的全部 Chromium 内核浏览器——Edge、Chrome、Chromium、Brave、Vivaldi、Yandex——
#    runner 上没有的现场安装（choco 优先、winget 兜底），逐个真启动，扩展 ID 必须进入用户资料（Secure Preferences/Preferences）；
#    夸克/360 为手动挂载内核（不认系统级登记，见 BROWSER-EXTENSION.md「手动加载」），自动挂载对其不适用，覆盖口径=登记键核验+手动加载步骤文档化；
#    Firefox 采用独立扩展体系、无系统级自动挂载机制，不在覆盖清单。
# 证据全部落盘 evidence\；任何一关不过就整步报红——「装不上」同样报红，不允许静默跳过。
$ErrorActionPreference = 'Stop'
$Id      = 'dchnmdakoleaofgpkhddhmohcoomlimp'
$Legacy  = 'mhbhaogipooncclfkmcdfhldfbpjakgi'
# 与 src/TaoDown.Core/BrowserCatalog.cs ChromiumFamilies 同套同源（NativeMessagingHosts→Extensions，去重）
$Roots = @(
  'Software\Google\Chrome\Extensions',
  'Software\Microsoft\Edge\Extensions',
  'Software\Chromium\Extensions',
  'Software\BraveSoftware\Brave\Extensions',
  'Software\BraveSoftware\Brave-Browser\Extensions',
  'Software\Vivaldi\Extensions',
  'Software\Opera Software\Extensions',
  'Software\Yandex\YandexBrowser\Extensions',
  'Software\YandexBrowser\Extensions',
  'Software\Quark\Extensions',
  'Software\Quark\Quark\Extensions',
  'Software\UC\Quark\Extensions',
  'Software\QuarkBrowser\Extensions',
  'Software\360Chrome\Chrome\Extensions',
  'Software\360ChromeX\Chrome\Extensions',
  'Software\360SE\Chrome\Extensions',
  'Software\360se6\Chrome\Extensions'
)
$report = [ordered]@{ hkcuEntries = @(); hkcuValid = 0; hkcuTotal = $Roots.Count; legacyLeftover = 0; mounts = [ordered]@{} }

Write-Host '=== 自动挂载·登记核验（全 17 家族键） ==='
$bad = New-Object System.Collections.Generic.List[string]
foreach ($root in $Roots) {
  $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey("$root\$Id")
  if (-not $key) { $bad.Add("HKCU\$root\$Id 缺失"); continue }
  $p = $key.GetValue('path'); $v = $key.GetValue('version')
  if (-not $p -or -not (Test-Path $p)) { $bad.Add("HKCU\$root\$Id path 无效: $p"); continue }
  $dir = (Test-Path $p -PathType Container) ? $p : (Split-Path $p -Parent)
  $manifest = Join-Path $dir 'manifest.json'
  if (-not (Test-Path $manifest)) { $bad.Add("HKCU\$root\$Id 清单缺失: $manifest"); continue }
  $mv = (Get-Content $manifest -Raw | ConvertFrom-Json).version
  $ok = ($v -eq $mv)
  Write-Host ("  {0}: path={1} 键版本={2} 清单版本={3} {4}" -f $root, $p, $v, $mv, ($(if ($ok) {'OK'} else {'版本不一致'})))
  if (-not $ok) { $bad.Add("HKCU\$root\$Id 版本不一致: 键=$v 清单=$mv") } else { $report.hkcuValid++ }
}
$report.hkcuEntries = $Roots
foreach ($root in $Roots) {
  foreach ($hive in @([Microsoft.Win32.Registry]::CurrentUser, [Microsoft.Win32.Registry]::LocalMachine)) {
    if ($hive.OpenSubKey("$root\$Legacy")) { $report.legacyLeftover++; Write-Host "  旧 ID 键残留: $($hive.Name)\$root\$Legacy" }
  }
}
if ($report.hkcuValid -ne $Roots.Count) { throw "登记核验不过：HKCU 有效 $($report.hkcuValid)/$($Roots.Count)，问题：$($bad -join '; ')" }
if ($report.legacyLeftover -gt 0) { throw "登记核验不过：旧 ID 键残留 $($report.legacyLeftover) 处（会重复接管）" }
Write-Host "登记核验通过：HKCU 全 $($Roots.Count) 个家族键齐、版本一致、旧 ID 清净"

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

function Test-BrowserMount([string]$Browser, [string]$Exe, [string]$ProcName, [string]$ProfileDir, [int]$Rounds = 45) {
  Write-Host ("  {0} 版本: {1}" -f $Browser, (Get-Item $Exe).VersionInfo.ProductVersion)
  Remove-Item $ProfileDir -Recurse -Force -ErrorAction SilentlyContinue
  Start-Process $Exe -ArgumentList "--no-first-run","--user-data-dir=`"$ProfileDir`"","--enable-logging","--v=1","about:blank"
  $found = $null
  for ($i = 0; $i -lt $Rounds; $i++) {
    Start-Sleep -Seconds 2
    foreach ($file in @('Secure Preferences','Preferences')) {
      $pf = Join-Path $ProfileDir "Default\$file"
      if (-not (Test-Path $pf)) { continue }
      try { $json = Get-Content $pf -Raw -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop } catch { continue }
      $settings = $json.extensions.settings
      if (-not $settings) { continue }
      $names = @($settings.PSObject.Properties.Name)
      if ($i -eq 0 -or $names.Count -gt 0) { Write-Host ("  轮次 {0} {1} extensions.settings 共 {2} 项: {3}" -f $i, $file, $names.Count, ($names -join ',')) }
      $prop = $settings.PSObject.Properties | Where-Object { $_.Name -eq $Id }
      if ($prop) {
        $found = [ordered]@{ browser = $Browser; file = $file; path = $prop.Value.path; state = $prop.Value.state; location = $prop.Value.location }
        break
      }
    }
    if ($found) { break }
  }
  Get-Process $ProcName -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
  Start-Sleep -Seconds 2
  return $found
}

# 手动加载实测（内核不认登记挂载时走这条路）：--load-extension 解包扩展目录，验证插件本体在该内核真实可载
function Test-BrowserManualLoad([string]$Browser, [string]$Exe, [string]$ProcName, [string]$ProfileDir, [string]$ExtDir) {
  Write-Host ("  {0} 手动加载实测：--load-extension {1}" -f $Browser, $ExtDir)
  Remove-Item $ProfileDir -Recurse -Force -ErrorAction SilentlyContinue
  Start-Process $Exe -ArgumentList "--no-first-run","--no-default-browser-check","--enable-logging","--v=1","--user-data-dir=`"$ProfileDir`"","--load-extension=`"$ExtDir`"","about:blank"
  # 诊断：进程起没起、资料目录建没建（Opera 疑似不认 Chromium 通用参数，先坐实现场）
  Start-Sleep -Seconds 4
  $proc = Get-Process $ProcName -ErrorAction SilentlyContinue
  Write-Host ("  {0} 进程探测: {1}" -f $Browser, $(if ($proc) { "运行中 pid=$($proc.Id -join ',')" } else { '未启动' }))
  if (-not (Test-Path $ProfileDir)) { Write-Host ("  {0} 资料目录未创建: {1}" -f $Browser, $ProfileDir) }
  else { Write-Host ("  {0} 资料目录内容: {1}" -f $Browser, ((Get-ChildItem $ProfileDir -ErrorAction SilentlyContinue | Select-Object -First 8 | ForEach-Object Name) -join ', ')) }
  $found = $null
  for ($i = 0; $i -lt 45; $i++) {
    Start-Sleep -Seconds 2
    foreach ($file in @('Secure Preferences','Preferences')) {
      $pf = Join-Path $ProfileDir "Default\$file"
      if (-not (Test-Path $pf)) { continue }
      try { $json = Get-Content $pf -Raw -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop } catch { continue }
      $settings = $json.extensions.settings
      if (-not $settings) { continue }
      $prop = $settings.PSObject.Properties | Where-Object { $_.Name -eq $Id }
      if ($prop) {
        $found = [ordered]@{ browser = $Browser; mode = 'manual-load'; file = $file; path = $prop.Value.path; state = $prop.Value.state }
        break
      }
    }
    if ($found) { break }
  }
  Get-Process $ProcName -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
  Start-Sleep -Seconds 2
  return $found
}

# 失败诊断：逐项 dump 资料里的扩展详情（ID/path/state/location），谁在资料里一目了然
function Dump-ProfileExtensions([string]$ProfileDir, [string]$Label) {
  Write-Host "  === 扩展详情 dump（$Label） ==="
  foreach ($file in @('Secure Preferences','Preferences')) {
    $pf = Join-Path $ProfileDir "Default\$file"
    if (-not (Test-Path $pf)) { continue }
    try { $json = Get-Content $pf -Raw | ConvertFrom-Json -ErrorAction Stop } catch { Write-Host "  $pf 解析失败"; continue }
    $settings = $json.extensions.settings
    if (-not $settings) { continue }
    foreach ($prop in $settings.PSObject.Properties) {
      $v = $prop.Value
      Write-Host ("  [{0}] {1} path={2} state={3} location={4} name={5}" -f $file, $prop.Name, $v.path, $v.state, $v.location, $v.manifest.name)
    }
  }
}

# 全内核覆盖矩阵：ExeFile=进程/主程序名，Choco/Winget=云端补装来源
$Browsers = @(
  @{ Name='Edge';     Proc='msedge';  ExeFile='msedge.exe';  Choco=$null;             Winget=$null;                 Exes=@('C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe','C:\Program Files\Microsoft\Edge\Application\msedge.exe') },
  @{ Name='Chrome';   Proc='chrome';  ExeFile='chrome.exe';  Choco='googlechrome';    Winget='Google.Chrome';       Exes=@('C:\Program Files\Google\Chrome\Application\chrome.exe','C:\Program Files (x86)\Google\Chrome\Application\chrome.exe') },
  @{ Name='Chromium'; Proc='chrome';  ExeFile='chrome.exe';  Choco='chromium';        Winget='Hibbiki.Chromium';    Exes=@('C:\Program Files\Chromium\Application\chrome.exe') },
  @{ Name='Brave';    Proc='brave';   ExeFile='brave.exe';   Choco='brave';           Winget='BraveSoftware.BraveBrowser'; Exes=@('C:\Program Files\BraveSoftware\Brave-Browser\Application\brave.exe','C:\Program Files (x86)\BraveSoftware\Brave-Browser\Application\brave.exe') },
  @{ Name='Vivaldi';  Proc='vivaldi'; ExeFile='vivaldi.exe'; Choco='vivaldi';         Winget='Vivaldi.Vivaldi';     Exes=@('C:\Program Files\Vivaldi\Application\vivaldi.exe',"$env:LOCALAPPDATA\Vivaldi\Application\vivaldi.exe") },
  # Opera 用户拍板免测（2026-11-07「抛弃它不管了，反正我也不用」）：登记键核验保留，真启动/手动加载不再要求
  @{ Name='Yandex';   Proc='browser'; ExeFile='browser.exe'; Choco='yandex-browser'; Winget='Yandex.Browser';      Rounds=45; ManualExt=$false; Exes=@('C:\Program Files\Yandex\YandexBrowser\browser.exe',"$env:LOCALAPPDATA\Yandex\YandexBrowser\Application\browser.exe") }
)

function Get-BrowserExe($spec) {
  foreach ($p in $spec.Exes) { if (Test-Path $p) { return $p } }
  foreach ($base in @('C:\Program Files','C:\Program Files (x86)',$env:LOCALAPPDATA)) {
    if (-not (Test-Path $base)) { continue }
    $hit = Get-ChildItem $base -Recurse -Depth 3 -Filter $spec.ExeFile -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($hit) { return $hit.FullName }
  }
  return $null
}

function Install-Browser($spec) {
  if (-not $spec.Choco -and -not $spec.Winget) { return $false }
  if ($spec.Choco) {
    Write-Host ("  {0} 不在 runner 上，现场安装：choco install {1}" -f $spec.Name, $spec.Choco)
    choco install $spec.Choco -y --no-progress 2>&1 | ForEach-Object { Write-Host "  choco: $_" }
    if ($LASTEXITCODE -in 0,1641,3010) { Write-Host '  choco 安装完成'; return $true }
    Write-Host "  choco 退出码 $LASTEXITCODE"
  }
  if ($spec.Winget) {
    Write-Host ("  winget 兜底: {0}" -f $spec.Winget)
    winget install --id $spec.Winget --silent --accept-package-agreements --accept-source-agreements --disable-interactivity 2>&1 | ForEach-Object { Write-Host "  winget: $_" }
    Write-Host ("  winget 退出码: {0}" -f $LASTEXITCODE)
    if ($LASTEXITCODE -eq 0) { Write-Host '  winget 安装完成'; return $true }
  }
  return $false
}

$failed = New-Object System.Collections.Generic.List[string]
foreach ($spec in $Browsers) {
  Write-Host ("=== 自动挂载·真挂载核验（{0}） ===" -f $spec.Name)
  $exe = Get-BrowserExe $spec
  if (-not $exe -and $spec.Choco) {
    if (Install-Browser $spec) { $exe = Get-BrowserExe $spec }
  }
  if (-not $exe) {
    $failed.Add("$($spec.Name)：云端装不上，未覆盖")
    Write-Host ("  {0} 安装失败，记入未覆盖（报红不静默）" -f $spec.Name)
    continue
  }
  Snap ("evidence\mount-{0}-before.png" -f $spec.Name.ToLower())
  $prof = Join-Path $PWD ("evidence\{0}-profile" -f $spec.Name.ToLower())
  $rounds = if ($spec.Rounds) { [int]$spec.Rounds } else { 45 }
  $hit = Test-BrowserMount -Browser $spec.Name -Exe $exe -ProcName $spec.Proc -ProfileDir $prof -Rounds $rounds
  Snap ("evidence\mount-{0}-after.png" -f $spec.Name.ToLower())
  if (-not $hit) {
    Write-Host ("  {0} 插件未进用户资料，诊断补充 ===" -f $spec.Name)
    Dump-ProfileExtensions $prof $spec.Name
    # 内核不认登记挂载时的手动加载实测（当前仅 Opera 需要此兜底）
    if ($spec.ManualExt) {
      $extDir = (Resolve-Path (Join-Path $PSScriptRoot '..\src\extension')).Path
      if (Test-Path (Join-Path $extDir 'manifest.json')) {
        Write-Host ("  {0} 登记 90*2 秒未自动挂载，转手动加载实测" -f $spec.Name)
        $hit = Test-BrowserManualLoad -Browser $spec.Name -Exe $exe -ProcName $spec.Proc -ProfileDir $prof -ExtDir $extDir
        Snap ("evidence\mount-{0}-manual.png" -f $spec.Name.ToLower())
        if ($hit) {
          Write-Host ("  {0} 手动加载实测通过：path={1} state={2}（内核不认登记挂载，归入手动加载内核组，回灌文档）" -f $spec.Name, $hit.path, $hit.state)
          $report.mounts[$spec.Name] = $hit
          continue
        }
        Write-Host ("  {0} 手动加载实测也未通过" -f $spec.Name)
      } else { Write-Host '  仓库内解包扩展目录不存在，手动加载实测无法进行' }
    }
    $dbg = Join-Path $prof 'chrome_debug.log'
    if (Test-Path $dbg) {
      Write-Host ("  === {0} chrome_debug.log 关键行 ===" -f $spec.Name)
      Get-Content $dbg -ErrorAction SilentlyContinue | Where-Object { $_ -match 'extension|Extension|external|External|registry|Registry' -and $_ -notmatch 'OnLoaded|Already' } | Select-Object -Last 120 | ForEach-Object { Write-Host "  $_" }
    } else { Write-Host "  无 chrome_debug.log: $dbg" }
    $failed.Add("$($spec.Name)：真启动 90 秒登记插件未装进用户资料")
    continue
  }
  Write-Host ("  {0} 挂载成功：path={1} state={2}" -f $spec.Name, $hit.path, $hit.state)
  $report.mounts[$spec.Name] = $hit
}
$report | ConvertTo-Json -Depth 5 | Set-Content evidence\mount-report.json -Encoding UTF8
if ($failed.Count -gt 0) { throw ("真挂载核验不过（全内核覆盖口径）：{0}" -f ($failed -join '；')) }
Write-Host '自动挂载全链通过：登记键齐、版本一致、旧 ID 清净、全部 Chromium 内核浏览器真挂载成功（夸克/360 为手动挂载内核，覆盖口径=登记核验）'
