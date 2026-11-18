@echo off
rem TaoDown 一键重出最新安装包（双击自提权）
chcp 936 >nul
net session >nul 2>&1
if errorlevel 1 (
    echo 需要管理员权限，正在请求提权...
    powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)
cd /d "%~dp0"
if not exist "src\TaoDown.App\TaoDown.App.csproj" (
    echo [X] 未找到源码根目录：请把本脚本放在源码根目录（src 的上一级）再双击。
    pause & exit /b 1
)
echo ============================================
echo [1/3] 发布程序本体（Release，自包含，几分钟）
echo ============================================
dotnet publish src\TaoDown.App\TaoDown.App.csproj -c Release -r win-x64 --self-contained true -p:WindowsAppSDKSelfContained=true -p:Platform=x64 -o out\publish
if errorlevel 1 ( echo [X] 发布失败，请把上面红字截给我。 & pause & exit /b 1 )
echo ============================================
echo [2/3] 生成安装包 MSI
echo ============================================
powershell -NoProfile -ExecutionPolicy Bypass -File tools\msi\package.ps1 -PublishDir "%~dp0out\publish" -OutDir "%~dp0out\msi"
if errorlevel 1 ( echo [X] 打包失败，请把上面红字截给我。 & pause & exit /b 1 )
echo ============================================
echo [3/3] 校验安装包完整性
echo ============================================
for %%F in ("%~dp0out\msi\*.msi") do set "MSIFILE=%%~fF"
powershell -NoProfile -ExecutionPolicy Bypass -File tools\msi\verify_msi.ps1 -MsiPath "%MSIFILE%"
if errorlevel 1 ( echo [X] 校验未过，把输出发我。 & pause & exit /b 1 )
echo ============================================
echo 完成！安装包在 out\msi 目录里，装它即可。
echo ============================================
pause
