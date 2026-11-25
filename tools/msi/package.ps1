# 生成 TaoDown 的 Windows 安装器 (.msi)





# 特性: 微信风格自绘单页向导 (说明+同意勾选+安装目录+桌面快捷方式 同页完成);





#       桌面快捷方式可选勾选, 开始菜单默认创建; 同版本可覆盖安装; 升级卸载。





# 用法: powershell -File package.ps1 -PublishDir <publish目录> -OutDir <out目录>





# 离线打包: 工具已随包内置 (tools/msi/wix), 无需联网





param(





    [Parameter(Mandatory = $true)][string]$PublishDir,





    [string]$OutDir = "out"





)





$ScriptVersion = "v1.02-1106"





Write-Host "打包脚本版本: $ScriptVersion (若日志无此行, 说明运行的是旧脚本, 请全新目录解压重试)"





$ErrorActionPreference = 'Stop'



# ---- v2.50: 安装期写浏览器插件外链登记（机器级, 卸载随组件自动清除）----

# 键组唯一来源 tools/extension/registry_paths.json（与 BrowserCatalog.Tests 交叉校验）;

# path 直指安装目录里的已签名插件包, Edge/Chrome/Brave/Vivaldi 重启浏览器即自动挂载。

$regPathsJson = Get-Content (Join-Path (Split-Path $PSScriptRoot) 'extension\registry_paths.json') -Raw | ConvertFrom-Json

$extId = $regPathsJson.extension_id

$extVersion = (Get-Content (Join-Path (Split-Path $PSScriptRoot) '..\src\extension\manifest.json') -Raw | ConvertFrom-Json).version

$extRegKeys = @($regPathsJson.paths) + @($regPathsJson.wow_paths_64bit_windows)







$publishRoot = (Resolve-Path $PublishDir).Path



# ---- 全系安装器运行时新标准 (2026-11-21 老板令): 安装包不内置运行时 ----
# 安装期由 EnsureRuntime.ps1 检测/补装三项运行时; 脚本随发布文件进包 (递归枚举自动收)。
$rtSrc = Join-Path $PSScriptRoot 'EnsureRuntime.ps1'
if (-not (Test-Path $rtSrc)) { throw "运行时保障脚本缺失: $rtSrc (请使用完整包)" }
Copy-Item $rtSrc (Join-Path $publishRoot 'EnsureRuntime.ps1') -Force

# ---- 内置兜底 (2026-11-24 老板拍板三步走): 检测已有 → 在线拉 → 内置兜底 ----
# 打包时下载官方运行时安装器随包内置 (打包机网络可靠); 装机时在线拉不动才用兜底。
# 系统里运行时永远只装一份 (机器级共享, 装前必检测), 兜底文件只是备胎不重复安装。
# .NET 桌面运行时: 官方安装器 exe 是 MSI 链, 在本产品 MSI 事务内必撞 _MSIExecute 互斥
#   (run 44886290820 实锤), 故内置 zip 两件套 (基础包 + 桌面包, 缺一不可——
#   desktop zip 只含 WindowsDesktop.App, 基础 NETCore.App 在 dotnet-runtime zip 里),
#   EnsureRuntime 在线全灭时解包进系统统一 .NET 目录, 不走 MSI 无互斥。
#   zip 版本 10.0.15 (2026-11-25 官方 10.0 通道现行版), 在线路始终取最新, 兜底仅应急。
$fallbackDir = Join-Path $publishRoot 'fallback'
New-Item -ItemType Directory -Force -Path $fallbackDir | Out-Null
$fbItems = @(
    @{ Name = 'windowsappruntimeinstall-x64.exe'; Url = 'https://aka.ms/windowsappsdk/1.8/latest/windowsappruntimeinstall-x64.exe' },
    @{ Name = 'vc_redist.x64.exe'; Url = 'https://aka.ms/vs/17/release/vc_redist.x64.exe' },
    @{ Name = 'dotnet-runtime-10.0.15-win-x64.zip'; Url = 'https://builds.dotnet.microsoft.com/dotnet/Runtime/10.0.15/dotnet-runtime-10.0.15-win-x64.zip' },
    @{ Name = 'windowsdesktop-runtime-10.0.15-win-x64.zip'; Url = 'https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/10.0.15/windowsdesktop-runtime-10.0.15-win-x64.zip' }
)
foreach ($fb in $fbItems) {
    $dst = Join-Path $fallbackDir $fb.Name
    if (-not (Test-Path $dst) -or (Get-Item $dst).Length -lt 1MB) {
        Write-Host "下载内置兜底: $($fb.Name)"
        Invoke-WebRequest -Uri $fb.Url -OutFile $dst -UseBasicParsing -TimeoutSec 600
    }
    if (-not (Test-Path $dst) -or (Get-Item $dst).Length -lt 1MB) { throw "内置兜底文件异常偏小: $($fb.Name)" }
}
Write-Host ("内置兜底就绪: " + ((Get-ChildItem $fallbackDir | Measure-Object Length -Sum).Sum / 1MB).ToString('0.0') + " MB")





if (-not (Test-Path (Join-Path $publishRoot 'TaoDown.exe'))) {





    throw "发布目录里没找到 TaoDown.exe: $PublishDir"





}











# ---- 枚举发布文件: 组件直接嵌在各自目录内(经典写法, 兼容 WiX4) ----



$allFiles = Get-ChildItem -Path $publishRoot -Recurse -File



$script:seq = 0



$script:coreRefs = @()



$dirFiles = @{}



foreach ($f in $allFiles) {



    $rel = $f.FullName.Substring($publishRoot.Length).TrimStart('\')



    $parts = $rel -split '\\'



    $dir = if ($parts.Length -gt 1) { ($parts[0..($parts.Length-2)] -join '\') } else { '' }



    if (-not $dirFiles.ContainsKey($dir)) { $dirFiles[$dir] = @() }



    $dirFiles[$dir] += [pscustomobject]@{ Src = $f.FullName }



}



function New-CompXml([string]$src) {



    $script:seq++



    $id = "cmp$script:seq"



    $script:coreRefs += $id



    $esc = [System.Security.SecurityElement]::Escape($src)



    return "<Component Id=`"$id`"><File Source=`"$esc`" KeyPath=`"yes`" /></Component>"



}



function Get-SubDirXml([string]$prefix) {



    $xml = ''



    $childDirs = @($dirFiles.Keys | Where-Object {



        if ($prefix -eq '') { ($_ -ne '') -and (($_ -split '\\').Count -eq 1) }



        else { $_.StartsWith($prefix + '\') -and ((($_.Substring($prefix.Length + 1)) -split '\\').Count -eq 1) }



    })



    foreach ($d in $childDirs) {



        $script:seq++



        $id = "sdir$script:seq"



        $name = if ($prefix -eq '') { $d } else { $d.Substring($prefix.Length + 1) }



        $inner = Get-SubDirXml $d



        $fileXml = ''



        foreach ($entry in $dirFiles[$d]) { $fileXml += New-CompXml $entry.Src }



        $xml += "<Directory Id=`"$id`" Name=`"$name`">$inner$fileXml</Directory>"



    }



    return $xml



}



$rootFilesXml = ''



foreach ($entry in $dirFiles['']) { $rootFilesXml += New-CompXml $entry.Src }



$extRegValues = ''

$firstReg = $true

foreach ($keyPath in $extRegKeys) {

    $escKey = [System.Security.SecurityElement]::Escape(($keyPath + '\' + $extId))

    $escCrx = [System.Security.SecurityElement]::Escape('[INSTALLFOLDER]extension\' + $regPathsJson.crx_file)

    $kp = if ($firstReg) { ' KeyPath="yes"' } else { '' }

    $extRegValues += "<RegistryValue Root=`"HKMU`" Key=`"$escKey`" Name=`"path`" Type=`"string`" Value=`"$escCrx`"$kp />"

    $extRegValues += "<RegistryValue Root=`"HKMU`" Key=`"$escKey`" Name=`"version`" Type=`"string`" Value=`"$extVersion`" />"

    $firstReg = $false

}

$extRegXml = "<Component Id=`"ExtRegC`">$extRegValues</Component>"



$subDirXml = Get-SubDirXml ''



$coreRefsXml = ($script:coreRefs | ForEach-Object { "<ComponentRef Id=`"$_`" />" }) -join ''



Write-Host "安装内容编目完成: $($script:coreRefs.Count) 个文件组件"



# 安装包版本：随发布递增，覆盖安装（MajorUpgrade）依赖它判断新旧

# 默认取主工程 FileVersion，保持安装包与应用版本一致；环境变量仍可覆盖
$csprojFile = Join-Path (Split-Path (Split-Path $PSScriptRoot)) 'src\TaoDown.App\TaoDown.App.csproj'
$csprojRaw = Get-Content $csprojFile -Raw
$csprojVer = if ($csprojRaw -match '<FileVersion>([^<]+)</FileVersion>') { $Matches[1] } else { '1.0.2.0' }
Write-Host "主工程版本: $csprojVer"
$PkgVersion = if ($env:TAODOWN_PKG_VERSION) { $env:TAODOWN_PKG_VERSION } else { $csprojVer }



$wxs = @"





<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs">





    <Package Codepage="936" Name="TaoDown" Manufacturer="Tao" Version="$PkgVersion"





           UpgradeCode="D4A7E1F3-9B2C-4E8A-A1D6-3F5B7C9E2A4D" Scope="perMachine" Compressed="yes">





    <MajorUpgrade DowngradeErrorMessage="已安装了更新版本的 TaoDown，如需重装请先在“应用”中卸载。" AllowSameVersionUpgrades="yes" />

    <!-- 修红班 R3 P1-4：32 位 Windows 无交付覆盖（仅发布 win-x64），安装前置拦截并说明要求 -->
    <Launch Condition="Installed OR VersionNT64"
            Message="TaoDown 仅支持 64 位 Windows（本安装包不含 32 位版本）。请确认系统为 64 位后重新安装。" />





    <MediaTemplate EmbedCab="yes" />



    <CustomAction Id="SetDefaultInstallDir" Property="INSTALLFOLDER" Value="D:\Program Files\TaoDown\" Execute="immediate" Return="check" />

    <!-- 装/卸/升级前结束运行中的程序（进程残留专项，TaoPad/TaoZip 同款双动作写法，真机验证过）:

         属性转发定位 taskkill 避免 WIX1077; Return=ignore: 程序没在运行时 taskkill 报错不影响安装 -->

    <CustomAction Id="SetKillCmd" Property="KILLCMD" Value="[SystemFolder]taskkill.exe" />

    <CustomAction Id="KillRunningTaoDown" Property="KILLCMD"

                  ExeCommand="/f /t /im TaoDown.exe"

                  Execute="immediate" Return="ignore" />

    <CustomAction Id="SetKillCmdHost" Property="KILLCMD2" Value="[SystemFolder]taskkill.exe" />

    <CustomAction Id="KillRunningTaoDownHost" Property="KILLCMD2"

                  ExeCommand="/f /im TaoDown.Host.exe"

                  Execute="immediate" Return="ignore" />

    <!-- 运行时保障 (2026-11-21 全系安装器新标准): 装完文件后检测/补装三项运行时
         (.NET 10 桌面 / 界面框架 WinAppSDK 1.8 / VC++ 2015-2022), 卸载 REMOVE 时跳过;
         失败即回滚, 无网时脚本弹通俗重试指引。必须是 deferred: immediate CA 在
         InstallFinalize 落盘脚本执行前跑, EnsureRuntime.ps1 还没装到盘上, powershell
         -File 找不到文件秒退 (TaoZip run 44877397577 实锤); deferred 排在 InstallFiles
         后即落盘后执行。64 位 PowerShell (System64Folder): 32 位 PS 下注册表被重定向
         漏检 (TaoZip run 44875970874 实锤)。
         下载不弹命令窗 (全系安装器标准, TaoPad v1.1.3 同款): CA 改 WixQuietExec64
         (WixToolset.Util 扩展, 随包内置 tools/msi/wix/ext), 直接起 PowerShell 不再经
         cmd 包一层——旧写法 cmd 窗口在安装期可见。输出落盘改由 PowerShell 内联完成:
         先建日志目录, 脚本输出 *>> 重定向到 ca-out.txt, 脚本自身另有 runtime log 双通道。
         分段进度 (2.4.3, 老板 2.4.2 真机实锤单 CA 下载段进度条长时间不动像卡死):
         单 CA 拆成三个顺序 CA (EnsureRtDotNet → EnsureRtWinApp → EnsureRtVc), 各传
         -RtPhase 只跑一段; MSI 进度条按动作数推进, 每段结束各推进一步, 用户看得到安装
         在动。三段共用同一脚本与日志, 日志行与旧版完全一致, 云测断言不受影响。 -->
    <!-- 云测钩子属性: 断言二强制走下载路。环境变量传不进 msiexec 服务上下文
         (TaoZip run 44885229031 实锤), 改走 MSI 公共属性: msiexec /i xxx.msi TAODOWNFORCE=1;
         以 -ForceDownload 参数直传脚本 (cmd set 环境变量随 cmd 消失, 参数直达) -->
    <Property Id="TAODOWNFORCE" Secure="yes" />
    <!-- 版面铁律 (TaoPad run 44901597525 实锤 1721): SetProperty 必须写在 InstallExecuteSequence
         之前——同挂 After="InstallFiles" 的动作按源码顺序编号, 反过来写会让 EnsureRuntimes
         先于其命令行属性求值, 起跑时属性还是空的直接 1721→1603。
         WixQuietExec64 约定: 与 CA 同名的属性承载完整命令行, 引号内引号用 &quot; 转义,
         PS 字符串用单引号避免冲突 -->
    <SetProperty Id="EnsureRtDotNet" After="InstallFiles" Sequence="execute"
                 Value="&quot;[System64Folder]WindowsPowerShell\v1.0\powershell.exe&quot; -NoProfile -ExecutionPolicy Bypass -Command &quot;New-Item -ItemType Directory -Force -Path '[CommonAppDataFolder]TaoDown' | Out-Null; &amp; '[INSTALLFOLDER]EnsureRuntime.ps1' -ForceDownload '[TAODOWNFORCE]' -RtPhase 'DotNet' *>> '[CommonAppDataFolder]TaoDown\ca-out.txt'&quot;" />
    <CustomAction Id="EnsureRtDotNet" BinaryRef="Wix4UtilCA_X64" DllEntry="WixQuietExec64"
                  Execute="deferred" Return="check" Impersonate="no" />
    <SetProperty Id="EnsureRtWinApp" After="InstallFiles" Sequence="execute"
                 Value="&quot;[System64Folder]WindowsPowerShell\v1.0\powershell.exe&quot; -NoProfile -ExecutionPolicy Bypass -Command &quot;New-Item -ItemType Directory -Force -Path '[CommonAppDataFolder]TaoDown' | Out-Null; &amp; '[INSTALLFOLDER]EnsureRuntime.ps1' -ForceDownload '[TAODOWNFORCE]' -RtPhase 'WinAppSDK' *>> '[CommonAppDataFolder]TaoDown\ca-out.txt'&quot;" />
    <CustomAction Id="EnsureRtWinApp" BinaryRef="Wix4UtilCA_X64" DllEntry="WixQuietExec64"
                  Execute="deferred" Return="check" Impersonate="no" />
    <SetProperty Id="EnsureRtVc" After="InstallFiles" Sequence="execute"
                 Value="&quot;[System64Folder]WindowsPowerShell\v1.0\powershell.exe&quot; -NoProfile -ExecutionPolicy Bypass -Command &quot;New-Item -ItemType Directory -Force -Path '[CommonAppDataFolder]TaoDown' | Out-Null; &amp; '[INSTALLFOLDER]EnsureRuntime.ps1' -ForceDownload '[TAODOWNFORCE]' -RtPhase 'VcRedist' *>> '[CommonAppDataFolder]TaoDown\ca-out.txt'&quot;" />
    <CustomAction Id="EnsureRtVc" BinaryRef="Wix4UtilCA_X64" DllEntry="WixQuietExec64"
                  Execute="deferred" Return="check" Impersonate="no" />
    <InstallExecuteSequence>

      <Custom Action="SetKillCmd" After="InstallValidate" />

      <Custom Action="KillRunningTaoDown" After="SetKillCmd" />

      <Custom Action="SetKillCmdHost" After="KillRunningTaoDown" />

      <Custom Action="KillRunningTaoDownHost" After="SetKillCmdHost" />

      <Custom Action="EnsureRtDotNet" After="InstallFiles" Condition="NOT REMOVE" />

      <Custom Action="EnsureRtWinApp" After="EnsureRtDotNet" Condition="NOT REMOVE" />

      <Custom Action="EnsureRtVc" After="EnsureRtWinApp" Condition="NOT REMOVE" />

      <Custom Action="SetDefaultInstallDir" Before="CostFinalize" />

    </InstallExecuteSequence>





    <Icon Id="TaoDownIco" SourceFile="$publishRoot\TaoDown.ico" />



    <Property Id="ARPNOREPAIR" Value="1" />

    <Property Id="ARPPRODUCTICON" Value="TaoDownIco" />





    <Property Id="DESKTOP_SHORTCUT" Value="1" />



    <Property Id="BROWSER_MOUNT" Value="1" />











    <UI Id="TaoDownInstallUI">





      <TextStyle Id="DlgFont" FaceName="微软雅黑" Size="9" />





      <TextStyle Id="TitleFont" FaceName="微软雅黑" Size="13" Bold="yes" />





      <Property Id="DefaultUIFont" Value="DlgFont" />











      <Dialog Id="MainDlg" Width="440" Height="290" Title="[ProductName] 安装程序">





        <Control Id="Title" Type="Text" X="20" Y="12" Width="400" Height="22" Text="{\TitleFont}安装 TaoDown" />





        <Control Id="LicenseText" Type="Text" X="20" Y="42" Width="400" Height="92" Text="TaoDown 1.0，免费使用、无广告、无捆绑。本软件按“现状”提供：请仅将它用于合法合规的文件下载用途，下载速度遵循各来源的公开策略，作者不对使用本软件产生的任何损失承担责任。" />





        <Control Id="AcceptCheck" Type="CheckBox" X="20" Y="140" Width="400" Height="16" Property="LICENSE_ACCEPTED" CheckBoxValue="1" Text="我已阅读并同意以上说明" />





        <Control Id="SepLine" Type="Line" X="20" Y="164" Width="400" Height="1" />





        <Control Id="DirLabel" Type="Text" X="20" Y="174" Width="400" Height="14" Text="安装目录（可直接输入，不存在的文件夹会自动创建）：" />





        <Control Id="DirEdit" Type="PathEdit" X="20" Y="192" Width="310" Height="17" Property="INSTALLFOLDER" />





        <Control Id="DeskCheck" Type="CheckBox" X="20" Y="218" Width="320" Height="16" Property="DESKTOP_SHORTCUT" CheckBoxValue="1" Text="创建桌面快捷方式">





          <Publish Event="Remove" Value="DeskShortcutF" Condition="NOT DESKTOP_SHORTCUT" />





          <Publish Event="AddLocal" Value="DeskShortcutF" Condition="DESKTOP_SHORTCUT" />





        </Control>





        <Control Id="BrowserCheck" Type="CheckBox" X="20" Y="240" Width="310" Height="16" Property="BROWSER_MOUNT" CheckBoxValue="1" Text="浏览器自动挂载下载插件（重启浏览器生效）">





          <Publish Event="Remove" Value="ExtRegF" Condition="NOT BROWSER_MOUNT" />





          <Publish Event="AddLocal" Value="ExtRegF" Condition="BROWSER_MOUNT" />





        </Control>





        <Control Id="InstallNow" Type="PushButton" X="340" Y="240" Width="80" Height="24" Default="yes" Text="立即安装">





          <Publish Event="SpawnDialog" Value="AcceptWarnDlg" Condition="NOT LICENSE_ACCEPTED" Order="1" />





          <Publish Event="NewDialog" Value="VerifyReadyDlg" Condition="LICENSE_ACCEPTED" Order="2" />





        </Control>





        <Control Id="CancelBtn" Type="PushButton" X="340" Y="268" Width="80" Height="18" Cancel="yes" Text="取消">





          <Publish Event="EndDialog" Value="Exit" />





        </Control>





      </Dialog>











      <Dialog Id="VerifyReadyDlg" Width="440" Height="130" Title="[ProductName] 安装程序">





        <Control Id="ReadyText" Type="Text" X="20" Y="30" Width="400" Height="24" Text="准备安装 TaoDown，点击“安装”开始。" />





        <Control Id="BackBtn" Type="PushButton" X="270" Y="95" Width="70" Height="22" Text="返回">





          <Publish Event="NewDialog" Value="MainDlg" />





        </Control>





        <Control Id="InstallBtn" Type="PushButton" X="350" Y="95" Width="70" Height="22" Default="yes" Text="安装">





          <Publish Event="EndDialog" Value="Return" />





        </Control>





      </Dialog>











      <Dialog Id="ProgressDlg" Width="440" Height="110" Title="[ProductName] 安装程序" Modeless="yes">





        <Control Id="ProgressText" Type="Text" X="20" Y="24" Width="400" Height="14" Text="正在安装，请稍候……" />





        <Control Id="ProgressBar" Type="ProgressBar" X="20" Y="48" Width="400" Height="14" Text="正在安装" />





        <Control Id="CancelBtn" Type="PushButton" X="350" Y="78" Width="70" Height="20" Cancel="yes" Text="取消">





          <Publish Event="SpawnDialog" Value="CancelDlg" />





        </Control>





      </Dialog>











      <Dialog Id="AcceptWarnDlg" Width="280" Height="90" Title="[ProductName] 安装程序">





        <Control Id="WarnText" Type="Text" X="20" Y="18" Width="240" Height="18" Text="请先勾选“我已阅读并同意以上说明”。" />





        <Control Id="OkBtn" Type="PushButton" X="110" Y="55" Width="60" Height="20" Default="yes" Text="确定">





          <Publish Event="EndDialog" Value="Return" />





        </Control>





      </Dialog>





      <Dialog Id="CancelDlg" Width="280" Height="90" Title="[ProductName] 安装程序">





        <Control Id="CancelText" Type="Text" X="20" Y="18" Width="240" Height="18" Text="确定要取消安装吗？" />





        <Control Id="YesBtn" Type="PushButton" X="100" Y="55" Width="60" Height="20" Default="yes" Text="是">





          <Publish Event="EndDialog" Value="Exit" />





        </Control>





        <Control Id="NoBtn" Type="PushButton" X="170" Y="55" Width="60" Height="20" Text="否">





          <Publish Event="EndDialog" Value="Return" />





        </Control>





      </Dialog>











      <Dialog Id="ExitDialog" Width="440" Height="130" Title="[ProductName] 安装程序">





        <Control Id="DoneText" Type="Text" X="20" Y="30" Width="400" Height="24" Text="TaoDown 安装完成，双击桌面图标即可使用。" />





        <Control Id="FinishBtn" Type="PushButton" X="350" Y="95" Width="70" Height="22" Default="yes" Text="完成">





          <Publish Event="EndDialog" Value="Return" />





        </Control>





      </Dialog>





      <Dialog Id="UserExitDlg" Width="440" Height="130" Title="[ProductName] 安装程序">





        <Control Id="UserExitText" Type="Text" X="20" Y="30" Width="400" Height="24" Text="安装已取消，本机未做任何更改。" />





        <Control Id="FinishBtn" Type="PushButton" X="350" Y="95" Width="70" Height="22" Default="yes" Text="完成">





          <Publish Event="EndDialog" Value="Return" />





        </Control>





      </Dialog>





      <Dialog Id="FatalErrorDlg" Width="440" Height="130" Title="[ProductName] 安装程序">





        <Control Id="ErrorText" Type="Text" X="20" Y="30" Width="400" Height="24" Text="安装未完成，请重试或联系开发者。" />





        <Control Id="FinishBtn" Type="PushButton" X="350" Y="95" Width="70" Height="22" Default="yes" Text="完成">





          <Publish Event="EndDialog" Value="Return" />





        </Control>





      </Dialog>











      <InstallUISequence>





        <Custom Action="SetDefaultInstallDir" Before="CostFinalize" />



        <Show Dialog="MainDlg" Sequence="1250" />





        <Show Dialog="ProgressDlg" Before="ExecuteAction" />





        <Show Dialog="ExitDialog" OnExit="success" />





        <Show Dialog="UserExitDlg" OnExit="cancel" />





        <Show Dialog="FatalErrorDlg" OnExit="error" />





      </InstallUISequence>





    </UI>











    <StandardDirectory Id="DesktopFolder">



      <Component Id="DeskShortcutC">



        <Shortcut Id="DesktopLnk" Icon="TaoDownIco" Name="TaoDown" Target="[INSTALLFOLDER]TaoDown.exe" WorkingDirectory="INSTALLFOLDER" />



        <RegistryValue Root="HKCU" Key="Software\Tao\TaoDown" Name="DesktopShortcut" Type="integer" Value="1" KeyPath="yes" />



      </Component>



    </StandardDirectory>



    <StandardDirectory Id="ProgramFiles6432Folder">

      <Directory Id="INSTALLFOLDER" Name="TaoDown">$subDirXml$rootFilesXml$extRegXml</Directory>

    </StandardDirectory>



    <StandardDirectory Id="ProgramMenuFolder">



      <Directory Id="ApplicationProgramsFolder">



        <Component Id="MenuShortcutC">



          <Shortcut Id="MenuLnk" Icon="TaoDownIco" Name="TaoDown" Target="[INSTALLFOLDER]TaoDown.exe" WorkingDirectory="INSTALLFOLDER" />



          <RemoveFolder Id="RemoveMenuDir" On="uninstall" />



          <RegistryValue Root="HKCU" Key="Software\Tao\TaoDown" Name="MenuShortcut" Type="integer" Value="1" KeyPath="yes" />



        </Component>



      </Directory>



    </StandardDirectory>



    <Feature Id="Core" Title="TaoDown 主程序" Description="下载本体、双引擎与浏览器插件（必装）" Level="1" AllowAbsent="no">$coreRefsXml



      <ComponentRef Id="MenuShortcutC" />



    </Feature>



    <Feature Id="ExtRegF" Title="浏览器自动挂载下载插件" Level="1">



      <ComponentRef Id="ExtRegC" />



    </Feature>



    <Feature Id="DeskShortcutF" Title="创建桌面快捷方式" Level="1">



      <ComponentRef Id="DeskShortcutC" />



    </Feature>











    





  </Package>





</Wix>





"@

$wxsPath = Join-Path $env:TEMP "TaoDown.Package.wxs"





[System.IO.File]::WriteAllText($wxsPath, $wxs, (New-Object System.Text.UTF8Encoding($true)))











# 完全离线: 使用包内便携 WiX 工具, 自绘 UI 无需界面扩展, 无需联网





$wixExe = Join-Path $PSScriptRoot "wix\wix.exe"





if (-not (Test-Path $wixExe)) { throw "包内打包工具缺失 ($wixExe)，请使用完整包重解压" }











$msi = Join-Path $OutDir "TaoDown-$PkgVersion-x64.msi"





if (Test-Path $msi) { Remove-Item $msi -Force }





Write-Host ("打包工具: " + (Get-Item $wixExe).Length + " bytes @ " + (Get-Item $wixExe).LastWriteTime)





& $wixExe build $wxsPath -pdbtype none -o $msi -ext (Join-Path $PSScriptRoot "wix\ext\WixToolset.Util.wixext.dll") 2>&1 | Tee-Object -FilePath (Join-Path $env:TEMP "TaoDown.msi-build.log")





if ($LASTEXITCODE -ne 0) {





    Write-Host "打包配置已保存: $wxsPath (排错用, 请把此文件与上方完整报错一并发出)"





    throw "WiX 构建 msi 失败 (请确认本机已安装 .NET 8)"





}





Write-Host "安装器已生成: $msi"





