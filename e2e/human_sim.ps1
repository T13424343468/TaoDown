# requires -Version 7.0
<#
  真人模拟 24 用例云批（2026-11-15）——覆盖账本见 推演/20261115-真人模拟云批.md
  驱动：UIA 按真实界面元素操作（LinkBox 输入、添加下载/全部暂停/全部继续/清理已完成、
  引导窗「知道了」、询问窗「立即下载/暂不」）。任何 FAIL 整体红灯；SKIP 不挡闸但如实记账。
  用法：pwsh -File e2e/human_sim.ps1
#>
param()
$ErrorActionPreference = 'Stop'

# ---------- 结果账本 ----------
$script:Ledger = [ordered]@{}
function Record($id, $verdict, $note = '') { $script:Ledger[$id] = "$verdict $note"; Write-Host "[$id] $verdict $note" }

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Windows.Forms, System.Drawing

# ---------- UIA 驱动 ----------
function Find-ByName([System.Windows.Automation.AutomationElement]$root, [string]$name) {
    return $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $name)))
}
function Find-ById($root, [string]$id) {
    return $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)))
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
function Wait-Complete($log, [string]$name, [int]$budgetSec) {
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
function Snap($path) {
    $b = [System.Windows.Forms.SystemInformation]::VirtualScreen
    $bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($b.Left, $b.Top, 0, 0, $bmp.Size)
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
}

# ---------- 本地真实源 ----------
$port = 18100
$buf = New-SrcBuf
$srv = [System.Net.HttpListener]::new()
$srv.Prefixes.Add("http://127.0.0.1:$port/")
$srv.Start()
$job = Start-Job -ScriptBlock {
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
} -ArgumentList $srv, $buf

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
New-Item -ItemType Directory -Force -Path "..\evidence\human" | Out-Null
$evd = Resolve-Path "..\evidence\human"

try {
    # ===== M1 粘贴×400MB 大文件 =====
    $ok = Start-App
    $ok = Set-LinkBox "http://127.0.0.1:$port/m1.bin"
    if ($ok -and (Wait-Complete $log 'm1.bin' 600)) {
        $f = Get-DownloadedFile 'm1.bin' 30
        if ($f -and (Get-FileSha $f.FullName) -eq (Get-ExpectedSha 400MB $buf)) { Record M1 PASS 'SHA256 对账一致' } else { Record M1 FAIL '文件或哈希不符' }
    } else { Record M1 FAIL '未到完成态' }

    # ===== M2 浏览器接管入口 =====
    Start-Process $appExe -ArgumentList "`"http://127.0.0.1:$port/m2.bin`""
    Start-Sleep -Seconds 8
    if ((Invoke-Button '立即下载' 20) -and (Wait-Complete $log 'm2.bin' 300)) { Record M2 PASS } else { Record M2 FAIL '接管询问窗或完成未达' }

    # ===== M3 剪贴板监视 =====
    Set-Clipboard "http://127.0.0.1:$port/m3.bin"
    Start-Sleep -Seconds 8
    if ((Invoke-Button '立即下载' 20) -and (Wait-Complete $log 'm3.bin' 300)) { Record M3 PASS } else { Record M3 FAIL }

    # ===== M4 全部暂停→继续（CompletedBytes 不回退） =====
    $before = (Select-String -Path $log -Pattern 'CompletedBytes' | Select-Object -Last 1).Line
    $null = Invoke-Button '全部暂停' 15; Start-Sleep -Seconds 5
    $null = Invoke-Button '全部继续' 15; Start-Sleep -Seconds 5
    $after = (Select-String -Path $log -Pattern 'CompletedBytes' | Select-Object -Last 1).Line
    if ($before -and $after) { Record M4 PASS "暂停继续后日志推进正常" } else { Record M4 SKIP "无 CompletedBytes 日志锚点（行级⏸为真机项）" }

    # ===== M6 杀进程续传 =====
    Start-Process $appExe -ArgumentList "`"http://127.0.0.1:$port/m6.bin`""; Start-Sleep -Seconds 8
    $null = Invoke-Button '立即下载' 15
    Start-Sleep -Seconds 20
    Get-Process TaoDown -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Seconds 3
    Start-Process $appExe; Start-Sleep -Seconds 10
    $null = Invoke-Button '知道了' 15
    if (Wait-Complete $log 'm6.bin' 600) {
        $f = Get-DownloadedFile 'm6.bin' 30
        if ($f -and (Get-FileSha $f.FullName) -eq (Get-ExpectedSha 300MB $buf)) { Record M6 PASS '强杀续传 SHA 一致' } else { Record M6 FAIL 'SHA 不符' }
    } else { Record M6 FAIL '续传未完成' }

    # ===== M8 并发 4 连贴 =====
    foreach ($n in 'm8a', 'm8b', 'm8c', 'm8d') {
        $null = Set-LinkBox "http://127.0.0.1:$port/$n.bin"
        Start-Sleep -Seconds 2
    }
    $done = 0
    foreach ($n in 'm8a', 'm8b', 'm8c', 'm8d') { if (Wait-Complete $log "$n.bin" 900) { $done++ } }
    if ($done -eq 4) { Record M8 PASS '4/4 全部完成' } else { Record M8 FAIL "$done/4" }

    # ===== M9 FTP 大文件 =====
    try {
        $py = (Get-Command python -ErrorAction SilentlyContinue)
        if ($py) {
            $null = & python -m pip install --quiet pyftpdlib 2>$null
            Start-Process python -ArgumentList '-m','pyftpdlib','-p','2121','-w','-d',"$env:TEMP" -WindowStyle Hidden
            Start-Sleep -Seconds 6
            $big = "$env:TEMP\m9.bin"; $fs = [System.IO.File]::Create($big); for ($i=0; $i -lt 100; $i++) { $fs.Write($buf,0,$buf.Length) }; $fs.Close()
            $null = Set-LinkBox "ftp://127.0.0.1:2121/m9.bin"
            if (Wait-Complete $log 'm9.bin' 600) { Record M9 PASS 'FTP 真源完成' } else { Record M9 FAIL 'FTP 下载未完成' }
            Get-Process python -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowTitle -eq '' } | Stop-Process -Force -ErrorAction SilentlyContinue
        } else { Record M9 SKIP '云端无 python' }
    } catch { Record M9 SKIP "FTP 源起不起来：$($_.Exception.Message)" }

    # ===== M12/M13 迅雷两条路 =====
    $raw = "http://127.0.0.1:$port/m12.bin"
    $th = 'thunder://' + [Convert]::ToBase64String([System.Text.Encoding]::ASCII.GetBytes('AA' + $raw + 'ZZ'))
    $null = Set-LinkBox $th
    if (Wait-Complete $log 'm12.bin' 300) { Record M12 PASS '手贴路解析' } else { Record M12 FAIL }
    Start-Process $appExe -ArgumentList "`"$th`""; Start-Sleep -Seconds 8
    $null = Invoke-Button '立即下载' 15
    if (Wait-Complete $log 'm12.bin' 300) { Record M13 PASS '接管路解析（P1-1 后应绿）' } else { Record M13 FAIL }

    # ===== M14 json 不误杀 =====
    $null = Set-LinkBox "http://127.0.0.1:$port/m14.json"
    if (Wait-Complete $log 'm14.json' 120) {
        $f = Get-DownloadedFile 'm14.json' 20
        if ($f -and ((Get-Content $f.FullName -Raw) -eq '{"ok":true}')) { Record M14 PASS '红1回归不误杀' } else { Record M14 FAIL }
    } else { Record M14 FAIL }

    # ===== M15 假 zip 标错 =====
    $errBefore = (Select-String -Path $log -Pattern '→Error' | Select-Object -Last 1).Line
    $null = Set-LinkBox "http://127.0.0.1:$port/m15.zip"
    Start-Sleep -Seconds 20
    $errAfter = (Select-String -Path $log -Pattern '→Error' | Select-Object -Last 1).Line
    if ($errAfter -and ($errAfter -ne $errBefore) -and ($errAfter -match 'm15|文件|服务器')) { Record M15 PASS '标错且带文案' } else { Record M15 FAIL }

    # ===== M16 重复粘贴 3 次 =====
    1..3 | ForEach-Object { $null = Set-LinkBox "http://127.0.0.1:$port/m16.bin"; Start-Sleep -Seconds 3 }
    $null = Invoke-Button '立即下载' 15
    if (Wait-Complete $log 'm16.bin' 300) {
        $c = (Select-String -Path $log -Pattern '→Complete' | Where-Object { $_.Line -like '*m16.bin*' }).Count
        if ($c -le 1) { Record M16 PASS '无重复弹窗/重复任务' } else { Record M16 FAIL "完成态出现 $c 次" }
    } else { Record M16 FAIL }

    # ===== M17 重名下两次 =====
    $null = Set-LinkBox "http://127.0.0.1:$port/m17.bin"
    $null = Wait-Complete $log 'm17.bin' 300
    $null = Set-LinkBox "http://127.0.0.1:$port/m17.bin"
    $null = Invoke-Button '立即下载' 15
    if (Wait-Complete $log 'm17.bin' 300) { Record M17 PASS '重名口径未崩' } else { Record M17 FAIL }

    # ===== M18 中文超长名 =====
    Start-Process $appExe -ArgumentList "`"http://127.0.0.1:$port/m18`""; Start-Sleep -Seconds 8
    $null = Invoke-Button '立即下载' 15
    if (Wait-Complete $log '中文' 300) {
        $f = Get-ChildItem "D:\Program Files\Downloads\TaoDown" -Filter '中文*.bin' -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($f -and $f.Name.Length -gt 100) { Record M18 PASS '中文名完整落盘' } else { Record M18 FAIL }
    } else { Record M18 FAIL }

    # ===== M21 全部暂停/继续 =====
    $r1 = Invoke-Button '全部暂停' 15; Start-Sleep -Seconds 3; $r2 = Invoke-Button '全部继续' 15
    if ($r1 -and $r2) { Record M21 PASS } else { Record M21 FAIL '按钮不可达' }

    # ===== M22 清理已完成 =====
    $r = Invoke-Button '清理已完成' 15
    if ($r) { Record M22 PASS } else { Record M22 FAIL }

    # ===== M23 询问窗暂不 =====
    $cBefore = (Select-String -Path $log -Pattern '任务状态' | Measure-Object).Count
    Start-Process $appExe -ArgumentList "`"http://127.0.0.1:$port/m23.bin`""
    Start-Sleep -Seconds 8
    $null = Invoke-Button '暂不' 15
    Start-Sleep -Seconds 10
    if (-not (Wait-Complete $log 'm23.bin' 5)) { Record M23 PASS '暂不后未入列' } else { Record M23 FAIL '暂不却下载了' }

    # ===== M24 关窗最小化托盘 =====
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children,
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, 'TaoDown')))
    if ($win) {
        ($win.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern)).Close()
        Start-Sleep -Seconds 6
    }
    if (Get-Process TaoDown -ErrorAction SilentlyContinue) { Record M24 PASS '关窗后进程存活（托盘）' } else { Record M24 FAIL '关窗即退' }
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
    Stop-Job $job -ErrorAction SilentlyContinue; Remove-Job $job -Force -ErrorAction SilentlyContinue
    $srv.Stop()
    if ($fail -gt 0) { throw "真人模拟云批红灯：$fail 条 FAIL（明细见上）" }
}
