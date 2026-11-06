# 多协议链接识别与解析参考

适用范围：TaoDown 链接解析模块（LinkParser）。本文只讲识别规则与解析要点，不含 UI 与下载实现。

## 1. HTTP / HTTPS

- 识别：以 `http://` 或 `https://` 开头；Windows 消息或剪贴板中常带引号、括号等包裹，需做 trim。
- 解析要点：
  - 提取 URI 中的文件名：优先 `Content-Disposition`（响应头），其次 URL path 最后一段并做 URL 解码（`%20` 等百分号编码）。
  - 预检阶段只发 HEAD/部分 GET：读取 `Content-Length`、`Accept-Ranges`（判断断点续传）、`ETag/Last-Modified`。
  - 直链与网页链接区分：响应 `Content-Type` 为 `text/html` 时通常不是直链，交由用户决定或跳过，不做网页抓取下载（合规边界）。
  - 重定向跟随上限建议 10 次；记录最终 URL 用于断点续传一致性校验。

## 2. FTP

- 识别：以 `ftp://` 或 `ftps://` 开头。
- 语法：`ftp://[user[:password]@]host[:port]/path`；缺省端口 21（ftps 为 990）。
- 解析要点：
  - 匿名登录约定：用户缺省填 `anonymous`，密码填 `taodown@example.com` 之类占位。
  - 路径含中文或空格时需保留原样传给引擎，不做二次编码；显示层再做解码。
  - 被动模式（PASV）默认开启；SIZE 命令预检文件大小，不支持时下载中以实际字节数为准。
  - FTPS 分显式（`ftps://`，TLS 握手后升级）与隐式（旧式 990 端口），解析层只需透传协议头，模式选择交给引擎配置。

## 3. Magnet 磁力链

- 识别：以 `magnet:?` 开头。
- 关键参数（每段用 `&` 分隔）：
  - `xt=urn:btih:<40位十六进制 或 32位Base32>`——InfoHash，唯一标识，必备。
  - `xt=urn:btmh:44...`——BTv2 多哈希，较新，识别后按 BTv2 处理。
  - `dn=`——显示名（URL 编码）；`tr=`——tracker，可多个；`xl=`——总长度。
- 解析要点：
  - 多值参数（tr）必须解析成列表，去重后注入 BT 元数据获取阶段。
  - 纯磁力链（无 xt）丢弃并提示不支持。
  - 下载流程：先取元数据（见 aria2 对接参考第 3 节），拿到文件清单与单文件选择后再开始传数据；纯磁力任务在 UI 上先展示“正在获取资源信息”。

## 4. ed2k 电骡链

- 识别：以 `ed2k://` 开头。
- 语法：`ed2k://|file|<文件名>|<字节数>|<32位MD4十六进制>|/`，可追加 `|h=<AICH哈希>|`、`|s=<HTTP直链>|`、`|p=<哈希集>|` 段。
- 解析要点：
  - 四段式结构以 `|` 分隔，按位置取值而不是按名字取值。
  - `s=` 段给出 HTTP 直链时，可降级为 HTTP 任务（部分场景可行），降级策略做成配置项。
  - eMule 网络接入为独立引擎（如 aMule 内核库），TaoDown v1 是否内置由批次决策；解析层先完成识别与字段校验（文件名非空、大小为正整数、MD4 为 32 位十六进制）。

## 5. Thunder 迅雷专链

- 识别：以 `thunder://` 开头。
- 编码：`thunder://` 后是 Base64，解码前先补齐标准头部 `AA`、尾部 `ZZ`：即 `Base64Decode(thunder://X)` 通常得到 `AA<真实URL>ZZ`，剥掉 AA/ZZ 后得到 http/https/ftp/ed2k 之一的真实地址。
- 解析要点：
  - Base64 可能是 URL-safe 变体或缺 padding，解码失败时尝试补 `=`。
  - 解出的真实链接再递归走对应协议的识别逻辑（thunder 包 ed2k 很常见）。
  - 合规边界：只做专链解码转直链下载，不接入迅雷 P2SP 网络、不使用迅雷会员加速通道（用户红线，写进解析模块注释）。

## 6. 统一解析出口

- 输入：原始字符串（剪贴板、拖放、手输）。
- 步骤：trim → 协议识别（前缀表驱动）→ 各协议子解析 → 产出统一 `DownloadTaskCandidate { scheme, display_name, size_hint, engine_hint, raw_uri }`。
- 无法识别时不弹错误，归入“未知链接”供用户手动选引擎。

## 7. 测试要点（对齐 TaoDown.Core.Tests）

- 每协议至少：标准例、带中文/空格编码例、缺参非法例、thunder 嵌套例。
- 大小写不敏感：`MAGNET:?`、`Thunder://` 均须识别。
