# MSI 内容自检（2026-11-01 建，v2.44 随下载器聚焦瘦身：双引擎核对）。
# 本脚本在 WiX 构建成功后直接打开 MSI 数据库核对 File 表，
# 双引擎（aria2c.exe / aria2-next.exe）缺一即退出码 1，构建立刻失败，
# 不允许"构建绿了但包是空心的"流向装机。参数为相对或绝对 MSI 路径均可。
#
# v2.40 教训：File 表 FileName 列对超 8.3 短名规则的长名采用「短名|长名」格式，
# 直接整串比对会误报缺失——按竖线取末段归一化，大小写不敏感。
# v2.41 教训：路径拆拼不能用 -split "\\"（正则末尾反斜杠非法，且经过脚本转义易丢），
# 统一用 [System.IO.Path]::DirectorySeparatorChar 字符做 Split/Join，零正则零转义。
# v2.41 增强：File→Component→Directory 解析三引擎安装落点并打印，子目录错即失败。
param(
    [Parameter(Mandatory = $true)]
    [string]$MsiPath
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $MsiPath)) {
    Write-Host "MSI 自检失败: 文件不存在 $MsiPath"
    exit 1
}

$sep = [System.IO.Path]::DirectorySeparatorChar

$installer = New-Object -ComObject WindowsInstaller.Installer
$database = $installer.GetType().InvokeMember(
    "OpenDatabase", "InvokeMethod", $null, $installer, @($MsiPath, 0))

# 1) File 表: 取全部文件名(归一化短名|长名)
$view = $database.GetType().InvokeMember(
    "OpenView", "InvokeMethod", $null, $database,
    @("SELECT ``FileName`` FROM ``File``"))
$view.GetType().InvokeMember("Execute", "InvokeMethod", $null, $view, $null)

$raw = New-Object System.Collections.Generic.List[string]
while ($true) {
    $record = $view.GetType().InvokeMember("Fetch", "InvokeMethod", $null, $view, $null)
    if ($null -eq $record) { break }
    $null = $raw.Add([string]$record.GetType().InvokeMember("StringData", "GetProperty", $null, $record, @(1)))
}
$view.Close() | Out-Null

$names = New-Object System.Collections.Generic.HashSet[string] ([System.StringComparer]::OrdinalIgnoreCase)
foreach ($n in $raw) {
    $parts = $n -split "\|"
    if ($parts.Count -gt 1) { $null = $names.Add($parts[$parts.Count - 1]) }
    $null = $names.Add($n)
}

# v2.48 教训：扩展目录曾疑似缺装无从判断——插件三件（manifest/background/popup 脚本）
# 必须与双引擎同列为硬门槛，缺一即构建失败。
$missing = @("aria2c.exe", "aria2-next.exe", "manifest.json", "background.js", "taodown.crx") |
    Where-Object { -not $names.Contains($_) }
if ($missing.Count -gt 0) {
    Write-Host ("MSI 自检失败: MSI 缺少 {0} (文件总数 {1})" -f ($missing -join ", "), $names.Count)
    exit 1
}

# 2) Directory 表: 目录父子与段名(同样归一化短名|长名)
$dirView = $database.GetType().InvokeMember(
    "OpenView", "InvokeMethod", $null, $database,
    @("SELECT ``Directory``, ``Directory_Parent``, ``DefaultDir`` FROM ``Directory``"))
$dirView.GetType().InvokeMember("Execute", "InvokeMethod", $null, $dirView, $null)
$dirParent = @{}
$dirName = @{}
while ($true) {
    $r = $dirView.GetType().InvokeMember("Fetch", "InvokeMethod", $null, $dirView, $null)
    if ($null -eq $r) { break }
    $id = [string]$r.GetType().InvokeMember("StringData", "GetProperty", $null, $r, @(1))
    $parent = [string]$r.GetType().InvokeMember("StringData", "GetProperty", $null, $r, @(2))
    $seg = [string]$r.GetType().InvokeMember("StringData", "GetProperty", $null, $r, @(3))
    $segParts = $seg -split "\|"
    if ($segParts.Count -gt 1) { $seg = $segParts[$segParts.Count - 1] }
    $dirParent[$id] = $parent
    $dirName[$id] = $seg
}
$dirView.Close() | Out-Null

# 3) File→Component→Directory: 三引擎安装落点
$mapView = $database.GetType().InvokeMember(
    "OpenView", "InvokeMethod", $null, $database,
    @("SELECT ``File``.``FileName``, ``Component``.``Directory_`` FROM ``File``, ``Component`` WHERE ``File``.``Component_`` = ``Component``.``Component``"))
$mapView.GetType().InvokeMember("Execute", "InvokeMethod", $null, $mapView, $null)
$map = @{}
while ($true) {
    $r = $mapView.GetType().InvokeMember("Fetch", "InvokeMethod", $null, $mapView, $null)
    if ($null -eq $r) { break }
    $fn = [string]$r.GetType().InvokeMember("StringData", "GetProperty", $null, $r, @(1))
    $d = [string]$r.GetType().InvokeMember("StringData", "GetProperty", $null, $r, @(2))
    $fnParts = $fn -split "\|"
    if ($fnParts.Count -gt 1) { $fn = $fnParts[$fnParts.Count - 1] }
    if (-not $map.ContainsKey($fn)) { $map[$fn] = $d }
}
$mapView.Close() | Out-Null

function Resolve-Rel([string]$dirId) {
    $segs = New-Object System.Collections.Generic.List[string]
    $cur = $dirId
    while ($cur -and $dirName.ContainsKey($cur)) {
        $segs.Insert(0, $dirName[$cur])
        $cur = $dirParent[$cur]
    }
    return [System.String]::Join($sep, $segs)
}

$expect = @{
    "aria2c.exe"     = "engine"
    "aria2-next.exe" = "engine"
    "manifest.json"  = "extension"
    "taodown.crx"    = "extension"
}
$bad = @()
foreach ($k in $expect.Keys) {
    $rel = Resolve-Rel $map[$k]
    Write-Host ("MSI 落点: {0} -> {1}" -f $k, $rel)
    $leafParts = $rel.Split($sep)
    $leaf = $leafParts[$leafParts.Count - 1]
    if ($leaf -ne $expect[$k]) { $bad += ("{0} 落点错误: {1}" -f $k, $rel) }
}
if ($bad.Count -gt 0) {
    Write-Host ("MSI 自检失败: {0}" -f ($bad -join "; "))
    exit 1
}

# 4) Registry 表（v2.50）: 插件外链登记必须已写入安装包（Edge 自动挂载的机器级兜底），
# path 值必须直指安装目录里的 taodown.crx；键缺失或 path 指向目录即构建失败。
$regView = $database.GetType().InvokeMember(
    "OpenView", "InvokeMethod", $null, $database,
    @("SELECT ``Registry``, ``Root``, ``Key``, ``Name``, ``Value`` FROM ``Registry``"))
$regView.GetType().InvokeMember("Execute", "InvokeMethod", $null, $regView, $null)
$regEntries = @()
while ($true) {
    $r = $regView.GetType().InvokeMember("Fetch", "InvokeMethod", $null, $regView, $null)
    if ($null -eq $r) { break }
    $regEntries += [pscustomobject]@{
        Root  = [string]$r.GetType().InvokeMember("StringData", "GetProperty", $null, $r, @(2))
        Key   = [string]$r.GetType().InvokeMember("StringData", "GetProperty", $null, $r, @(3))
        Name  = [string]$r.GetType().InvokeMember("StringData", "GetProperty", $null, $r, @(4))
        Value = [string]$r.GetType().InvokeMember("StringData", "GetProperty", $null, $r, @(5))
    }
}
$regView.Close() | Out-Null

# 键组唯一来源与 package.ps1 同一套相对路径口径：tools\extension\registry_paths.json
# （v2.50 真机首跑教训：此处曾多拆一层目录到 Build\extension\，文件必查无）
$regPathsFile = Join-Path (Split-Path $PSScriptRoot) "extension\registry_paths.json"
if (-not (Test-Path $regPathsFile)) {
    Write-Host "MSI 自检失败: 找不到登记键唯一来源 $regPathsFile"
    exit 1
}
$regCfg = Get-Content $regPathsFile -Raw | ConvertFrom-Json
$regBad = @()
foreach ($keyPath in @($regCfg.paths) + @($regCfg.wow_paths_64bit_windows)) {
    $full = ($keyPath + "\" + $regCfg.extension_id)
    $hit = $regEntries | Where-Object { $_.Key -eq $full }
    if (-not $hit) {
        $regBad += ("缺登记键 {0}" -f $full)
        continue
    }
    $pathVal = $hit | Where-Object { $_.Name -eq "path" }
    if (-not $pathVal) {
        $regBad += ("{0} 缺 path 值" -f $full)
    }
    elseif ($pathVal.Value -notmatch "taodown\.crx") {
        $regBad += ("{0} path 未指向插件包: {1}" -f $full, $pathVal.Value)
    }
}
if ($regBad.Count -gt 0) {
    Write-Host ("MSI 自检失败: {0}" -f ($regBad -join "; "))
    exit 1
}

Write-Host ("MSI 自检通过: {0} 个文件, 双引擎与扩展文件齐全且落点正确, 插件登记 {1} 键齐全" -f $raw.Count, $regEntries.Count)
exit 0
