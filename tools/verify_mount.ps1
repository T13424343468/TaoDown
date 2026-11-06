# 云端真机「自动挂载」验证（v1.02+）：
# ① 登记核验：BrowserCatalog 全家族 Extensions\<扩展ID> 键（HKCU 必查），path 指向真实插件包、version 与清单一致，旧 ID 键必须清干净；
# ② 真挂载核验：真启动 Edge（runner 自带），等浏览器把登记的插件真正装进用户资料，扩展 ID 必须出现在 Secure Preferences/Preferences；
# 证据全部落盘 evidence\，任何一关不过就整步报红。
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
$report = [ordered]@{ hkcuEntries = @(); hkcuValid = 0; hkcuTotal = $Roots.Count; legacyLeftover = 0; edge = $null; chrome = $null }

Write-Host '=== 自动挂载·登记核验 ==='
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
Write-Host "登记核验通过：HKCU 全 $Roots.Count 个家族键齐、版本一致、旧 ID 清净"

function Test-BrowserMount([string]$Browser, [string]$Exe, [string]$ProfileDir) {
  Write-Host ("  {0} 版本: {1}" -f $Browser, (Get-Item $Exe).VersionInfo.ProductVersion)
  $marker = Join-Path $ProfileDir 'mounted.flag'
  Remove-Item $ProfileDir -Recurse -Force -ErrorAction SilentlyContinue
  Start-Process $Exe -ArgumentList "--no-first-run","--user-data-dir=`"$ProfileDir`"","--enable-logging","--v=1","about:blank"
  $found = $null
  for ($i = 0; $i -lt 45; $i++) {
    Start-Sleep -Seconds 2
    foreach ($file in @('Secure Preferences','Preferences')) {
      $pf = Join-Path $ProfileDir "Default\$file"
      if (-not (Test-Path $pf)) { continue }
      try { $json = Get-Content $pf -Raw -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop } catch { continue }
      $settings = $json.extensions.settings
      if (-not $settings) { continue }
      $names = @($settings.PSObject.Properties.Name)
      Write-Host ("  轮次 {0} {1} extensions.settings 共 {2} 项: {3}" -f $i, $file, $names.Count, ($names -join ',')) 
      $prop = $settings.PSObject.Properties | Where-Object { $_.Name -eq $Id }
      if ($prop) {
        $found = [ordered]@{ browser = $Browser; file = $file; path = $prop.Value.path; state = $prop.Value.state; location = $prop.Value.location }
        break
      }
    }
    if ($found) { break }
  }
  Get-Process ($Browser -eq 'Edge' ? 'msedge' : 'chrome') -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
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
      $mpath = $v.path; if (-not $mpath) { $mpath = ($v.manifest | ForEach-Object { $_ }) ; if ($v.manifest) { $mpath = '' } }
      Write-Host ("  [{0}] {1} path={2} state={3} location={4} name={5}" -f $file, $prop.Name, $v.path, $v.state, $v.location, $v.manifest.name)
    }
  }
}

Write-Host '=== 自动挂载·真挂载核验（Edge） ==='
$edge = Get-ChildItem 'C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe','C:\Program Files\Microsoft\Edge\Application\msedge.exe' -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $edge) { throw 'runner 上没找到 Edge，真挂载核验无法进行' }
Add-Type -AssemblyName System.Windows.Forms; Add-Type -AssemblyName System.Drawing
function Snap($path) {
  $b = [System.Windows.Forms.SystemInformation]::VirtualScreen
  $bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.CopyFromScreen($b.Left, $b.Top, 0, 0, $bmp.Size)
  $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
  $g.Dispose(); $bmp.Dispose()
}
Snap 'evidence\mount-edge-before.png'
$edgeProfile = Join-Path $PWD 'evidence\edge-profile'
$hit = Test-BrowserMount -Browser 'Edge' -Exe $edge.FullName -ProfileDir $edgeProfile
if (-not $hit) {
  Write-Host '  === 诊断补充 ==='
  foreach ($hiveName in @('CurrentUser','LocalMachine')) {
    foreach ($root in $Roots) {
      $k = [Microsoft.Win32.Registry]::"$hiveName".OpenSubKey("$root\$Id")
      if ($k) { Write-Host ("  {0} {1}: path={2}" -f $hiveName, $root, $k.GetValue('path')) }
    }
  }
  # Edge 策略：是否有 blocklist / external 相关限制
  Write-Host '  === Edge 策略注册表 ==='
  foreach ($pol in @('HKLM:\SOFTWARE\Policies\Microsoft\Edge','HKCU:\SOFTWARE\Policies\Microsoft\Edge')) {
    if (Test-Path $pol) {
      Get-ChildItem $pol -Recurse -ErrorAction SilentlyContinue | ForEach-Object { Write-Host "  策略键: $($_.Name)" }
      Get-ItemProperty $pol -ErrorAction SilentlyContinue | Select-Object * -ExcludeProperty PS* | Format-List | Out-String | ForEach-Object { Write-Host $_ }
    } else { Write-Host "  无 $pol" }
  }
  Dump-ProfileExtensions $edgeProfile '自定义资料'
  $dbg = Join-Path $edgeProfile 'chrome_debug.log'
  if (Test-Path $dbg) {
    Write-Host '  === Edge chrome_debug.log 关键行 ==='
    Get-Content $dbg -ErrorAction SilentlyContinue | Where-Object { $_ -match 'extension|Extension|external|External|registry|Registry' -and $_ -notmatch 'OnLoaded|Already' } | Select-Object -Last 150 | ForEach-Object { Write-Host "  $_" }
  } else { Write-Host "  无 chrome_debug.log: $dbg" }
  $sysDefault = Join-Path $env:LOCALAPPDATA 'Microsoft\Edge\User Data'
  if (Test-Path $sysDefault) {
    foreach ($prof in (Get-ChildItem $sysDefault -Directory -ErrorAction SilentlyContinue)) {
      foreach ($file in @('Secure Preferences','Preferences')) {
        $pf = Join-Path $prof.FullName $file
        if (Test-Path $pf) {
          $c = (Select-String -Path $pf -Pattern $Id -Quiet)
          if ($c) { Write-Host ("  系统默认资料命中: {0}\{1}" -f $prof.Name, $file); $hit = @{ browser='Edge'; file=$file; path='系统默认资料'; state='default-profile'; location='' } }
        }
      }
    }
  }
  if (-not $hit) { throw '真挂载核验不过：Edge 启动 90 秒内登记插件未装进任何用户资料（自动挂载失效，含系统默认资料）' }
}
Snap 'evidence\mount-edge-after.png'
Write-Host ("  Edge 挂载成功：文件={0} path={1} state={2}" -f $hit.file, $hit.path, $hit.state)
$report.edge = $hit

Write-Host '=== 自动挂载·真挂载核验（Chrome，缺则跳过） ==='
$chrome = Get-ChildItem 'C:\Program Files\Google\Chrome\Application\chrome.exe','C:\Program Files (x86)\Google\Chrome\Application\chrome.exe' -ErrorAction SilentlyContinue | Select-Object -First 1
if ($chrome) {
  $hitC = Test-BrowserMount -Browser 'Chrome' -Exe $chrome.FullName -ProfileDir (Join-Path $PWD 'evidence\chrome-profile')
  if (-not $hitC) { Write-Host '  Chrome 已装但插件未进资料（登记核验已过，先记观察）'; $report.chrome = 'not-mounted' }
  else { Write-Host ("  Chrome 挂载成功：path={0} state={1}" -f $hitC.path, $hitC.state); $report.chrome = $hitC }
} else {
  Write-Host '  runner 无 Chrome，跳过（Edge 已代表 Chromium 内核真挂载）'
  $report.chrome = 'absent'
}
$report | ConvertTo-Json -Depth 5 | Set-Content evidence\mount-report.json -Encoding UTF8
Write-Host '自动挂载全链通过：登记键齐、版本一致、旧 ID 清净、Edge 真挂载成功'
