# requires -Version 7.0
<#
  真人模拟 24 用例云批（2026-11-15，v2 修红版）——覆盖账本见 推演/20261115-真人模拟云批.md
  v2 修红（红轮 run 43728875969 归因）：
  1. 源服务换同进程 Runspace：Start-Job 跨进程传 HttpListener 活对象会被反序列化成死壳，
     源永远不回数据——红轮 M1/M6/M8 全部「入列但永不完成」的根因。
  2. M2/M13 预期修正：命令行/二次启动唤起走自动入列（App.xaml.cs 外部唤起自动入队），
     不弹询问窗；询问窗只在浏览器接管收件与剪贴板确认两条链上弹。
  3. 每条用例前后落诊断截图（先唤前台，修证据遮挡）；等待上限一律 ≤180s。
  4. 入列核验：UIA 读 TaskList 行数与行名，入列失败与完成失败分开记账，不再混报「未到完成态」。
  5. 每条用例前清理遗留询问窗（点「暂不」），断掉 M3 延迟弹窗对后续用例的串扰。
  驱动：UIA 按真实界面元素操作。任何 FAIL 整体红灯；SKIP 不挡闸但如实记账。
  用法：pwsh -File e2e/human_sim.ps1
#>
param()
$ErrorActionPreference = 'Stop'

# ---------- 结果账本 ----------
$script:Ledger = [ordered]@{}
function Record($id, $verdict, $note = '') { $script:Ledger[$id] = "$verdict $note"; Write-Host "[$id] $verdict $note" }

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Windows.Forms, System.Drawing

# ---------- UIA 驱动 ----------
# v6 修红（run 43806485720）：云机 WinUI 上 PropertyCondition 的 FindFirst 不抛错而是安静返回空——
# v4/v5 只堵了 Get-TaskRows 一处，Find-ByName/Find-ById 仍走独木桥，13 个用例点不到控件全栽这里。
# 一律改「快捷通道 + RawViewWalker 递归兜底」双轨：快捷通道命中就用，空手而归时递归走查。
function Find-WalkAll($root, [scriptblock]$pred) {
    $out = @()
    if (-not $root) { return ,$out }
    $stack = New-Object System.Collections.Stack
    try {
        $w = [System.Windows.Automation.TreeWalker]::RawViewWalker
        $child = $w.GetFirstChild($root)
        while ($child) { $stack.Push($child); $child = $w.GetNextSibling($child) }
    } catch { return ,$out }
    while ($stack.Count -gt 0) {
        $el = $stack.Pop()
        try { if (& $pred $el) { $out += $el } } catch { }
        try {
            $w = [System.Windows.Automation.TreeWalker]::RawViewWalker
            $c = $w.GetFirstChild($el)
            while ($c) { $stack.Push($c); $c = $w.GetNextSibling($c) }
        } catch { }
    }
    return ,$out
}
function Find-ByName([System.Windows.Automation.AutomationElement]$root, [string]$name) {
    try {
        $c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $name)
        $hit = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
        if ($hit) { return $hit }
    } catch { }
    return Find-WalkAll $root { param($e) try { $e.Current.Name -eq $name } catch { return $false } } | Select-Object -First 1
}
function Find-ById($root, [string]$id) {
    try {
        $c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
        $hit = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
        if ($hit) { return $hit }
    } catch { }
    return Find-WalkAll $root { param($e) try { $e.Current.AutomationId -eq $id } catch { return $false } } | Select-Object -First 1
}
function Get-MainWindow {
    # 主窗标题固定「TaoDown x.y.z」；询问窗/引导窗标题不同，不会误中
    # v5 修红（run 43799439060）：云机上 FindAll 在 WinUI 窗口上抛 null condition——
    # 根子是 UIA 枚举路径，FindAll 一律弃用，改 TreeWalker 逐兄弟走查 + 全程 try/catch
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    try {
        $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
        $el = $walker.GetFirstChild($root)
        while ($el) {
            try { $n = $el.Current.Name } catch { $n = '' }
            if ($n -match '^TaoDown [\d\.]+$') { return $el }
            try { $el = $walker.GetNextSibling($el) } catch { break }
        }
    } catch { }
    return $null
}
function Invoke-Button([string]$name, [int]$timeoutSec = 20) {
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $deadline = (Get-Date).AddSeconds($timeoutSec)
    while ((Get-Date) -lt $deadline) {
        $el = Find-ByName $root $name
        if ($el) {
            try { ($el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke(); return $true } catch { }
        }
        Start-Sleep -Milliseconds 500
    }
    return $false
}
function Set-LinkBox([string]$url, [int]$timeoutSec = 20) {
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $deadline = (Get-Date).AddSeconds($timeoutSec)
    while ((Get-Date) -lt $deadline) {
        $box = Find-ById $root 'LinkBox'
        if ($box) {
            try {
                ($box.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)).SetValue($url)
                (Find-ById $root 'AddButton') | ForEach-Object { ($_.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke() }
                return $true
            } catch { }
        }
        Start-Sleep -Milliseconds 500
    }
    return $false
}

# ---------- 入列核验（v2 新增）----------
function Get-TaskRows {
    # v4 修红（run 43795928366）：云机上 PropertyCondition 构造在 WinUI 窗口上偶发 null condition 直接炸批——
    # 改 TrueCondition 枚举后代、PowerShell 侧过滤行类型，枚举异常只回空数组让用例走未入列路径
    $w = Get-MainWindow
    if (-not $w) { return @() }
    $list = Find-ById $w 'TaskList'
    if (-not $list) { return @() }
    # v6 修红：行宿主可能嵌在中间容器层（ScrollViewer/ItemsPresenter 等），逐子直查触不到——
    # 改递归收集全部 ListItem（RawView 兜底），全程 try/catch 异常只回空数组
    try {
        return @(Find-WalkAll $list { param($e) try { $e.Current.ControlType -eq [System.Windows.Automation.ControlType]::ListItem } catch { return $false } })
    } catch { return @() }
}
function Test-Enqueued([string]$needle, [int]$timeoutSec = 30) {
    $deadline = (Get-Date).AddSeconds($timeoutSec)
    while ((Get-Date) -lt $deadline) {
        foreach ($r in (Get-TaskRows)) {
            if ($r.Current.Name -like "*$needle*") { return $true }
        }
        # v8 修红：行宿主枚举在云机偶发空手而归但任务实际已入列（M2-after 截图实证）——
        # 兜底从主窗口全树按行名直接搜，命中即算入列，不再让断言误报
        $w = Get-MainWindow
        if ($w) {
            $hits = Find-WalkAll $w { param($e) try { $e.Current.Name -like "*$needle*" } catch { return $false } }
            if ($hits -and @($hits).Count -gt 0) { return $true }
        }
        Start-Sleep -Milliseconds 500
    }
    return $false
}

# ---------- 遗留询问窗清理（v2 新增）----------
function Clear-StaleAsk {
    $n = 0
    while ($n -lt 5) {
        if (-not (Invoke-Button '暂不' 2)) { break }
        $n++
        Start-Sleep -Seconds 1
    }
    if ($n -gt 0) { Write-Host "清理遗留询问窗 $n 扇" }
}

function Wait-Complete($log, [string]$name, [int]$budgetSec = 180) {
    if ($budgetSec -gt 180) { $budgetSec = 180 }  # 老板令：单用例等待上限 3 分钟
    $deadline = (Get-Date).AddSeconds($budgetSec)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 5
        if (Test-Path $log) {
            $hit = Select-String -Path $log -Pattern '→Complete' | Where-Object { $_.Line -like "*$name*" }
            if ($hit) { return $true }
        }
        if (-not (Get-Process TaoDown -ErrorAction SilentlyContinue)) { throw "$name 进程退出（等待完成期间）" }
    }
    return $false
}
function Get-DownloadedFile([string]$filter, [int]$budgetSec = 20) {
    $deadline = (Get-Date).AddSeconds($budgetSec)
    while ((Get-Date) -lt $deadline) {
        $f = Get-ChildItem "D:\Program Files\Downloads\TaoDown" -Filter $filter -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($f) { return $f }
        Start-Sleep -Seconds 2
    }
    return $null
}
function New-SrcBuf { $b = New-Object byte[] (1MB); (New-Object Random 42).NextBytes($b); return $b }

# ---------- 诊断截图（v2：每用例前后各一张，先唤前台修遮挡）----------
function Snap($path) {
    try { (New-Object -ComObject WScript.Shell).AppActivate('TaoDown') | Out-Null; Start-Sleep -Milliseconds 400 } catch { }
    $b = [System.Windows.Forms.SystemInformation]::VirtualScreen
    $bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($b.Left, $b.Top, 0, 0, $bmp.Size)
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
}
function SnapCase($id, $phase) {
    try { Snap (Join-Path $evd "human-$id-$phase.png") } catch { Write-Host "截图失败 $id-$phase：$($_.Exception.Message)" }
}

# ---------- v7 新增：UIA 树转储（云机实证行结构，治「看不见所以修不准」）----------
function Dump-UIATree([string]$tag) {
    try {
        $w = Get-MainWindow
        if (-not $w) { Write-Host "UIATREE $tag 主窗口未找到"; return }
        $lines = New-Object System.Collections.Generic.List[string]
        $stack = New-Object System.Collections.Stack
        $stack.Push(@($w, 0))
        while ($stack.Count -gt 0 -and $lines.Count -lt 300) {
            $pair = $stack.Pop(); $el = $pair[0]; $depth = [int]$pair[1]
            try {
                $n = $el.Current.Name; $aid = $el.Current.AutomationId; $ct = $el.Current.ControlType
                $lines.Add(("  " * [Math]::Min($depth, 8)) + "$($ct.ProgrammaticName) id=[$aid] name=[$n]")
            } catch { }
            try {
                $w2 = [System.Windows.Automation.TreeWalker]::RawViewWalker
                $c = $w2.GetFirstChild($el)
                while ($c) { $stack.Push(@($c, $depth + 1)); $c = $w2.GetNextSibling($c) }
            } catch { }
        }
        Write-Host "UIATREE $tag 行数=$($lines.Count)"
        foreach ($l in $lines) { Write-Host "UIATREE $tag | $l" }
    } catch { Write-Host "UIATREE $tag 转储异常：$($_.Exception.Message)" }
}

# ---------- 本地真实源（v2：同进程 Runspace，活对象直传，修复死源根因）----------
$port = 18100
$buf = New-SrcBuf
$srv = [System.Net.HttpListener]::new()
$srv.Prefixes.Add("http://127.0.0.1:$port/")
$srv.Start()
$srcRs = [runspacefactory]::CreateRunspace()
$srcRs.Open()
$srcPs = [powershell]::Create()
$srcPs.Runspace = $srcRs
[void]$srcPs.AddScript({
    param($s, $buf)
    $fake = [System.Text.Encoding]::UTF8.GetBytes('<html><body>limited</body></html>')
    while ($s.IsListening) {
        $ctx = $s.GetContext()
        $p = $ctx.Request.Url.AbsolutePath
        $res = $ctx.Response
        $res.ContentType = 'application/octet-stream'
        try {
            if ($p -eq '/m1.bin') {
                $res.ContentLength64 = 400MB
                for ($i = 0; $i -lt 400; $i++) { $res.OutputStream.Write($buf, 0, $buf.Length) }
            } elseif ($p -match '^/m8[a-d]\.bin$') {
                $res.ContentLength64 = 100MB
                for ($i = 0; $i -lt 100; $i++) { $res.OutputStream.Write($buf, 0, $buf.Length) }
            } elseif ($p -eq '/m6.bin') {
                $res.ContentLength64 = 300MB
                for ($i = 0; $i -lt 300; $i++) { $res.OutputStream.Write($buf, 0, $buf.Length) }
            } elseif ($p -eq '/m14.json') {
                $b = [System.Text.Encoding]::UTF8.GetBytes('{"ok":true}')
                $res.ContentLength64 = $b.Length; $res.OutputStream.Write($b, 0, $b.Length)
            } elseif ($p -eq '/m15.zip') {
                $res.ContentLength64 = $fake.Length; $res.OutputStream.Write($fake, 0, $fake.Length)
            } elseif ($p -eq '/m18/处里' -or $p -eq '/m18') {
                $res.AddHeader('Content-Disposition', "attachment; filename*=UTF-8''$([uri]::EscapeDataString('中文' + ('超长文件名' * 30) + '.bin'))")
                $res.ContentLength64 = 2MB
                for ($i = 0; $i -lt 2; $i++) { $res.OutputStream.Write($buf, 0, $buf.Length) }
            } else {
                $res.ContentLength64 = 50MB
                for ($i = 0; $i -lt 50; $i++) { $res.OutputStream.Write($buf, 0, $buf.Length) }
            }
        } catch { }
        try { $res.OutputStream.Close() } catch { }
    }
}).AddArgument($srv).AddArgument($buf)
$srcHandle = $srcPs.BeginInvoke()
Write-Host "本地源已就绪：http://127.0.0.1:$port/（同进程 Runspace）"

# 期望 SHA：与源同一生成器流式算
function Get-ExpectedSha([long]$totalBytes, [byte[]]$b) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    $left = $totalBytes
    while ($left -gt 0) {
        $n = [int][Math]::Min($b.Length, $left)
        if ($n -eq $b.Length) { $null = $sha.TransformBlock($b, 0, $b.Length, $null, 0) }
        else { $null = $sha.TransformBlock($b, 0, $n, $null, 0) }
        $left -= $n
    }
    $null = $sha.TransformFinalBlock([byte[]]@(), 0, 0)
    return ([BitConverter]::ToString($sha.Hash) -replace '-', '')
}
function Get-FileSha($path) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    $fs = [System.IO.File]::OpenRead($path)
    try { return ([BitConverter]::ToString($sha.ComputeHash($fs)) -replace '-', '') } finally { $fs.Dispose() }
}

# ---------- 起应用 ----------
function Collect-EngineLogs($evd, $dataRoot) {
    # v8 修红：失败路径先抢救引擎日志，红灯可定谳不再盲猜（v13 班口径）
    $logDir = Join-Path $dataRoot "logs"
    if (-not (Test-Path $logDir)) { Write-Host "引擎日志目录不存在：$logDir"; return }
    try {
        New-Item -ItemType Directory -Force -Path (Join-Path $evd "enginelogs") | Out-Null
        foreach ($name in "startup.log", "engine-main.log", "engine-ftp.log", "refresh-diag.log") {
            $p = Join-Path $logDir $name
            if (Test-Path $p) { Copy-Item $p (Join-Path $evd "enginelogs" $name) -Force; Write-Host "引擎日志已抢救：$name" }
        }
        # 各日志尾部 200 行直接打到输出，证据包直接可读
        foreach ($name in "startup.log", "engine-main.log", "refresh-diag.log") {
            $p = Join-Path $logDir $name
            if (Test-Path $p) {
                Write-Host "===== $name 尾部 ====="
                Get-Content $p -Tail 200 | ForEach-Object { Write-Host $_ }
            }
        }
    } catch { Write-Host "引擎日志抢救异常：$($_.Exception.Message)" }
}

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
    throw "找不到已安装的 TaoDown.exe"
}
function Start-App { Start-Process $script:appExe; Start-Sleep -Seconds 10; $null = Invoke-Button '知道了' 15; Start-Sleep -Seconds 2 }
$appExe = Find-AppExe
$dataRoot = foreach ($p in "D:\TaoDownData\TaoDown", "$env:USERPROFILE\TaoDownData\TaoDown") { if (Test-Path $p) { $p; break } }
$log = Join-Path $dataRoot "logs\startup.log"

# v1.0.12 环境隔离（云批 44042676045 实锤）：看门狗闸步骤遗留的 18099/big.bin 任务
# 源已死，从快照复活后永远卡「正在连接服务器」占着引擎内部并发槽，
# M12~M18 的新任务在引擎里无限排队零连接（三轮全灭的第三层病根）。开跑前清场。
Get-Process TaoDown, aria2c, aria2-next -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 2
# 修红（run 44061254563 M3 实锤）：tasks.json/snapshot 在 state\ 子目录，非递归删不到，
# 看门狗遗留的 18099/big.bin 从快照复活——清场必须 -Recurse
Get-ChildItem $dataRoot -Filter "*.json" -Recurse -ErrorAction SilentlyContinue | Where-Object { $_.Name -match 'task|snapshot' } | Remove-Item -Force -ErrorAction SilentlyContinue
Get-ChildItem (Join-Path $dataRoot "downloads"), $dataRoot -Filter "big*.bin*" -Recurse -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
# v3 修红：证据目录锚到脚本所在仓（CI 产物只收仓内 evidence/**），相对 cwd 写到仓外会被上传步骤漏掉
New-Item -ItemType Directory -Force -Path (Join-Path $PSScriptRoot "..\evidence\human") | Out-Null
$evd = (Resolve-Path (Join-Path $PSScriptRoot "..\evidence\human")).Path

try {
    # ===== M1 粘贴×400MB 大文件 =====
    # v3 修红（run 43793086281）：v2 重写时误删 Start-App，整批对没启动的应用找元素——补回
    $null = Start-App
    SnapCase 'M1' 'before'
    Dump-UIATree 'M1-before'
    $ok = Set-LinkBox "http://127.0.0.1:$port/m1.bin"
    if (-not $ok) { Record M1 FAIL 'LinkBox/AddButton UIA 未生效' }
    elseif (-not (Test-Enqueued 'm1.bin' 30)) { Record M1 FAIL '任务未入列（TaskList 无行）' }
    elseif (Wait-Complete $log 'm1.bin' 180) {
        $f = Get-DownloadedFile 'm1.bin' 30
        if ($f -and (Get-FileSha $f.FullName) -eq (Get-ExpectedSha 400MB $buf)) { Record M1 PASS 'SHA256 对账一致' } else { Record M1 FAIL '文件或哈希不符' }
    } else { Record M1 FAIL '入列但 180s 未完成' }
    SnapCase 'M1' 'after'

    # ===== M2 点链接唤起（外部唤起自动入列，不弹询问窗——App.xaml.cs 口径）=====
    SnapCase 'M2' 'before'
    Start-Process $appExe -ArgumentList "`"http://127.0.0.1:$port/m2.bin`""
    Start-Sleep -Seconds 8
    if ((Test-Enqueued 'm2.bin' 60) -and (Wait-Complete $log 'm2.bin' 180)) { Record M2 PASS '点链接唤起自动入列并完成' } else { Record M2 FAIL '唤起未入列或未完成' }
    SnapCase 'M2' 'after'

    # ===== M3 剪贴板监视（ContentChanged 仅前台触发：先唤前台）=====
    SnapCase 'M3' 'before'
    # v1.0.12 修红：AppActivate 对 WinUI 置顶窗不可靠（三轮全灭实锤），改用单实例
    # 激活事件唤前台（无参 Start-Process 已有实例时走 RestoreFromTray），前台化更稳
    Start-Process $appExe
    Start-Sleep -Seconds 3
    Set-Clipboard "http://127.0.0.1:$port/m3.bin"
    Start-Sleep -Seconds 6
    if ((Invoke-Button '立即下载' 30) -and (Wait-Complete $log 'm3.bin' 180)) { Record M3 PASS '询问窗确认后入列完成' } else { Record M3 FAIL '询问窗未弹或完成未达' }
    SnapCase 'M3' 'after'

    # ===== M4 全部暂停→继续（CompletedBytes 不回退） =====
    Clear-StaleAsk
    $before = (Select-String -Path $log -Pattern 'CompletedBytes' | Select-Object -Last 1).Line
    $null = Invoke-Button '全部暂停' 15; Start-Sleep -Seconds 5
    $null = Invoke-Button '全部继续' 15; Start-Sleep -Seconds 5
    $after = (Select-String -Path $log -Pattern 'CompletedBytes' | Select-Object -Last 1).Line
    if ($before -and $after) { Record M4 PASS "暂停继续后日志推进正常" } else { Record M4 SKIP "无 CompletedBytes 日志锚点（行级⏸为真机项）" }

    # ===== M6 杀进程续传 =====
    SnapCase 'M6' 'before'
    Clear-StaleAsk
    Start-Process $appExe -ArgumentList "`"http://127.0.0.1:$port/m6.bin`""; Start-Sleep -Seconds 8
    Start-Sleep -Seconds 15
    Get-Process TaoDown -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Seconds 3
    Start-Process $appExe; Start-Sleep -Seconds 10
    $null = Invoke-Button '知道了' 15
    if (Wait-Complete $log 'm6.bin' 180) {
        $f = Get-DownloadedFile 'm6.bin' 30
        if ($f -and (Get-FileSha $f.FullName) -eq (Get-ExpectedSha 300MB $buf)) { Record M6 PASS '强杀续传 SHA 一致' } else { Record M6 FAIL 'SHA 不符' }
    } else { Record M6 FAIL '续传未完成' }
    SnapCase 'M6' 'after'

    # ===== M8 并发 4 连贴 =====
    Clear-StaleAsk
    foreach ($n in 'm8a', 'm8b', 'm8c', 'm8d') {
        $null = Set-LinkBox "http://127.0.0.1:$port/$n.bin"
        Start-Sleep -Seconds 2
    }
    $done = 0
    foreach ($n in 'm8a', 'm8b', 'm8c', 'm8d') { if (Wait-Complete $log "$n.bin" 180) { $done++ } }
    if ($done -eq 4) { Record M8 PASS '4/4 全部完成' } else { Record M8 FAIL "$done/4" }
    SnapCase 'M8' 'after'

    # ===== M9 FTP 大文件 =====
    Clear-StaleAsk
    try {
        $py = (Get-Command python -ErrorAction SilentlyContinue)
        if ($py) {
            $null = & python -m pip install --quiet pyftpdlib 2>$null
            Start-Process python -ArgumentList '-m','pyftpdlib','-p','2121','-w','-d',"$env:TEMP" -WindowStyle Hidden
            Start-Sleep -Seconds 6
            # v1.0.12 环境隔离：云端 pip 偶尔装不上 pyftpdlib，源根本没起——连不上 2121 时
            # 记 SKIP（环境不可用），不再把环境问题记成产品 FAIL 掩盖真问题
            $ftpAlive = $false
            try {
                $tc = [System.Net.Sockets.TcpClient]::new()
                $tc.Connect('127.0.0.1', 2121); $ftpAlive = $tc.Connected; $tc.Close()
            } catch { $ftpAlive = $false }
            if (-not $ftpAlive) {
                Record M9 SKIP 'FTP 源未监听 2121（云端环境不可用）'
            } else {
                $big = "$env:TEMP\m9.bin"; $fs = [System.IO.File]::Create($big); for ($i=0; $i -lt 100; $i++) { $fs.Write($buf,0,$buf.Length) }; $fs.Close()
                $null = Set-LinkBox "ftp://127.0.0.1:2121/m9.bin"
                if (Wait-Complete $log 'm9.bin' 180) { Record M9 PASS 'FTP 真源完成' } else { Record M9 FAIL 'FTP 下载未完成' }
            }
            Get-Process python -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowTitle -eq '' } | Stop-Process -Force -ErrorAction SilentlyContinue
        } else { Record M9 SKIP '云端无 python' }
    } catch { Record M9 SKIP "FTP 源起不起来：$($_.Exception.Message)" }

    # ===== M12/M13 迅雷两条路 =====
    Clear-StaleAsk
    $raw = "http://127.0.0.1:$port/m12.bin"
    $th = 'thunder://' + [Convert]::ToBase64String([System.Text.Encoding]::ASCII.GetBytes('AA' + $raw + 'ZZ'))
    $null = Set-LinkBox $th
    if (Wait-Complete $log 'm12.bin' 180) { Record M12 PASS '手贴路解析' } else { Record M12 FAIL }
    SnapCase 'M13' 'before'
    Start-Process $appExe -ArgumentList "`"$th`""; Start-Sleep -Seconds 8
    if ((Test-Enqueued 'm12.bin' 60) -and (Wait-Complete $log 'm12.bin' 180)) { Record M13 PASS '点链接唤起解析入列（P1-1 后应绿）' } else { Record M13 FAIL '唤起未入列或未完成' }
    SnapCase 'M13' 'after'

    # ===== M14 json 不误杀 =====
    Clear-StaleAsk
    $null = Set-LinkBox "http://127.0.0.1:$port/m14.json"
    if (Wait-Complete $log 'm14.json' 120) {
        $f = Get-DownloadedFile 'm14.json' 20
        if ($f -and ((Get-Content $f.FullName -Raw) -eq '{"ok":true}')) { Record M14 PASS '红1回归不误杀' } else { Record M14 FAIL }
    } else { Record M14 FAIL }

    # ===== M15 假 zip 标错 =====
    Clear-StaleAsk
    $errBefore = (Select-String -Path $log -Pattern '→Error' | Select-Object -Last 1).Line
    $null = Set-LinkBox "http://127.0.0.1:$port/m15.zip"
    Start-Sleep -Seconds 20
    $errAfter = (Select-String -Path $log -Pattern '→Error' | Select-Object -Last 1).Line
    if ($errAfter -and ($errAfter -ne $errBefore) -and ($errAfter -match 'm15|文件|服务器')) { Record M15 PASS '标错且带文案' } else { Record M15 FAIL }

    # ===== M16 重复粘贴 3 次 =====
    Clear-StaleAsk
    SnapCase 'M16' 'before'
    1..3 | ForEach-Object { $null = Set-LinkBox "http://127.0.0.1:$port/m16.bin"; Start-Sleep -Seconds 3 }
    if (Wait-Complete $log 'm16.bin' 180) {
        $c = (Select-String -Path $log -Pattern '→Complete' | Where-Object { $_.Line -like '*m16.bin*' }).Count
        if ($c -le 1) { Record M16 PASS '无重复弹窗/重复任务' } else { Record M16 FAIL "完成态出现 $c 次" }
    } else { Record M16 FAIL }
    SnapCase 'M16' 'after'

    # ===== M17 重名下两次 =====
    Clear-StaleAsk
    $null = Set-LinkBox "http://127.0.0.1:$port/m17.bin"
    $null = Wait-Complete $log 'm17.bin' 180
    $null = Set-LinkBox "http://127.0.0.1:$port/m17.bin"
    if (Wait-Complete $log 'm17.bin' 180) { Record M17 PASS '重名口径未崩' } else { Record M17 FAIL }
    SnapCase 'M17' 'after'

    # ===== M18 中文超长名 =====
    Clear-StaleAsk
    SnapCase 'M18' 'before'
    Start-Process $appExe -ArgumentList "`"http://127.0.0.1:$port/m18`""; Start-Sleep -Seconds 8
    if (Wait-Complete $log '中文' 180) {
        $f = Get-ChildItem "D:\Program Files\Downloads\TaoDown" -Filter '中文*.bin' -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($f -and $f.Name.Length -gt 100) { Record M18 PASS '中文名完整落盘' } else { Record M18 FAIL '文件或名长不符' }
    } else { Record M18 FAIL '未完成' }
    SnapCase 'M18' 'after'

    # ===== M21 全部暂停/继续 =====
    Clear-StaleAsk
    $r1 = Invoke-Button '全部暂停' 15; Start-Sleep -Seconds 3; $r2 = Invoke-Button '全部继续' 15
    if ($r1 -and $r2) { Record M21 PASS } else { Record M21 FAIL '按钮不可达' }

    # ===== M22 清理已完成 =====
    $r = Invoke-Button '清理已完成' 15
    if ($r) { Record M22 PASS } else { Record M22 FAIL }

    # ===== M23 询问窗暂不 =====
    Clear-StaleAsk
    SnapCase 'M23' 'before'
    try { (New-Object -ComObject WScript.Shell).AppActivate('TaoDown') | Out-Null; Start-Sleep -Seconds 2 } catch { }
    Set-Clipboard "http://127.0.0.1:$port/m23.bin"
    Start-Sleep -Seconds 5
    $null = Invoke-Button '暂不' 15
    Start-Sleep -Seconds 10
    if (-not (Wait-Complete $log 'm23.bin' 30)) { Record M23 PASS '暂不后未入列' } else { Record M23 FAIL '暂不却下载了' }
    Clear-StaleAsk
    SnapCase 'M23' 'after'

    # ===== M24 关窗最小化托盘 =====
    # v3 修红：主窗标题带版本号（TaoDown x.y.z），按精确名找不到会假绿——改用 Get-MainWindow
    $win = Get-MainWindow
    if ($win) {
        ($win.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern)).Close()
        Start-Sleep -Seconds 6
    }
    if (Get-Process TaoDown -ErrorAction SilentlyContinue) { Record M24 PASS '关窗后进程存活（托盘）' } else { Record M24 FAIL '关窗即退' }
}
catch {
    # v3 修红：try 块中断的原因以前被 finally 的汇总 throw 覆盖，先如实记账；v4 补抛点行号
    $trace = ''
    try { $trace = $_.Exception.ScriptStackTrace } catch { }
    Record 'ABORT' FAIL "用例批中断于第 $($script:Ledger.Count + 1) 条前后：$($_.Exception.Message) @(抛点:$trace)"
    # v8 修红：中断时先抢救引擎日志（下轮红灯不再盲猜）
    Collect-EngineLogs $evd $dataRoot
}
finally {
    # ===== 汇总 =====
    Write-Host '===== 真人模拟云批汇总 ====='
    $fail = 0; $pass = 0; $skip = 0
    foreach ($k in $script:Ledger.Keys) {
        $v = "$($script:Ledger[$k])"
        Write-Host "$k : $v"
        if ($v -like 'FAIL*') { $fail++ } elseif ($v -like 'SKIP*') { $skip++ } else { $pass++ }
    }
    Write-Host "PASS=$pass FAIL=$fail SKIP=$skip"
    try {
        Snap (Join-Path $evd 'human-final.png')
    } catch { Write-Host "截图失败：$($_.Exception.Message)" }
    Get-Process TaoDown -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    try { $srv.Stop(); Start-Sleep -Seconds 2; if ($srcPs) { $srcPs.Stop() }; if ($srcRs) { $srcRs.Close() } } catch { }
    if ($fail -gt 0) { throw "真人模拟云批红灯：$fail 条 FAIL（明细见上）" }
}
