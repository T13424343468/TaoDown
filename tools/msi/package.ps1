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
$PkgVersion = if ($env:TAODOWN_PKG_VERSION) { $env:TAODOWN_PKG_VERSION } else { "1.0.2.0" }

$wxs = @"


<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs">


    <Package Codepage="936" Name="TaoDown" Manufacturer="Tao" Version="$PkgVersion"


           UpgradeCode="D4A7E1F3-9B2C-4E8A-A1D6-3F5B7C9E2A4D" Scope="perMachine" Compressed="yes">


    <MajorUpgrade DowngradeErrorMessage="已安装了更新版本的 TaoDown，如需重装请先在“应用”中卸载。" AllowSameVersionUpgrades="yes" />


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
    <InstallExecuteSequence>
      <Custom Action="SetKillCmd" After="InstallValidate" />
      <Custom Action="KillRunningTaoDown" After="SetKillCmd" />
      <Custom Action="SetKillCmdHost" After="KillRunningTaoDown" />
      <Custom Action="KillRunningTaoDownHost" After="SetKillCmdHost" />
      <Custom Action="SetDefaultInstallDir" Sequence="1150" />
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


        <Custom Action="SetDefaultInstallDir" Sequence="1150" />

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

    <Feature Id="Core" Title="TaoDown 主程序" Description="下载本体、双引擎、网盘组件与浏览器插件（必装）" Level="1" AllowAbsent="no">$coreRefsXml

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


& $wixExe build $wxsPath -pdbtype none -o $msi 2>&1 | Tee-Object -FilePath (Join-Path $env:TEMP "TaoDown.msi-build.log")


if ($LASTEXITCODE -ne 0) {


    Write-Host "打包配置已保存: $wxsPath (排错用, 请把此文件与上方完整报错一并发出)"


    throw "WiX 构建 msi 失败 (请确认本机已安装 .NET 8)"


}


Write-Host "安装器已生成: $msi"


