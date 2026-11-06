# TaoDown

免费、无广告、合法合规的多协议下载工具（WinUI 3 + .NET 8），界面与操作对齐 TaoPad。

## 功能

- 下载协议：HTTP(S)/FTP 直链多线程分段、BT 种子、磁力链接、ed2k（电驴）；thunder://、flashget://、qqdl:// 链接自动解析
- 全任务断点续传、并发队列、优先级、定时开始、自动分类（视频/音频/压缩包/文档/安装包/图片）
- 链接智能补全：裸哈希自动转磁力链、「文件名|大小|哈希」碎片自动补全为完整 ed2k 链接
- 下载列表：每行实时显示已下载/总大小、下载速度、剩余时间、连接数、BT 上传速度与做种数、失败原因；状态徽章着色；行内一键暂停/继续/打开文件夹/删除；双击完成任务直接打开文件夹
- 批量操作：全部暂停 / 全部继续 / 清理已完成（文件保留在磁盘）；按「全部/进行中/已完成」筛选；底部汇总（任务数与总速度）
- 浏览器接入引导：首次启动自动检测已安装浏览器（Chrome/Edge/夸克/360/Brave/Vivaldi/Opera），一次性弹窗指路，一键打开插件文件夹或图文指引（docs\BROWSER-EXTENSION-GUIDE.html）
- 剪贴板监视：复制下载链接自动填入新建任务
- 链接协议接管：装好即接管磁力/电骡/迅雷/快车/旋风链接与 .torrent 种子文件，点链接直接唤起 TaoDown 并自动开始下载（设置可关）
- 开机自启（可选）：托盘常驻待命，下载随时接手
- 下载完成/失败托盘气泡通知：不用盯着窗口等
- 拖拽添加：把链接或种子文件拖进窗口即开始下载
- 浏览器接管：装完重启浏览器即自动挂载插件（Chrome/Edge/Brave/Vivaldi 等，免配置免操作），点下载即弹出保存位置（默认选中默认下载位置，可改），支持右键「用 TaoDown 下载」；夸克/360 等内核手动加载一次；详见 docs\BROWSER-EXTENSION.md
- 工具本身不限速，不做任何限速绕过

## 一键构建（Windows 10 2004+，需 .NET 8 SDK）

双击或命令行运行 `build.bat`，最终产物只有一项（Windows 只出 MSI）：

```
out\TaoDown-2.54.0.0-x64.msi    安装器：应用、双引擎、BROWSER-EXTENSION全内置
```

构建步骤：还原依赖 → 编译 → 自包含发布（含 .NET 8 与 Windows App SDK 运行时）→ 整理暂存目录（应用 + 引擎 + BROWSER-EXTENSION + 已签名插件包）→ 校验 → 生成离线 msi。安装包在安装期直接写浏览器插件登记（机器级，卸载自动清除）；Chrome/Edge 等重启浏览器即自动挂载，夸克/360 等内核手动加载一次（见 docs\BROWSER-EXTENSION.md）。

注意：WinUI 界面编译器不支持含中文等非英文字符的路径。若解压目录路径含中文，`build.bat` 会自动把源码镜像到纯英文路径（`%ProgramData%\TaoDown\Build`）完成构建，并把安装包复制回本目录的 `out` 文件夹；也可以手动把文件夹名改成纯英文后直接运行。

## 目录结构

- `src\TaoDown.App` —— WinUI 3 界面层（主窗口两页：下载 / 设置；浏览器请求弹窗）
- `src\TaoDown.Core` —— 服务层（任务模型、队列、自动分类、链接分类、Native Messaging 帧协议）
- `src\TaoDown.Engine` —— 引擎层（aria2-next / aria2c 双引擎托管，JSON-RPC 控制）
- `src\TaoDown.Host` —— BROWSER-EXTENSION Native Messaging 宿主（收件箱交接）
- `src\TaoDown.Core.Tests` —— 三层测试 + 暴力测试，共 230 例（见 docs\TEST-STRATEGY.md）
- `src\extension` —— 浏览器扩展源码与已签名插件包（Chromium 系 MV3；签名打包见 tools\extension\pack_crx.py）
- `docs\` —— ARCHITECTURE、BATCH-PLAN、SELF-CHECK-LOG、BROWSER-EXTENSION说明、对接参考
- `tools\vendor\` —— 随包分发的开源组件与许可证文本（见 THIRD-PARTY-NOTICES.txt）

## 开源组件

- aria2-next（GPL-2.0）：HTTP(S)/BT/磁力/ed2k 引擎
- aria2 1.37.0（GPL-2.0）：FTP 引擎

本项目源码按 GPL-2.0 兼容方式分发；许可证文本随包内置。
