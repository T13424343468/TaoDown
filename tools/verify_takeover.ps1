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

# ---- CDP 模拟用户启用（Chrome 系外部扩展的一次性确认, run 45116390905 口径）----
# Google 品牌 Chrome 137+ 无视 --load-extension (chrome_debug.log 原话: "--load-extension is not
# allowed in Google Chrome, ignoring", 恢复开关 DisableLoadExtensionCommandLineSwitch 在 157 实测无效),
# 扩展只能走注册表外部安装一路, 装上即待用户确认 (disable_reasons=8192)。真实用户在
# chrome://extensions 开一次开关即启用——该开关的实现就是 chrome.management.setEnabled
# (chromium chrome/browser/resources/extensions/service.ts setItemEnabled)。这里用 CDP 在
# chrome://extensions 页面上下文里调同一 API 模拟用户操作, 不静默跳过: 失败即报红。
$CdpPort = 9223
function Enable-ExtensionAsUser([int]$Port, [string]$ExtId) {
  $tgt = $null
  foreach ($m in @('PUT', 'GET')) {
    try { $tgt = Invoke-RestMethod -Method $m -Uri "http://127.0.0.1:$Port/json/new?chrome://extensions/" -TimeoutSec 10; break } catch { }
  }
  if (-not $tgt -or -not $tgt.webSocketDebuggerUrl) { throw 'CDP 打开 chrome://extensions 失败' }
  $ws = [System.Net.WebSockets.ClientWebSocket]::new()
  $ws.ConnectAsync([Uri]$tgt.webSocketDebuggerUrl, [System.Threading.CancellationToken]::None).GetAwaiter().GetResult()
  try {
    $buf = New-Object byte[] (1MB)
    $send = {
      param($obj)
      $bytes = [Text.Encoding]::UTF8.GetBytes(($obj | ConvertTo-Json -Depth 6 -Compress))
      $ws.SendAsync([ArraySegment[byte]]::new($bytes), [System.Net.WebSockets.WebSocketMessageType]::Text, $true, [System.Threading.CancellationToken]::None).GetAwaiter().GetResult()
    }
    $recv = {
      param($wantId)
      $deadline = [DateTime]::UtcNow.AddSeconds(30)
      while ([DateTime]::UtcNow -lt $deadline) {
        $ms = [System.IO.MemoryStream]::new()
        while ($true) {
          $seg = [ArraySegment[byte]]::new($buf)
          $r = $ws.ReceiveAsync($seg, [System.Threading.CancellationToken]::None).GetAwaiter().GetResult()
          if ($r.MessageType -eq [System.Net.WebSockets.WebSocketMessageType]::Close) { throw 'CDP 连接被对端关闭' }
          $ms.Write($buf, 0, $r.Count)
          if ($r.EndOfMessage) { break }
        }
        $msg = [Text.Encoding]::UTF8.GetString($ms.ToArray()) | ConvertFrom-Json
        if ($msg.id -ne $wantId) { continue }  # 事件帧跳过, 只认命令回包
        return $msg
      }
      return $null
    }
    # 等页面就绪（WebUI 绑定加载完才有 chrome.management）
    $ready = $false
    for ($i = 0; $i -lt 20; $i++) {
      Start-Sleep -Milliseconds 800
      & $send @{ id = 1; method = 'Runtime.evaluate'; params = @{ expression = "typeof chrome !== 'undefined' && typeof chrome.management !== 'undefined'"; returnByValue = $true } }
      $resp = & $recv 1
      if ($resp -and $resp.result -and $resp.result.result -and $resp.result.result.value -eq $true) { $ready = $true; break }
    }
    if (-not $ready) {
      # Yandex 实锤 (run 45146764146): chrome.management 在其扩展页 WebUI 不可用,
      # 退回真实用户口径的另一实现——递归穿 shadow DOM 找到扩展条目的 cr-toggle 真点一次
      # (与页面开关同一控件, chromium extensions_item.html cr-toggle#enableToggle)。
      # 点击后由调用方轮询 disable_reasons 断言启用落盘, 点不上照旧报红不静默。
      Write-Host '  chrome.management 不可用，退回页面开关 DOM 点击…'
      $domJs = @'
(async () => {
  const sleep = ms => new Promise(r => setTimeout(r, ms));
  for (let t = 0; t < 20; t++) {
    const items = [];
    const walk = (root) => {
      for (const el of root.querySelectorAll('*')) {
        if (el.tagName === 'EXTENSIONS-ITEM') items.push(el);
        if (el.shadowRoot) walk(el.shadowRoot);
      }
    };
    walk(document);
    for (const item of items) {
      if (item.id && item.id !== '__EXTID__') continue;
      const sr = item.shadowRoot;
      if (!sr) continue;
      const toggle = sr.querySelector('cr-toggle');
      if (!toggle) continue;
      if (toggle.checked) return 'already-on';
      toggle.click();
      return 'clicked';
    }
    await sleep(500);
  }
  return 'no-toggle';
})()
'@
      $domJs = $domJs.Replace('__EXTID__', $ExtId)
      & $send @{ id = 3; method = 'Runtime.evaluate'; params = @{ expression = $domJs; awaitPromise = $true; returnByValue = $true } }
      $resp3 = & $recv 3
      if (-not $resp3) { throw 'CDP DOM 开关点击无回包' }
      if ($resp3.error) { throw ("CDP DOM 开关点击失败: " + ($resp3.error | ConvertTo-Json -Compress)) }
      if ($resp3.result.exceptionDetails) { throw ("CDP DOM 开关点击异常: " + ($resp3.result.exceptionDetails | ConvertTo-Json -Compress)) }
      Write-Host ("  DOM 开关点击结果: {0}" -f $resp3.result.result.value)
      return
    }
    & $send @{ id = 2; method = 'Runtime.evaluate'; params = @{ expression = "chrome.management.setEnabled('$ExtId', true)"; awaitPromise = $true; returnByValue = $true } }
    $resp2 = & $recv 2
    if (-not $resp2) { throw 'CDP setEnabled 无回包' }
    if ($resp2.error) { throw ("CDP setEnabled 失败: " + ($resp2.error | ConvertTo-Json -Compress)) }
    if ($resp2.result.exceptionDetails) { throw ("CDP setEnabled 异常: " + ($resp2.result.exceptionDetails | ConvertTo-Json -Compress)) }
  } finally { $ws.Dispose() }
}

# 读资料目录里扩展的 disable_reasons（挂载≠启用, 非空即待处理）
function Get-DisableReasons([string]$ProfileDir, [string]$ExtId) {
  foreach ($file in @('Secure Preferences', 'Preferences')) {
    $pf = Join-Path $ProfileDir "Default\$file"
    if (-not (Test-Path $pf)) { continue }
    try {
      $j = Get-Content $pf -Raw -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop
      $es = $j.extensions.settings
      if ($es -and ($es.PSObject.Properties.Name -contains $ExtId)) { return $es.$ExtId.disable_reasons }
    } catch { continue }
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
# run 45029338370 再实锤 (Secure Preferences 亲读): 上轮只清 HKCU 且没停应用——应用常驻且每 30s
# 刷新接管注册表 (NativeHostRegistrar.WriteLocalExtensionEntries HKCU+HKLM 双写, startup.log
# 「接管注册表已刷新 插件外链=17」), 清完 28s 内被回写, Chrome 启动时外部登记在位 → 仍 location=3
# + 8192。修法: 先停主程序再清 HKCU+HKLM 双蜂巢 (收件箱由宿主独立落盘, TaoDown.Host 只写 inbox
# 不依赖主程序, 接管判定不受影响; 关4 bigfile_regression 自行拉起应用)。
# run 45033054105 实锤: Get-Process 'TaoDown' 杀不中主程序 (应用宿主进程名不叫 TaoDown, 可执行文件
# Path 才含 TaoDown), 清键后 28s 被每 30s 刷新的接管注册表回写, Chrome 启动时外部登记在位 → 仍
# location=3+8192。改为按可执行文件 Path 多路匹配强杀, 杀完断言进程清零。
# run 45116390905 证据包定谳 (Secure Preferences 亲读 + chrome_debug.log 原话):
#   ① 清键断言全过、主程序杀净, Chrome/Chromium/Yandex 的扩展仍 location=3 (EXTERNAL_REGISTRY,
#      path=资料内 Extensions\dchnmda...\1.2.11_0, from_webstore=false) —— 漏网之源是 MSI 装机写的
#      32 位视图登记键 (tools/extension/registry_paths.json wow_paths_64bit_windows:
#      Software\Wow6432Node\Google\Chrome\Extensions + Wow6432Node\Microsoft\Edge\Extensions),
#      上轮清理只清 64 位视图, WOW 键在位 → Chrome 启动时从 WOW 键装 CRX。修法: 清理清单补两个 WOW 键。
#   ② Chrome 157 日志原话 "--load-extension is not allowed in Google Chrome, ignoring" ——
#      --disable-features=DisableLoadExtensionCommandLineSwitch 恢复路在品牌版 157 实测无效,
#      Google 品牌 Chrome/Chromium (choco 包实为品牌内核) 的扩展只剩注册表外部安装一路。
#   ③ 真实用户口径: 装机后 Chrome 系扩展经注册表外部安装进资料目录、装上即待用户确认
#      (disable_reasons 8192=DISABLE_EXTERNAL_EXTENSION, Yandex 1), 用户在 chrome://extensions
#      开一次开关即启用——开关实现就是 chrome.management.setEnabled (chromium
#      chrome/browser/resources/extensions/service.ts setItemEnabled)。本步对 Chrome/Chromium/Yandex
#      重写登记键模拟装机状态, 用 CDP 在 chrome://extensions 页面调同一 API 模拟用户开关,
#      断言启用态落盘后再实测接管——不静默跳过, 启用失败即报红。Edge/Brave/Vivaldi 非 Google 品牌
#      仍走 --load-extension 未打包加载 (location=8), 口径不变。
Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.Path -and ($_.Path -like '*TaoDown*') } | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 2
$leftApp = @(Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.Path -and ($_.Path -like '*TaoDown*') })
if ($leftApp.Count -gt 0) { throw ("主程序未杀净: " + (($leftApp | ForEach-Object { "$($_.Id):$($_.Path)" }) -join '; ')) }
# 清键前先抄下装机登记的 CRX 路径（关3 由应用写入, 与 MSI 装机登记同值）——
# Chrome/Chromium/Yandex 走真实用户路径时要用它重写登记键
$CrxPath = $null
foreach ($probe in @('HKCU:\Software\Google\Chrome\Extensions', 'HKLM:\Software\Google\Chrome\Extensions')) {
  $k = Get-Item (Join-Path $probe $Id) -ErrorAction SilentlyContinue
  if ($k) { $p = $k.GetValue('path'); if ($p -and (Test-Path $p)) { $CrxPath = $p; break } }
}
if ($CrxPath) { Write-Host ("  装机登记 CRX 路径: {0}" -f $CrxPath) }
$extRegRoots = @(
  'HKCU:\Software\Google\Chrome\Extensions',
  'HKLM:\Software\Google\Chrome\Extensions',
  'HKCU:\Software\Chromium\Extensions',
  'HKLM:\Software\Chromium\Extensions',
  'HKCU:\Software\Microsoft\Edge\Extensions',
  'HKLM:\Software\Microsoft\Edge\Extensions',
  'HKCU:\Software\BraveSoftware\Brave\Extensions',
  'HKLM:\Software\BraveSoftware\Brave\Extensions',
  'HKCU:\Software\BraveSoftware\Brave-Browser\Extensions',
  'HKLM:\Software\BraveSoftware\Brave-Browser\Extensions',
  'HKCU:\Software\Vivaldi\Extensions',
  'HKLM:\Software\Vivaldi\Extensions',
  'HKCU:\Software\Yandex\YandexBrowser\Extensions',
  'HKLM:\Software\Yandex\YandexBrowser\Extensions',
  'HKCU:\Software\YandexBrowser\Extensions',
  'HKLM:\Software\YandexBrowser\Extensions',
  # run 45116390905 实锤: MSI 装机还写了 32 位视图登记键 (tools/extension/registry_paths.json
  # wow_paths_64bit_windows), 上轮清理只清 64 位视图, Wow6432Node 键漏网——Chrome/Chromium/Yandex
  # 启动时从 WOW 键把 CRX 装成外部安装 (location=3), Google 品牌内核强制校验打 8192, Yandex 打 1。
  'HKLM:\Software\Wow6432Node\Google\Chrome\Extensions',
  'HKLM:\Software\Wow6432Node\Microsoft\Edge\Extensions'
)
foreach ($root in $extRegRoots) {
  try {
    if (Test-Path $root) {
      Remove-Item (Join-Path $root $Id) -Recurse -Force -ErrorAction SilentlyContinue
      # run 45033054105 教训: Remove-Item 失败被 SilentlyContinue 吞掉仍打印成功——清完必须 Test-Path 断言
      if (Test-Path (Join-Path $root $Id)) { throw "清后仍存在" }
      Write-Host ("  清外部登记 {0}\{1}" -f $root, $Id)
    }
  } catch {
    Write-Host ("  外部登记清理失败 {0}: {1}" -f $root, $_.Exception.Message)
    $failed.Add("外部登记清理失败 ${root}（清后仍存在，主程序回写或权限不足）")
  }
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

  # Chrome/Chromium 走真实用户路径 (run 45116390905 口径): Google 品牌 Chrome 137+ 无视
  # --load-extension (chrome_debug.log 原话 "--load-extension is not allowed in Google Chrome,
  # ignoring"), 恢复开关在 157 实测无效; 真实装机后扩展经注册表外部安装进资料目录、装上即待用户
  # 确认 (8192), 用户在 chrome://extensions 开一次开关即启用。本步重写登记键模拟装机状态,
  # 再用 CDP 模拟用户开关, 然后实测接管——不静默跳过, 启用失败即报红。
  # Yandex 退出该路 (run 45152500256 实锤): Yandex 对注册表外部安装打 DISABLE_USER_ACTION(1),
  # 开关在 UI 上对真实用户也是灰的, 模拟启用结构性不可达 (chrome.management 在其 WebUI 不可用,
  # CDP evaluate 打扩展页回 Forbidden); Yandex 非 Google 品牌, --load-extension 未打包加载路
  # 与 Edge/Brave/Vivaldi 同口径照测接管行为。
  $NeedsUserEnable = @('Chrome', 'Chromium') -contains $spec.Name

  # 每家独立资料目录：扩展按登记自动挂载（与 verify_mount 同机制）
  $prof = Join-Path $PWD ("evidence\takeover-{0}-profile" -f $spec.Name.ToLower())
  Remove-Item $prof -Recurse -Force -ErrorAction SilentlyContinue
  # --enable-logging: SW 控制台与 native messaging 报错落 chrome_debug.log (run 44991534420 六家全不接管却无浏览器侧第一手日志, 归因断粮)
  # --load-extension: Edge/Brave/Vivaldi 非 Google 品牌仍走未打包加载 (location=8), 接管行为照测;
  #   Chrome/Chromium/Yandex 该 flag 被品牌版无视, 扩展走登记键外部安装 (见上)。
  $extDir = (Resolve-Path (Join-Path $PSScriptRoot '..\src\extension')).Path
  Write-Host ("  扩展加载方式: " + $(if ($NeedsUserEnable) { "注册表外部安装 (模拟真实装机, 用户一次启用后接管)" } else { "未打包加载" }) + " — 扩展目录: " + $extDir)
  # 逐家开跑前再停主程序、再清一遍登记键并断言（run 45029338370/45033054105 教训：主程序存活时
  # 每 30s 周期回写登记键，任何残留都会把实例并回外部安装路）
  Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.Path -and ($_.Path -like '*TaoDown*') } | Stop-Process -Force -ErrorAction SilentlyContinue
  foreach ($root in $extRegRoots) {
    try {
      if (Test-Path $root) {
        Remove-Item (Join-Path $root $Id) -Recurse -Force -ErrorAction SilentlyContinue
        if (Test-Path (Join-Path $root $Id)) { throw "清后仍存在" }
      }
    } catch { $failed.Add("$($spec.Name)：开跑前外部登记清不干净 $root") }
  }
  if ($NeedsUserEnable) {
    # 重写登记键 = 真实装机状态 (应用每 30s 刷新 + MSI 装机登记, HKCU/HKLM/32 位视图同值)
    if (-not $CrxPath) { $failed.Add("$($spec.Name)：装机登记 CRX 路径未取得，无法模拟装机登记"); continue }
    $extVersion = (Get-Content (Join-Path $extDir 'manifest.json') -Raw | ConvertFrom-Json).version
    foreach ($root in @(
        'HKCU:\Software\Google\Chrome\Extensions', 'HKLM:\Software\Google\Chrome\Extensions',
        'HKLM:\Software\Wow6432Node\Google\Chrome\Extensions',
        'HKCU:\Software\Chromium\Extensions', 'HKLM:\Software\Chromium\Extensions',
        'HKCU:\Software\Yandex\YandexBrowser\Extensions', 'HKLM:\Software\Yandex\YandexBrowser\Extensions',
        'HKCU:\Software\YandexBrowser\Extensions', 'HKLM:\Software\YandexBrowser\Extensions')) {
      try {
        New-Item -ItemType Directory -Force -Path $root | Out-Null
        $k = Get-Item (Join-Path $root $Id) -ErrorAction SilentlyContinue
        if (-not $k) { $k = New-Item (Join-Path $root $Id) -Force }
        $k.SetValue('path', $CrxPath)
        $k.SetValue('version', $extVersion)
      } catch { $failed.Add("$($spec.Name)：登记键重写失败 $root : $($_.Exception.Message)") }
    }
    Write-Host ("  已重写登记键 (模拟装机): path={0} version={1}" -f $CrxPath, $extVersion)
  }
  $launchArgs = @("--no-first-run", "--no-default-browser-check", "--enable-logging", "--v=1", "--disable-features=DisableLoadExtensionCommandLineSwitch", "--load-extension=`"$extDir`"", "--user-data-dir=`"$prof`"", "about:blank")
  if ($NeedsUserEnable) { $launchArgs = @("--no-first-run", "--no-default-browser-check", "--enable-logging", "--v=1", "--remote-debugging-port=$CdpPort", "--user-data-dir=`"$prof`"", "about:blank") }
  Start-Process $exe -ArgumentList $launchArgs
  $mounted = Wait-ExtensionMounted $prof 45
  if (-not $mounted) {
    Get-Process $spec.Proc -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    $failed.Add("$($spec.Name)：扩展未进用户资料，接管无从谈起")
    continue
  }
  # 启用态取证 (run 44999835778 教训): 挂载≠启用, disable_reasons 非空即当场报红
  $dr = Get-DisableReasons $prof $Id
  if ($dr -and @($dr).Count -gt 0 -and $NeedsUserEnable) {
    # 真实用户路径: 模拟用户在 chrome://extensions 开一次开关 (CDP 调 chrome.management.setEnabled,
    # 与页面开关同一实现), 再断言启用态落盘
    Write-Host ("  {0} 扩展待用户确认 (disable_reasons={1})，模拟用户启用…" -f $spec.Name, ($dr -join ','))
    try {
      Enable-ExtensionAsUser $CdpPort $Id
      for ($i = 0; $i -lt 15; $i++) {
        Start-Sleep -Seconds 2
        $dr2 = Get-DisableReasons $prof $Id
        if (-not $dr2 -or @($dr2).Count -eq 0) { $dr = $dr2; break }
        $dr = $dr2
      }
      Write-Host ("  模拟用户启用后 disable_reasons={0}" -f ($(if ($dr) { $dr -join ',' } else { '(空)' })))
    } catch {
      Write-Host ("  模拟用户启用失败: {0}" -f $_.Exception.Message)
    }
  }
  if ($dr -and @($dr).Count -gt 0) {
    Get-Process $spec.Proc -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    $failed.Add("$($spec.Name)：扩展被浏览器禁用 (disable_reasons=$($dr -join ','))，接管无从谈起")
    continue
  }
  Write-Host ("  {0} 扩展已挂载且启用，触发下载: {1}" -f $spec.Name, $Url)

  $inboxBefore = @(Test-InboxHit).Count
  # SW 预热 (run 44991534420 归因待证): 挂载后先触发一条预热下载, 唤醒 Service Worker 并走通监听注册, 再清残留跑正式实测
  Start-Process $exe -ArgumentList "--user-data-dir=`"$prof`"", "--disable-features=DisableLoadExtensionCommandLineSwitch", "--load-extension=`"$extDir`"", "http://127.0.0.1:$Port/warmup.bin"
  Start-Sleep -Seconds 8
  Get-ChildItem "$env:USERPROFILE\Downloads" -Filter 'warmup*' -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
  Start-Process $exe -ArgumentList "--user-data-dir=`"$prof`"", "--disable-features=DisableLoadExtensionCommandLineSwitch", "--load-extension=`"$extDir`"", $Url
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
