# aria2 对接参考（RPC）

适用范围：TaoDown.Engine 对 aria2 的封装层。aria2 以独立进程运行，通过 JSON-RPC over HTTP/WS 控制，不修改 aria2 源码。

## 1. 进程与连接

- 启动参数建议：
  `aria2c --enable-rpc --rpc-listen-port=6800 --rpc-secret=<token> --dir=<默认目录> --continue=true --bt-enable-lpd=true --enable-dht=true --dht-file-path=<dat>`
- 认证：所有请求带 `token:<secret>` 作为 params 首元素；密钥随机生成，存于用户配置（不落日志）。
- 传输：v1 用 HTTP JSON-RPC（POST，method + params 数组）；后续要进度推送再升级 WebSocket。
- 生命周期：TaoDown 启动拉起 aria2，退出时 `aria2.shutdown`；崩溃检测看进程存活+RPC 心跳（`aria2.getVersion`）。

## 2. 常用接口清单

| 方法 | 用途 | 关键参数 |
| --- | --- | --- |
| addUri | 新增 HTTP(S)/FTP/磁力任务 | urls, options |
| addTorrent | 新增种子任务 | torrent 文件内容(base64), options |
| remove | 删除活动任务 | gid |
| pause / unpause | 暂停/恢复 | gid |
| forcePause / forceRemove | 不等上报立即停 | gid |
| tellStatus | 单任务状态 | gid, keys[] |
| tellActive | 活动任务列表 | keys[] |
| tellWaiting / tellStopped | 等待/已完成列表 | offset, num, keys[] |
| getGlobalStat | 全局速度/数量 | - |
| getOption / changeGlobalOption | 读/改全局配置 | options |
| getFiles / getPeers / getUris | 文件清单/Peer/来源 | gid |
| changePosition | 调整队列顺序 | gid, pos, how |
| changeUri | 修改任务 URL | gid, fileIndex, delUris, addUris |
| saveSession | 保存会话 | - |
| shutdown | 优雅退出 | - |
| removeDownloadResult / purgeDownloadResult | 清理记录 | gid / - |

常用 option：`dir`、`out`（文件名）、`header`、`user-agent`、`max-connection-per-server`、`split`、`select-file`（种子选文件）、`pause=true`（加队即暂停，便于入队后统一展示）。

## 3. BT / 磁力要点

- 元数据获取：把 magnet 直接 addUri 给 aria2（配置 `bt-save-metadata=true`），任务状态从 `active` 变为含 `.torrent` 产物即拿到元数据；此阶段 UI 显示“获取资源信息”，无速度是正常现象，注意别当成卡死。
- `follow-torrent=mem` 建议关闭，用 `true`/`false` 明确语义，避免本地内存盘差异。
- DHT 首次冷启动慢（节点表为空），需随安装包预置 `dht.dat` 或启动后预热 1-2 分钟；私有 tracker 列表允许用户自定义追加。
- 判活提示：`seeder` 为 0 且长时间无 Peer 时，向用户提示“资源冷门/无做种”，不算程序故障。

## 4. 状态与进度映射

- tellStatus 关键字段：`status`（active/waiting/paused/error/complete/removed）、`totalLength`、`completedLength`、`downloadSpeed`、`connections`、`errorCode`。
- 速度节流：RPC 轮询建议 500ms-1s；UI 平滑用 3 点滑动平均，避免跳动。
- 错误码需映射成中文提示（如 13=磁盘空间不足、24=授权失败），映射表放 Engine 层统一维护。

## 5. 合规红线（照抄不可删）

- 不绕过任何官方限速机制；不用迅雷 P2SP 协议；BT 下载默认做种时长配置项需存在（默认建议 0，由用户自选）。

## 6. 测试要点

- RPC：token 错误、aria2 未启动、任务中途 kill 的恢复（saveSession + 重启续传）。
- BT：纯磁力取元数据超时路径、select-file 部分选择、DHT 冷启动。
