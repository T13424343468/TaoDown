# TaoDown BATCH-PLAN

版本：v0.1（2026-10-31）

## 批次一：主界面 + 下载引擎封装

- 解决方案骨架（App/Core/Engine/Tests 三层结构）。
- aria2 进程托管与 JSON-RPC 客户端（addUri/addTorrent/pause/unpause/remove/tellStatus/globalStat）。
- HTTP(S)/FTP 多线程分段下载、断点续传；BT 种子；磁力；ed2k；thunder/flashget/qqdl 链接解析。
- 主窗口：地址栏添加任务、任务列表、进度/速度展示、暂停/恢复/删除。
- 验收：沙箱可编译部分全绿；引擎参数与协议覆盖自检清单通过。

## 批次二：网盘聚合 + 账号管理（已取消）

- 2026-11-02 按用户决定（聚焦下载器）整体移除；2026-11-07 清理最后残留：移除 openlist.exe 随包引用、删除 ref-OpenList-Integration.md、修正安装器组件描述。产品自此只含下载本体（双引擎）与浏览器插件。

## 批次三：队列与体验增强

- 下载队列（并发上限、优先级、定时开始）。
- 速度限制设置（引擎级 --max-overall-download-limit）。
- 自动分类（视频/音频/压缩包/文档/安装包分目录）。
- 剪贴板监视与浏览器接管（自定义协议 + 浏览器扩展说明）。
- 验收：规则引擎单测通过；设置页读写生效。

## 批次四：安装器、打包与文档

- 一键打包脚本（含 aria2c.exe 双引擎发行版，CRLF/UTF-8 中文文件名规范遵循；OpenList 已移除）。
- 安装器（对齐既有微信风格安装器）与签名。
- 全套文档：图文 HTML + 纯文字/导图 Word。
- 全量自检自测修复后一次性交付。

## 排期说明

- 2026-10-31 16:03 起直接开工，不再等待 TaoPad 批次 B。
- 与 TaoPad/TaoDBM/TaoIDE/学习包并行推进；批次一完成即实时播报。

## 批次五（2026-10-31 21:56 用户追加）：浏览器接管下载插件

- Chrome/Edge MV3 扩展：拦截浏览器下载转交 TaoDown，右键菜单接管，连接状态角标。
- Native Messaging 宿主与自动注册；主程序收件箱监视 + 保存位置弹窗（默认选中默认下载位置）。
- 设置页新增「默认下载位置」沿用下载根目录 + 浏览器接管开关。
- 交付：随 MSI 一并安装；文档 docs\BROWSER-EXTENSION.md。
- 状态：已完成（见 docs\SELF-CHECK-LOG.md 2026-10-31 22:05），App 层待真机。
