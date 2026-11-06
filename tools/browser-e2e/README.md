# 真实浏览器 E2E 台架（tools/browser-e2e）

回应 v2.48「修完用真实浏览器模拟测试」要求：不是模拟脚本，而是把插件本体装进**真实 Chromium**
（Playwright 持久上下文 + `--load-extension` 未打包加载），验证接管全链路。

## 覆盖的断言（11 项）

1. **插件加载 + ID 一致**：真实浏览器加载后，扩展 ID 与注册表登记的
   `mhbhaogipooncclfkmcdfhldfbpjakgi` 完全一致（由 manifest key 公钥推导）。
2. **HTTP(S) 直链接管**：点带 Content-Disposition 附件头的直链 →
   宿主收到 `{type:"download", url, filename, referrer}`。
3. **blob: 接管全链路**：页面 JS 生成 1.5MB blob（512KB×3 分块 + 余数）→ 点击下载 →
   内容脚本在页面内抓取 → 分块回传 → 宿主收到
   `download-data-start` → `download-data-chunk×N` → `download-data-end`，
   重组字节与源**逐字节一致**。
4. **小 blob 余数路径**：777 字节（不足一个 512KB 分块）同样逐字节一致。
5. **data: 链接接管**：512KB+321 字节 base64 data URL → 后台解码分块 →
   重组逐字节一致（跨 2 个分块）。
6. **弹窗默认开启接管**：弹窗页打开后复选框默认勾选。
7. **弹窗 ping 已连接**：弹窗状态显示「已连接 TaoDown」。
8. **关闭接管 → 落库且直链放行**：取消勾选后 `chrome.storage.local.enabled=false` 落库，
   再点直链宿主收不到任何下载请求（浏览器正常下载）。
9. **重新开启接管 → 落库**：勾选后 `enabled=true` 落库。
10. **重新开启 → 恢复接管**：再点直链，宿主重新收到下载请求。
11. 以上断言的字节校验用**位置敏感散列模式**（`byte(i) = (i×0x9E3779B1) 的高 8 位`），
    能察觉错序/重复/截断；旧版 `(i*7+13)&0xff` 周期恰为 256 字节，无法发现错序，已弃用。

## 运行方法

需要：Node 18+、`playwright`（全局或本地均可，可用 `PLAYWRIGHT_MODULE` 指定所在目录）、
Chromium 二进制（默认 `/usr/local/bin/chromium`，可用 `CHROMIUM_BIN` 指定）。

```bash
node run-e2e.mjs
```

台架自动：起本地 HTTP 测试服务 → 写 Native Messaging 登记清单（Linux 用户目录）→
启动真实 Chromium 加载插件 → 依次点各类下载链接 → 校验桩宿主收到的消息与重组字节 →
弹窗开关两轮验证。任一断言失败退出码为 1。

## 边界说明（重要）

- **Playwright 会旁路扩展接管链路（v2.48 实测根因）**：Playwright 用 CDP
  `Browser.setDownloadBehavior` 把下载收进自己的收件流程，浏览器于是不再向扩展派发
  `downloads.onDeterminingFilename`（表现为下载直接完成、监听器零命中）。台架已在
  **浏览器级** CDP 会话上把下载行为还原为默认；若换其他自动化框架驱动浏览器，
  需要同样处理，否则接管断言会全部超时失败。
- **弹窗页要带到前台再等状态（v2.48 实测）**：后台标签里 rAF 可能整段不触发、
  原生消息应答投递也会被拖住，`waitForFunction` 的 rAF 轮询会空转超时；
  台架用 `bringToFront()` + 定时读取状态文本，模拟用户真正打开弹窗的场景。
- **Windows 注册表自动挂载无法在 Linux 沙箱复现**（那是 WindowsInstaller/注册表机制）。
  本台架验证的是"插件装上之后能否正确接管下载"这一用户最关心的链路；
  自动挂载（HKCU\Software\<家>\Extensions\<ID>）与夸克扩展页真地址
  （quark://extensions/?entry=mainmenu）需在真机按主文档指引验证。
- 测试桩宿主（stub-host.py）只按协议回 `{ok:true}` 并落日志/重组文件，用于替代
  真正的 TaoDown.Host.exe（Windows 可执行文件，沙箱无法运行）。
- 桩宿主把收到的每条消息原样写入 `host-messages.jsonl`，可审计。
- FTP / 磁力 / ed2k / 迅雷链接属协议启动或独立通道，不走本插件的接管链路，
  不在本台架覆盖范围（协议解析由主程序自身单元测试覆盖）。
