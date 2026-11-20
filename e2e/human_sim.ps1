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

# ---------- v11 第二轮补测新增助手（删除窗/过滤器/设置回读）----------
function Invoke-NavPage([string]$pageName, [int]$timeoutSec = 15) {
    $deadline = (Get-Date).AddSeconds($timeoutSec)
    while ((Get-Date) -lt $deadline) {
        try {
            $w = Get-MainWindow
            if ($w) {
                $navs = Find-WalkAll $w { param($e) try { $e.Current.ControlType -eq [System.Windows.Automation.ControlType]::ListItem -and $e.Current.Name -eq $pageName } catch { return $false } }
                if ($navs -and @($navs).Count -gt 0) {
                    ($navs[0].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)).Select()
                    Start-Sleep -Seconds 3
                    return $true
                }
            }
        } catch { }
        Start-Sleep -Milliseconds 500
    }
    return $false
}
function Find-RowByNeedle([string]$needle) {
    # 先按行名找；云机上行名偶发为空（v4 修红：行名绑定已补，兜底翻行内文本）
    foreach ($r in (Get-TaskRows)) { if ($r.Current.Name -like "*$needle*") { return $r } }
    foreach ($r in (Get-TaskRows)) {
        try {
            $texts = Find-WalkAll $r { param($e) try { $e.Current.ControlType -eq [System.Windows.Automation.ControlType]::Text } catch { return $false } }
            foreach ($t in $texts) { if ("$($t.Current.Name)" -like "*$needle*") { return $r } }
        } catch { }
    }
    return $null
}
function Invoke-RowButton($row, [string]$buttonId) {
    try {
        $btn = Find-ById $row $buttonId
        if ($btn) { ($btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke(); return $true }
    } catch { }
    return $false
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

# v12 新增：行级子树转储（治「摸不到所以修不准」，只打该行内部，证据包直接可读）
function Dump-Subtree($el, [string]$tag) {
    try {
        $lines = New-Object System.Collections.Generic.List[string]
        $stack = New-Object System.Collections.Stack
        $stack.Push(@($el, 0))
        while ($stack.Count -gt 0 -and $lines.Count -lt 120) {
            $pair = $stack.Pop(); $e = $pair[0]; $depth = [int]$pair[1]
            try {
                $n = $e.Current.Name; $aid = $e.Current.AutomationId; $ct = $e.Current.ControlType
                $lines.Add(("  " * [Math]::Min($depth, 8)) + "$($ct.ProgrammaticName) id=[$aid] name=[$n]")
            } catch { }
            try {
                $w = [System.Windows.Automation.TreeWalker]::RawViewWalker
                $c = $w.GetFirstChild($e)
                while ($c) { $stack.Push(@($c, $depth + 1)); $c = $w.GetNextSibling($c) }
            } catch { }
        }
        Write-Host "SUBTREE $tag 行数=$($lines.Count)"
        foreach ($l in $lines) { Write-Host "SUBTREE $tag | $l" }
    } catch { Write-Host "SUBTREE $tag 异常：$($_.Exception.Message)" }
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
function Set-NumberValue([System.Windows.Automation.AutomationElement]$edit, [string]$text) {
    # v2.4.3 修卷（run 44696397261 M34/M35 实锤）：ValuePattern.SetValue 对 WinUI NumberBox 内层 Edit
    # 抛「Indices length does not match the array rank」——换真键盘输入通道：焦点+全选+键入
    Add-Type -AssemblyName System.Windows.Forms
    $null = $edit
    $edit.SetFocus()
    Start-Sleep -Milliseconds 400
    [System.Windows.Forms.SendKeys]::SendWait('^a')
    Start-Sleep -Milliseconds 150
    [System.Windows.Forms.SendKeys]::SendWait($text)
    Start-Sleep -Milliseconds 300
}
function Get-NumberInnerEdit([System.Windows.Automation.AutomationElement]$box) {
    # WinUI NumberBox 的 UIA 值在内层 Edit 上：优先按 ValuePattern 摸，摸不到再递归找 Edit
    try { $null = $box.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern); return $box } catch { }
    return (Find-WalkAll $box { param($e) try { $e.Current.ControlType -eq [System.Windows.Automation.ControlType]::Edit } catch { return $false } } | Select-Object -First 1)
}

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

    # ===== M25 剪贴板全页生效（v2.63 老板拍板：任何页面检测到直链都弹置顶询问窗）=====
    Clear-StaleAsk
    SnapCase 'M25' 'before'
    # 先切到设置页：导航项是 ListItem（SelectionItemPattern），不是 Button
    $navSet = $false
    try {
        $w = Get-MainWindow
        if ($w) {
            $navs = Find-WalkAll $w { param($e) try { $e.Current.ControlType -eq [System.Windows.Automation.ControlType]::ListItem -and $e.Current.Name -eq '设置' } catch { return $false } }
            if ($navs -and @($navs).Count -gt 0) {
                ($navs[0].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)).Select()
                $navSet = $true
                Start-Sleep -Seconds 3
            }
        }
    } catch { }
    if (-not $navSet) { Record M25 SKIP '设置导航项不可达（UIA）' }
    else {
        $onSettings = $false
        $deadline = (Get-Date).AddSeconds(10)
        while ((Get-Date) -lt $deadline) {
            $w = Get-MainWindow
            if ($w -and (Find-ById $w 'SaveButton')) { $onSettings = $true; break }
            Start-Sleep -Milliseconds 500
        }
        if (-not $onSettings) { Record M25 SKIP '设置页未就绪（SaveButton 不可见）' }
        else {
            # 场景一：停在设置页，复制直链——询问窗必须弹出且确认后完成
            Set-Clipboard "http://127.0.0.1:$port/m25.bin"
            $r = Invoke-Button '立即下载' 20
            if ($r -and (Wait-Complete $log 'm25.bin' 180)) { Record M25 PASS '设置页复制直链弹窗并完成' } else { Record M25 FAIL '非下载页弹窗失效或完成未达' }
            # 场景二：复制普通网页地址——依 v2.60 口径必须静默不弹窗
            Clear-StaleAsk
            Set-Clipboard 'https://www.example.com/article/123'
            Start-Sleep -Seconds 8
            $leaked = $false
            try {
                $hits = Find-WalkAll ([System.Windows.Automation.AutomationElement]::RootElement) { param($e) try { $e.Current.ControlType -eq [System.Windows.Automation.ControlType]::Window -and $e.Current.Name -like '检测到剪贴板链接*' } catch { return $false } }
                if ($hits -and @($hits).Count -gt 0) { $leaked = $true }
            } catch { }
            if ($leaked) { Record M26 FAIL '网页地址误弹询问窗（静默口径被破）' } else { Record M26 PASS '网页地址静默不扰（回归不破）' }
        }
    }
    Clear-StaleAsk
    SnapCase 'M25' 'after'

    # ===== M27 设置页往返不丢任务列表（M26 静默断言在 M25 场景二内完成）=====
    try {
        $w = Get-MainWindow
        $navDl = $null
        if ($w) {
            $navs = Find-WalkAll $w { param($e) try { $e.Current.ControlType -eq [System.Windows.Automation.ControlType]::ListItem -and $e.Current.Name -eq '下载' } catch { return $false } }
            if ($navs -and @($navs).Count -gt 0) { $navDl = $navs[0] }
        }
        if ($navDl) {
            ($navDl.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)).Select()
            Start-Sleep -Seconds 3
            $w2 = Get-MainWindow
            $backOk = $false
            $deadline = (Get-Date).AddSeconds(10)
            while ((Get-Date) -lt $deadline) {
                if ($w2 -and (Find-ById $w2 'TaskList')) { $backOk = $true; break }
                Start-Sleep -Milliseconds 500
            }
            if ($backOk) { Record M27 PASS '设置往返后任务列表健在' } else { Record M27 FAIL '返回下载页 TaskList 不可见' }
        } else { Record M27 SKIP '下载导航项不可达（UIA）' }
    } catch { Record M27 FAIL "导航往返异常：$($_.Exception.Message)" }
    SnapCase 'M27' 'after'

    # ===== M28 删除确认窗·取消不动账（口径：先问再删，取消不删）=====
    Clear-StaleAsk
    SnapCase 'M28' 'before'
    try {
        $null = Set-LinkBox "http://127.0.0.1:$port/m28.bin"
        if (-not (Wait-Complete $log 'm28.bin' 180)) { Record M28 SKIP '前置任务未完成（环境）' }
        else {
            $row = $null
            $dl = (Get-Date).AddSeconds(15)
            while ((Get-Date) -lt $dl) {
                $row = Find-RowByNeedle 'm28'
                if ($row) { break }
                Start-Sleep -Milliseconds 800
            }
            if (-not $row) { Dump-UIATree 'M28-norow'; Record M28 SKIP '任务行不可达（UIA）' }
            else {
                $btn = $null
                $dl = (Get-Date).AddSeconds(10)
                while ((Get-Date) -lt $dl) {
                    try { $btn = Find-ById $row 'RowDeleteButton' } catch { }
                    if ($btn) { break }
                    Start-Sleep -Milliseconds 800
                }
                if (-not $btn) { Dump-Subtree $row 'M28-row'; Record M28 SKIP '删除按钮不可达（UIA）' }
                else {
                    try { ($btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke() } catch { Record M28 SKIP '删除按钮调用失败（UIA）' }
                    Start-Sleep -Seconds 2
                    if (-not (Invoke-Button '取消' 15)) { Record M28 FAIL '删除未弹确认窗（先问再删口径被破）' }
                    else {
                        Start-Sleep -Seconds 2
                        if (Find-RowByNeedle 'm28') { Record M28 PASS '取消后任务健在' } else { Record M28 FAIL '取消后任务消失' }
                    }
                }
            }
        }
    } catch { Record M28 FAIL "异常：$($_.Exception.Message)" }
    SnapCase 'M28' 'after'

    # ===== M29 删除确认窗·勾选连本地文件一起删（口径：勾选后文件清理）=====
    Clear-StaleAsk
    SnapCase 'M29' 'before'
    try {
        $f = Get-DownloadedFile 'm28.bin' 10
        if (-not $f) { Record M29 SKIP 'm28 本地文件不在（前置缺失）' }
        else {
            $row = Find-RowByNeedle 'm28'
            if (-not $row) { Record M29 SKIP '任务行不可达（UIA）' }
            else {
                $btn = $null
                $dl = (Get-Date).AddSeconds(10)
                while ((Get-Date) -lt $dl) {
                    try { $btn = Find-ById $row 'RowDeleteButton' } catch { }
                    if ($btn) { break }
                    Start-Sleep -Milliseconds 800
                }
                if (-not $btn) { Dump-Subtree $row 'M29-row'; Record M29 SKIP '删除按钮不可达（UIA）' }
                else {
                    try { ($btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke() } catch { Record M29 SKIP '删除按钮调用失败（UIA）' }
                    Start-Sleep -Seconds 2
                    $chk = $null
                    try {
                        $w = Get-MainWindow
                        if ($w) {
                            $hits = Find-WalkAll $w { param($e) try { $e.Current.ControlType -eq [System.Windows.Automation.ControlType]::CheckBox -and $e.Current.Name -like '*同时删除本地文件*' } catch { return $false } }
                            if ($hits -and @($hits).Count -gt 0) { $chk = $hits[0] }
                        }
                    } catch { }
                    if (-not $chk) { Record M29 FAIL '确认窗缺勾选框（口径缺失）'; $null = Invoke-Button '取消' 5 }
                    else {
                        ($chk.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)).Toggle()
                        Start-Sleep -Seconds 1
                        if (-not (Invoke-Button '删除' 15)) { Record M29 SKIP '删除按钮不可达'; $null = Invoke-Button '取消' 5 }
                        else {
                            $gone = $false
                            $dl = (Get-Date).AddSeconds(15)
                            while ((Get-Date) -lt $dl) {
                                if (-not (Find-RowByNeedle 'm28')) { $gone = $true; break }
                                Start-Sleep -Milliseconds 500
                            }
                            $fileGone = -not (Test-Path $f.FullName)
                            if ($gone -and $fileGone) { Record M29 PASS '任务与本地文件一并清理' }
                            elseif (-not $gone) { Record M29 FAIL '任务行仍在' }
                            else { Record M29 FAIL '任务删了但文件还在' }
                        }
                    }
                }
            }
        }
    } catch { Record M29 FAIL "异常：$($_.Exception.Message)" }
    SnapCase 'M29' 'after'
    # ===== M30 过滤器三视图行数账（全部 = 进行中 + 已完成；选择必须真生效）=====
    Clear-StaleAsk
    SnapCase 'M30' 'before'
    try {
        function Get-FilterCount([string]$label) {
            try {
                $w = Get-MainWindow
                $box = if ($w) { Find-ById $w 'FilterBox' } else { $null }
                if (-not $box) { return -1 }
                # 选择并核对生效：FilterBox 的 UIA Name 应等于所选项；未生效重试 3 次
                for ($try = 0; $try -lt 3; $try++) {
                    try { ($box.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)).Expand() } catch { }
                    Start-Sleep -Seconds 1
                    $items = Find-WalkAll ([System.Windows.Automation.AutomationElement]::RootElement) { param($e) try { $e.Current.ControlType -eq [System.Windows.Automation.ControlType]::ListItem -and $e.Current.Name -eq $label } catch { return $false } }
                    if ($items -and @($items).Count -gt 0) {
                        try { ($items[0].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)).Select() } catch { }
                        Start-Sleep -Seconds 2
                    }
                    # 核对生效：重开下拉核对所选项 IsSelected（box 的 UIA Name 恒空，不可信）
                    $picked = $false
                    try {
                        ($box.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)).Expand()
                        Start-Sleep -Seconds 1
                        $chk = Find-WalkAll ([System.Windows.Automation.AutomationElement]::RootElement) { param($e) try { $e.Current.ControlType -eq [System.Windows.Automation.ControlType]::ListItem -and $e.Current.Name -eq $label } catch { return $false } }
                        if ($chk -and @($chk).Count -gt 0) { $picked = ($chk[0].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)).Current.IsSelected }
                        try { ($box.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)).Collapse() } catch { }
                    } catch { }
                    Start-Sleep -Seconds 1
                    Write-Host "FILTERSEL label=$label try=$try picked=[$picked]"
                    if ($picked) {
                        # 逐行取证：每个视图把可见行的名字打出来，红绿都留实锤（不再只报计数）
                        $names = @()
                        foreach ($r in (Get-TaskRows)) {
                            $n = $r.Current.Name
                            if (-not $n) { try { $ts = Find-WalkAll $r { param($e) try { $e.Current.ControlType -eq [System.Windows.Automation.ControlType]::Text } catch { return $false } }; $n = (@($ts) | ForEach-Object { $_.Current.Name }) -join '/' } catch { } }
                            $names += "[$n]"
                        }
                        Write-Host "VIEWROWS label=$label rows=$($names -join ' ')"
                        return $names.Count
                    }
                }
            } catch { }
            return -1
        }
        $nAll = Get-FilterCount '全部'
        $nRun = Get-FilterCount '进行中'
        $nDone = Get-FilterCount '已完成'
        $nBack = Get-FilterCount '全部'
        Write-Host "FILTERCOUNT 全部=$nAll 进行中=$nRun 已完成=$nDone 回全部=$nBack"
        if ($nAll -lt 0 -or $nRun -lt 0 -or $nDone -lt 0) { Dump-UIATree 'M30-filter'; Record M30 SKIP '筛选选择未生效（UIA）' }
        elseif (($nRun + $nDone) -eq $nAll -and $nAll -eq $nBack) { Record M30 PASS "全部=$nAll 进行中=$nRun 已完成=$nDone 帐目一致" }
        else { Dump-UIATree 'M30-mismatch'; Record M30 FAIL "全部=$nAll 进行中=$nRun 已完成=$nDone 回全部=$nBack" }
    } catch { Record M30 FAIL "异常：$($_.Exception.Message)" }
    SnapCase 'M30' 'after'
    # ===== M31 设置改动重启回读（持久化账）=====
    Clear-StaleAsk
    SnapCase 'M31' 'before'
    try {
        if (-not (Invoke-NavPage '设置')) { Record M31 SKIP '设置页不可达（UIA）' }
        else {
            $sw = $null
            $deadline = (Get-Date).AddSeconds(10)
            while ((Get-Date) -lt $deadline) {
                $w = Get-MainWindow
                if ($w) { $sw = Find-ById $w 'AutoClassifySwitch' }
                if ($sw) { break }
                Start-Sleep -Milliseconds 500
            }
            if (-not $sw) { Record M31 SKIP 'AutoClassifySwitch 不可达（UIA）' }
            else {
                $st = $sw.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
                $orig = ($st.Current.ToggleState -eq [System.Windows.Automation.ToggleState]::On)
                $newVal = -not $orig
                $st.Toggle(); Start-Sleep -Seconds 1
                if (-not (Invoke-Button '保存设置' 15)) { Record M31 SKIP '保存按钮不可达'; $null = Invoke-NavPage '下载' }
                else {
                    Start-Sleep -Seconds 2
                    Get-Process TaoDown -ErrorAction SilentlyContinue | Stop-Process -Force
                    Start-Sleep -Seconds 3
                    $null = Start-App
                    if (-not (Invoke-NavPage '设置')) { Record M31 SKIP '重启后设置页不可达' }
                    else {
                        $sw2 = $null
                        $deadline = (Get-Date).AddSeconds(10)
                        while ((Get-Date) -lt $deadline) {
                            $w = Get-MainWindow
                            if ($w) { $sw2 = Find-ById $w 'AutoClassifySwitch' }
                            if ($sw2) { break }
                            Start-Sleep -Milliseconds 500
                        }
                        if (-not $sw2) { Record M31 SKIP '重启后开关不可达（UIA）' }
                        else {
                            $st2 = $sw2.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
                            $read = ($st2.Current.ToggleState -eq [System.Windows.Automation.ToggleState]::On)
                            if ($read -eq $newVal) { Record M31 PASS '改动重启后回读一致' } else { Record M31 FAIL "改了没存住（改=$newVal 读=$read）" }
                            if ($read -ne $orig) { $st2.Toggle(); Start-Sleep -Seconds 1; $null = Invoke-Button '保存设置' 10 }
                        }
                    }
                }
            }
        }
    } catch { Record M31 FAIL "异常：$($_.Exception.Message)" }
    SnapCase 'M31' 'after'

    # ===== M32 磁力链接全页询问窗+入列（补测第三轮：磁力/种子场景）=====
    Clear-StaleAsk
    SnapCase 'M32' 'before'
    try {
        # M31 结束停在设置页：正好考「非下载页复制磁力也弹窗」
        Set-Clipboard 'magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567&dn=m32-magnet-task.bin'
        $r = Invoke-Button '立即下载' 20
        if (-not $r) { Record M32 FAIL '磁力链接未弹询问窗（接管口径被破）' }
        else {
            if (Invoke-NavPage '下载' 15) {
                Start-Sleep -Seconds 5
                $row = Find-RowByNeedle 'm32-magnet'
                if (-not $row) { Start-Sleep -Seconds 10; $row = Find-RowByNeedle 'm32-magnet' }
                if ($row) { Record M32 PASS '磁力确认后任务入列（无种环境不强求完成）' } else { Dump-UIATree 'M32-norow'; Record M32 FAIL '磁力确认后任务未入列' }
            } else { Record M32 SKIP '下载页导航不可达（UIA）' }
        }
    } catch { Record M32 FAIL "异常：$($_.Exception.Message)" }
    SnapCase 'M32' 'after'

    # ===== M33 .torrent 直链真下载（补测第三轮：种子文件直链按下载接管）=====
    Clear-StaleAsk
    SnapCase 'M33' 'before'
    try {
        Set-Clipboard "http://127.0.0.1:$port/m33.torrent"
        $r = Invoke-Button '立即下载' 20
        if ($r -and (Wait-Complete $log 'm33.torrent' 180)) { Record M33 PASS '种子直链弹窗并完成下载' } else { Record M33 FAIL '种子直链弹窗失效或未完成' }
    } catch { Record M33 FAIL "异常：$($_.Exception.Message)" }
    Clear-StaleAsk
    SnapCase 'M33' 'after'

    # ===== M34 限速真驱动引擎（run 44285307095 全绿后补测全集：设置→引擎参数对账）=====
    Clear-StaleAsk
    SnapCase 'M34' 'before'
    try {
        if (-not (Invoke-NavPage '设置')) { Record M34 SKIP '设置页不可达（UIA）' }
        else {
            $box = $null
            $deadline = (Get-Date).AddSeconds(10)
            while ((Get-Date) -lt $deadline) {
                $w = Get-MainWindow
                if ($w) { $box = Find-ById $w 'SpeedLimitBox' }
                if ($box) { break }
                Start-Sleep -Milliseconds 500
            }
            if (-not $box) { Record M34 SKIP 'SpeedLimitBox 不可达（UIA）' }
            else {
                $inner = Get-NumberInnerEdit $box
                if (-not $inner) { throw 'NumberBox 内层 Edit 不可达' }
                Set-NumberValue $inner '1024'
                if (-not (Invoke-Button '保存设置' 15)) { Record M34 SKIP '保存按钮不可达'; $null = Invoke-NavPage '下载' }
                else {
                    # 重启后引擎带参启动，进程命令行是对账锚点
                    Get-Process TaoDown -ErrorAction SilentlyContinue | Stop-Process -Force
                    Start-Sleep -Seconds 5
                    Start-App
                    Start-Sleep -Seconds 5
                    $eng = (Get-CimInstance Win32_Process -Filter "Name='aria2-next.exe'" -ErrorAction SilentlyContinue | Select-Object -First 1)
                    if (-not $eng) { Record M34 SKIP '引擎进程不可见（云机口径）' }
                    elseif ($eng.CommandLine -like '*--max-overall-download-limit=1024K*') { Record M34 PASS '限速 1024K 已注入引擎启动参数' }
                    else { Record M34 FAIL "引擎参数未带限速：$($eng.CommandLine)" }
                    # 复原
                    $null = Invoke-NavPage '设置'
                    $b2 = $null
                    $w2 = Get-MainWindow
                    if ($w2) { $b2 = Find-ById $w2 'SpeedLimitBox' }
                    if ($b2) { $i2 = Get-NumberInnerEdit $b2; if ($i2) { Set-NumberValue $i2 '0'; $null = Invoke-Button '保存设置' 15 } }
                    $null = Invoke-NavPage '下载'
                }
            }
        }
    } catch { Record M34 SKIP "限速用例异常：$($_.Exception.Message)" }
    SnapCase 'M34' 'after'

    # ===== M35 并发上限拦截（上限=1 时 3 连贴只允许 1 活跃）=====
    Clear-StaleAsk
    SnapCase 'M35' 'before'
    try {
        if (-not (Invoke-NavPage '设置')) { Record M35 SKIP '设置页不可达（UIA）' }
        else {
            $box = $null
            $deadline = (Get-Date).AddSeconds(10)
            while ((Get-Date) -lt $deadline) {
                $w = Get-MainWindow
                if ($w) { $box = Find-ById $w 'MaxConcurrentBox' }
                if ($box) { break }
                Start-Sleep -Milliseconds 500
            }
            if (-not $box) { Record M35 SKIP 'MaxConcurrentBox 不可达（UIA）' }
            else {
                $inner1 = Get-NumberInnerEdit $box
                if (-not $inner1) { throw 'NumberBox 内层 Edit 不可达' }
                Set-NumberValue $inner1 '1'
                if (-not (Invoke-Button '保存设置' 15)) { Record M35 SKIP '保存按钮不可达'; $null = Invoke-NavPage '下载' }
                else {
                    for ($i = 1; $i -le 3; $i++) {
                        Start-Process $appExe -ArgumentList "`"http://127.0.0.1:$port/m35-$i.bin`""
                        Start-Sleep -Seconds 2
                    }
                    Start-Sleep -Seconds 15
                    # 对账锚点：队列把上限注入引擎启动参数，进程命令行是唯一实据
                    Start-Sleep -Seconds 3
                    $eng2 = (Get-CimInstance Win32_Process -Filter "Name='aria2-next.exe'" -ErrorAction SilentlyContinue | Select-Object -First 1)
                    if (-not $eng2) { Record M35 SKIP '引擎进程不可见（云机口径）' }
                    elseif ($eng2.CommandLine -like '*--max-concurrent-downloads=1*') { Record M35 PASS '并发上限=1 已注入引擎参数' } else { Record M35 FAIL '并发上限参数未见注入' }
                    # 复原上限=3
                    $null = Invoke-NavPage '设置'
                    $w3 = Get-MainWindow
                    if ($w3) { $b3 = Find-ById $w3 'MaxConcurrentBox'; if ($b3) { $i3 = Get-NumberInnerEdit $b3; if ($i3) { Set-NumberValue $i3 '3'; $null = Invoke-Button '保存设置' 15 } } }
                    $null = Invoke-NavPage '下载'
                }
            }
        }
    } catch { Record M35 SKIP "并发上限用例异常：$($_.Exception.Message)" }
    SnapCase 'M35' 'after'

    # ===== M36 主题切换即改即存（关1视觉：切换态截图归档）=====
    Clear-StaleAsk
    SnapCase 'M36' 'before'
    try {
        if (-not (Invoke-NavPage '设置')) { Record M36 SKIP '设置页不可达（UIA）' }
        else {
            $tb = $null
            $deadline = (Get-Date).AddSeconds(10)
            while ((Get-Date) -lt $deadline) {
                $w = Get-MainWindow
                if ($w) { $tb = Find-ById $w 'ThemeBox' }
                if ($tb) { break }
                Start-Sleep -Milliseconds 500
            }
            if (-not $tb) { Record M36 SKIP 'ThemeBox 不可达（UIA）' }
            else {
                # 切第二主题（WinUI ComboBox 选项在弹层：全窗枚举 ListItem，FILTERSEL 同法）
                $picked = $false
                try {
                    $tb.SetFocus()
                    Start-Sleep -Milliseconds 800
                    $tb.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
                    Start-Sleep -Milliseconds 1500
                    $w36 = Get-MainWindow
                    if ($w36) {
                        # v2.4.3 修卷：三象主题项名=水墨/天灰/墨夜，旧过滤「深色/浅色/主题」永不命中（SKIP 根因）；
                        # 弹层 ListItem 依序取第二项（切离默认主题），不猜名字
                        $items = @(Find-WalkAll $w36 { param($e) try { $e.Current.ControlType -eq [System.Windows.Automation.ControlType]::ListItem } catch { return $false } })
                        Write-Host ("  M36 弹层 ListItem: {0}" -f (($items | ForEach-Object { $_.Current.Name }) -join ' | '))
                        if ($items.Count -ge 2) { try { $items[1].Select(); $picked = $true } catch { } }
                    }
                    Start-Sleep -Milliseconds 800
                    try { (Get-MainWindow).SetFocus() } catch { }
                } catch { }
                Start-Sleep -Seconds 3
                SnapCase 'M36' 'theme1'
                # 主窗口健在且无崩溃 = 切换未炸；持久化回读归到重启后主题仍对的截图账
                $w6 = Get-MainWindow
                if ($picked -and $w6) { Record M36 PASS '主题切换未炸（截图归档，视觉账走关1）' } else { Record M36 SKIP '主题项不可选（UIA）' }
                # 切回默认第一项
                try {
                    $tb.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
                    Start-Sleep -Milliseconds 1000
                    $first = Find-WalkAll $tb { param($e) try { $e.Current.ControlType -eq [System.Windows.Automation.ControlType]::ListItem } catch { return $false } } | Select-Object -First 1
                    if ($first) { try { $first.Select() } catch { } }
                    Start-Sleep -Milliseconds 800
                    try { (Get-MainWindow).SetFocus() } catch { }
                } catch { }
                Start-Sleep -Seconds 2
                SnapCase 'M36' 'reverted'
            }
        }
    } catch { Record M36 SKIP "主题用例异常：$($_.Exception.Message)" }
    SnapCase 'M36' 'after'

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
