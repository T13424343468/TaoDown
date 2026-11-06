# TaoDown ARCHITECTURE

版本：v0.1（2026-10-31 立项稿）

## 1. 总体定位

- WinUI 3 + .NET 8 桌面应用，界面与操作逻辑对齐 TaoPad（太极图标、简洁布局、浅色/水墨配色）。
- 免费、无广告、无捆绑、免安装；工具本身不做任何限速。
- 合规红线：不接入迅雷 P2SP 私有加速网络；不做任何限速绕过。

## 2. 三层结构

```
TaoDown.App      界面层（WinUI 3）：主窗口、任务列表、添加任务、设置页
TaoDown.Core     服务层（.NET 8）：业务模型、下载服务接口、任务分类
TaoDown.Engine   引擎层（.NET 8）：aria2 JSON-RPC 封装、引擎进程托管、链接解析（thunder/flashget/qqdl）
TaoDown.Core.Tests  单元测试（xUnit）
```

依赖方向：App → Core、App → Engine、Engine → Core；Core 不反向依赖。

## 3. 下载引擎（aria2-next 封装）

- 采用 aria2-next（aria2 活跃分支，开源 GPL-2.0，2026-10 仍在维护，v3.0.9）作为下载内核，`aria2-next.exe` 随应用分发，以子进程方式托管，进程内独占 RPC 端口（默认 16800）与随机密钥。
- 选型理由（2026-10-31 批次四核实）：原版 aria2 不支持 ed2k（仅 HTTP(S)/FTP/SFTP/BT/Metalink）；aria2-next 在保持 JSON-RPC/命令行兼容的前提下原生支持 ED2K/电驴与 thunder 链接，兑现"全部主流协议"的承诺。
- 通过 JSON-RPC（HTTP）调用：
  - `aria2.addUri`：HTTP(S)/FTP/磁力/ed2k 直链添加，多连接分段（`--split` / `--max-connection-per-server`）。
  - `aria2.addTorrent`：本地 .torrent 文件。
  - `aria2.unpause / pause / forceRemove / tellStatus / getGlobalStat`：任务控制与进度查询。
- 启动参数要点：`--continue=true`（断点续传）、`--enable-dht=true`（BT/DHT）、`--bt-save-metadata=true`（磁力自动获取种子元数据）、`--file-allocation=none`；设置页的速度上限与做种时长经 `--max-overall-download-limit` / `--seed-time` 传入。
- 引擎进程崩溃时由宿主类（`Aria2EngineHost`）负责重启与状态恢复。

## 4. 链接解析

- `LinkClassifier`（Core）：按协议前缀分类 http/ftp/magnet/ed2k/thunder 等。
- `ThunderLinkParser`（Engine）：
  - `thunder://`：base64 解码，去掉首尾 `AA`/`ZZ`，还原真实地址后交引擎。
  - `flashget://`：base64 解码并截断 `[EM=...]` 后缀。
  - `qqdl://`：base64 解码。
- 解析失败返回 null 并提示用户，不做猜测性下载。

## 5. 网盘聚合与账号管理（2026-11-02 移除）

- 按用户决定（聚焦下载器）整体移除：聚合服务组件、网盘浏览页、账号管理与配套测试均不再随包分发。
- 设计依据见 docs\DESIGN-DAO.md（本体纯一：产品只有一个本体——下载）。

## 6. 界面要点（对齐 TaoPad）

- 主窗口：顶部地址栏（粘贴即解析）+ 任务列表（名称/大小/进度/速度/状态）+ 底部全局速度。
- 太极图标、圆角卡片列表、浅色默认主题、设置页含并发数/下载目录/BT 做种策略。
- 剪贴板监视、浏览器接管、自动分类目录规则在批次三补充。

## 7. 风险与说明

- 沙箱内无法编译 WinUI 3（需 Windows SDK），App 层按规范编写并以静态自检兜底；Core/Engine/Tests 力求在沙箱内直接编译通过。
- ed2k 下载依赖网络中该文件来源，可用性不做承诺。

## 8. 浏览器接管（批次五）

- Chrome/Edge MV3 扩展（src\extension）：`downloads.onDeterminingFilename` 拦截并取消浏览器下载，URL/来源/文件名经 Native Messaging 转发本机宿主；右键菜单「用 TaoDown 下载」；m3u8/ts/mpd 分片放行浏览器。
- Native Messaging 宿主（src\TaoDown.Host，独立 exe）：校验协议白名单（http/https/ftp/magnet/ed2k）后把请求写入 `%LOCALAPPDATA%\TaoDown\inbox\`（临时文件+原子替换）。
- 主程序（BrowserInboxWatcher）：监视收件箱 → 弹出保存位置对话框（默认设置页「下载根目录」，可改）→ 入队下载并跳转下载页。
- 宿主注册（NativeHostRegistrar）：主程序启动时写 host manifest 与 HKCU 注册表（Chrome/Edge/Chromium），allowed_origins 锁定扩展固定 ID（manifest 内置 key）。
- 设置项：`BrowserTakeover`（设置页开关，关闭后恢复浏览器默认下载）。
- 链接协议接管（v2.45）：magnet/ed2k/thunder/flashget/qqdl 与 .torrent 文件关联写入 HKCU（`ProtocolRegistrar`，纯计划表可测），应用启动与设置保存时按 `ProtocolTakeover` 开关刷新；点链接/双击种子由命令行参数进入（`LaunchArgs.Parse`），二次启动经 `LaunchForward` 收件文件转发主实例后自动入队。
