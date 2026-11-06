# TaoDown SELF-CHECK-LOG

## 2026-10-31 23:10 UiSyntaxCheck 类型桩编译级拦截（移植自 TaoZip）

- 移植 TaoZip 已验证的桩检查方案：tools/UiSyntaxCheck（Stubs.cs + ControlStubs.cs + PartialFields.cs），把全部界面 code-behind + BrowserInboxWatcher + NativeHostRegistrar 用 WinUI 类型桩在沙箱以编译器真实诊断过一遍。
- **抓出 5 处真编译错误**（静态逐行审计三轮都没发现，全部会导致真机 XamlCompiler 退出码 1）：
  1. MainWindow.xaml.cs:23 `DispatcherQueue.CreateTimer()` 静态调用（真实 API 是实例方法）→ 改 `DispatcherQueue.GetForCurrentThread().CreateTimer()`。
  2. DownloadsPage.xaml.cs:199 `App.Main!.DispatcherQueue`（WinUI Window 无此属性）→ 改 `DispatcherQueue.GetForCurrentThread()`。
  3. BrowserInboxWatcher.cs:71 同上。
  4. BrowserInboxWatcher.cs:26 `NotifyFilters`（类型名误作属性名，正确属性为 `NotifyFilter`）。
  5. DownloadsPage.xaml.cs、BrowserInboxWatcher.cs 缺 `using TaoDown.Engine;`（ThunderLinkParser 在 Engine 命名空间，两处引用必编译失败——**最可能是真机失败根因**）。
- 假阳性（桩缺成员，已补桩排除）：DataPackageView.Contains/GetTextAsync、UIElement.Visibility、ListView.SelectedItems、StackPanel.MinWidth。
- 修复后桩检查 0 错误 0 关键警告；Core/Engine 复测 66/66 全绿。
- 结论：此前「界面代码合规」的审计结论被推翻——人眼审计不能替代编译器；此工具纳入交付前必跑清单。

## 2026-10-31 20:20 批次四（打包流水线）开工与组件落地

- **引擎切换（关键）**：核实原版 aria2 不支持 ed2k（仅 HTTP(S)/FTP/SFTP/BT/Metalink）；改用 aria2-next（aria2 活跃分支，GPL-2.0，v3.0.9，2026-10-29 仍在更新，776 star）——JSON-RPC 兼容且原生支持 ED2K/电驴/thunder，兑现全部协议承诺。
  - 代码侧：`Aria2EngineHost.DefaultExePath` → `engine\aria2-next.exe`，异常提示与注释同步；Core/Engine 重编译 0 错 0 警，单测 **49/49 全绿**（复跑确认无破坏）。
- **组件预取与校验（沙箱完成，交付即离线）**：
  - `tools/vendor/aria2-next/aria2-next.exe`（18.3MB，PE32+ x64 校验通过）；
  - `tools/vendor/openlist/openlist.exe`（156.9MB，zip 解压 CRC 校验 OK）；
  - `tools/vendor/LICENSE-aria2-next.txt`（GPL-2.0）、`LICENSE-openlist.txt`（AGPL-3.0）已随包内置；
  - GitHub 直连不稳（多次 502/断流），改用镜像（gh-proxy.com）完成下载。
- **打包流水线（Windows 端执行）**：
  - `build.bat`（GBK+CRLF）六步：还原→编译→自包含发布（.NET 8 + Windows App SDK 运行时随包，`WindowsAppSDKSelfContained=true`，用户机免装运行库）→重建 out（绿色版 `out\exe\` + engine + openlist + 许可证）→校验主程序与组件存在→生成 msi。
  - `tools/msi/package.ps1`（UTF-8 BOM）：微信风格三步向导（WixUI_InstallDir，无许可证页），桌面+开始菜单快捷方式自动创建，同版本可覆盖安装（AllowSameVersionUpgrades），枚举 out\exe 全部文件（含引擎与 OpenList）进 msi，`-pdbtype none` 不出调试文件，产物 `out\TaoDown-$PkgVersion-x64.msi`（文件名带版本号，如 TaoDown-2.54.0.0-x64.msi）。
  - 离线 WiX 6.0.2（tools/msi/wix，12MB，自 TaoZip 同款）随包内置，用户机零联网。
- **沙箱验证**：wix 工具可在 .NET 8 下加载（--version = 6.0.2）；wxs 语法与已验证的 TaoZip 模式一致；msi 生成只能在 Windows 端（Linux 缺 msi.dll，与既有结论一致）。
- 待办：全套开发文档（图文 HTML、纯文字、导图 Word）、源码包整理、交付清单；真机编译与打包实测。

## 2026-10-31 19:05 批次二/三（界面层）静态自检（沙箱无 WinUI，逐项核对）

- 新增界面（App 层）：
  - `MainWindow` 改为 NavigationView 左侧导航（下载/网盘/账号/设置），Frame 承载四页；800ms 进度刷新定时器与任务更新转发上移至主窗口；剪贴板监视（批次三）在主窗口实现：捕获下载链接 → 填入下载页输入框并提示。
  - `DownloadsPage`：链接添加（经队列提交，并发/优先级统一管理）、种子文件选择、任务列表、暂停/继续/删除（多选批量）、双击切换暂停/继续、跨页返回自动重载。
  - `CloudPage`：OpenList 目录浏览（路径栏/上级/刷新/双击进入）、文件一键拉取下载（取直链 → 按类型自动分类入队）、未连接时的引导提示。
  - `AccountsPage`：OpenList 管理密码保存（加密落盘）与连接、网盘账号添加（六类网盘）、设为当前、删除；应用启动时自动重连 OpenList。
  - `SettingsPage`：下载目录、并发数（立即生效于队列）、速度上限、自动分类、剪贴板监视、BT 做种时长；开机会自启动预留（禁用态）。
- 引擎侧配套：`Aria2Options` 增 `OverallSpeedLimitKB`、`SeedMinutes`；`BuildArguments` 消费（0=不限速/不做种时不变）——新测试 2 例验证。
- 静态核对项（App 无法沙箱编译，逐项人工核对）：
  - 20 个 XAML 事件（Click×16、DoubleTapped×2、KeyDown、SelectionChanged）与代码侧处理函数一一对应，签名类型匹配（RoutedEventHandler/TypedEventHandler/DoubleTappedRoutedEventArgs/KeyRoutedEventArgs）。
  - x:Bind DataTemplate 均带 x:DataType 且类型在对应命名空间存在（TaskRow/NetdiskEntryRow/AccountRow）。
  - 控件名与代码引用交叉核对无缺失（仅 Click 绑定的按钮未在代码访问，正常）。
  - 可空流转：App/Cloud/Token 等静态属性先取局部变量再跨 await 使用，消除 CS8604 隐患。
  - 符号图标用 FontIcon 内置字形（E896/E753/E716/E713），避免 Symbol 枚举兼容性问题。
  - 全部新文件补 UTF-8 BOM（11 个新增 + 3 个修改）。
- 真实编译与测试：Core/Engine 0 错误 0 警告；**49/49 全绿**（新增 2 例：速度上限与做种参数注入）。
- App（WinUI 3）仍待真机编译；界面外观微调待真机确认。

## 2026-10-31 17:58 批次二/三（服务与引擎层）编译与测试（沙箱真实执行）

- 新增代码：
  - Core：`Settings`（设置 JSON 存储，临时文件原子替换）、`AutoClassifier`（扩展名→视频/音频/压缩包/文档/安装包/图片分类目录）、`DownloadQueue`（并发上限/优先级/定时开始，占坑式并发控制）。
  - Engine：`OpenListClient`（登录/列目录/取直链/健康检查）、`OpenListHost`（OpenList 独立进程托管 + /ping 就绪等待）。
  - 批次一收口：`Aria2EngineHost` 增加端口冲突自动顺延（IsPortFree，最多 20 个候选）与启动重试（3 次）、崩溃自动重启 `EnsureRunningAsync`。
  - 全部源码补 UTF-8 BOM（31 个文件），符合 Windows 交付编码规范。
- 测试：**48/48 全绿**（新增 26 例：分类、设置回环、队列并发/优先级/定时/移除、OpenList 登录/列目录/直链/错误码/探活、宿主参数、端口检测）。
- 测试抓出并修复的真问题：
  - OpenListClient 请求用相对路径导致无效 URI → 改为拼 BaseUrl（真 bug）。
  - DownloadQueue 并发提交竞态可能超限 → 改为先占坑再提交。
  - `await using` 误用、static 方法引用实例字段等编译错误 2 处。
- 编译：Core/Engine 0 错误；DPAPI 平台警告用 `[SupportedOSPlatform("windows")]` 消除。
- 界面层（网盘浏览页、账号管理页、设置页、剪贴板监视）待下一批；App（WinUI 3）仍待真机编译。

## 2026-10-31 17:10 编译与测试（沙箱真实执行）

- 环境：沙箱内已有 .NET SDK 8.0.427（`/root/workspace/.local/dotnet`，非 PATH 预装；此前"沙箱无 SDK"的结论作废）。
- 编译：TaoDown.Core ✅ 0 错误 0 警告；TaoDown.Engine ✅ 0 错误 0 警告。
- 单元测试：22/22 全部通过（0 失败 0 跳过），含链接分类、迅雷/快车/旋风解析、RPC 请求体、引擎启动参数、账号存取回环。
- 修复：真实编译抓出静态自检漏掉的错误——`Aria2DownloadService.ApplyStatus` 为 static 方法却使用 `this` 触发事件 → 改为实例方法。
- App（WinUI 3）需 Windows + Windows App SDK，仍待真机编译验证。

## 2026-10-31 16:05 开工阶段静态自检（历史）

1. **结构**：四项目分层与依赖方向正确（App→Core/Engine，Engine→Core，Core 无反向依赖）。
2. **命名**：类型与成员为简短英文语义化命名（DownloadTask、Aria2Client、ThunderLinkParser 等），符合既有约定。
3. **逻辑一致性（自检发现并已修复 5 处）**：
   - MainWindow.xaml.cs 缺 `System.Collections.ObjectModel`、`System.ComponentModel`、`Microsoft.UI.Dispatching` 引用 → 已补。
   - MainWindow 调用 `AddTorrentAsync` 参数与签名不符 → 已补 `saveDir` 参数。
   - Aria2DownloadService 误在 DownloadTask 上触发服务级事件（两处）→ 改为 `Updated?.Invoke(this, task)`。
   - 解析 aria2 files 字段时未判空数组即取属性 → 增加 `ValueKind == Object` 判断。
   - Engine 缺 DPAPI 包引用（System.Security.Cryptography.ProtectedData）→ 已补 8.0.0。
4. **合规**：无迅雷 P2SP 接入代码；无网盘限速绕过逻辑；OpenList 以独立组件分发并在架构文档注明 AGPL 合规方式。

## 遗留事项（下一批处理）

- WinUI 3 真机编译与界面微调（太极图标、水墨配色批次一收口时接入）。
- 引擎崩溃自动重启、RPC 端口冲突重试策略（批次一收口项）。
- aria2c.exe 与 OpenList 发行版的获取与随包分发（批次四打包时定版）。

## 2026-10-31 22:05 批次五（浏览器接管插件）自检

1. **真实编译**：沙箱 .NET 8（8.0.427）编译 TaoDown.Host 0 错误；Core/Tests 全量编译通过。
2. **测试**：新增 NativeMessageTests 17 例（帧编解码回环/双帧/空流/超长/半帧、请求解析合法/缺失字段/协议白名单/文件名消毒）。全套 66/66 全绿。
   - 测试抓出 1 个真问题：文件名消毒原用 `Path.GetInvalidFileNameChars()`（平台相关，Linux 下不拦截 `<`/`>`/`\`），改为显式非法字符表，跨平台行为一致。
3. **端到端实测（沙箱）**：模拟浏览器宿主会话——合法请求落收件箱 JSON 字段正确（Url/Referrer/Filename/ReceivedAt）；`file://` 协议被拒（"不支持的协议"）；非 JSON 输入被拒（明确错误信息）。
4. **扩展静态自检**：manifest.json JSON 校验通过；MV3 权限最小化（downloads/nativeMessaging/contextMenus/storage）；扩展 ID 经内置 key 固定为 mhbhaogipooncclfkmcdfhldfbpjakgi，宿主 allowed_origins 仅允许该 ID；background.js 拦截/取消/转发逻辑与边缘情况（m3u8/ts/mpd 放行）齐备。
5. **编码规范**：新 .cs 全部 UTF-8 BOM；build.bat GBK+CRLF（校验通过）；JSON/JS UTF-8 无 BOM。
6. **App 层待真机**：BrowserInboxWatcher 弹窗（ContentDialog + 默认目录）、NativeHostRegistrar 注册表写入需真机验证；build.bat 整体需真机重跑。

## 2026-10-31 22:35 真机构建失败修复（v1.1）

用户真机 build.bat：还原/编译 Core、Engine 成功，TaoDown.App 在 XAML 编译阶段失败（XamlCompiler.exe 退出码 1，MSB3073）。逐行静态审计 src 全部 XAML 与 code-behind，定位并修复：

1. **【根因，最高优先】MainWindow.xaml 第 15 行：`IsSettingsButtonVisible="False"`** —— NavigationView 不存在该属性，正确属性为 `IsSettingsVisible`（微软官方文档确认："要隐藏设置项，请将 IsSettingsVisible 属性设置为 false"）。XamlCompiler 解析到未知属性即整项目编译失败，与真机现象完全吻合。已改为 `IsSettingsVisible="False"`。
2. **NU1603 警告**：TaoDown.App.csproj 中 Microsoft.WindowsAppSDK 版本号 1.5.240624003 不存在 → 钉定为 1.5.240627000（真机实际解析的版本）。
3. **CS8602 警告**：NativeMessage.cs 第 36 行——`Uri.TryCreate` 失败时 `uri` 为 null 仍解引用 `uri.Scheme` → 拆分为两次判断，TryCreate 失败先返回"下载地址无效"。
4. **附带逻辑修复**：CloudPage 进入目录/下载文件用了 `row.Name`（带 📁/📄 图标前缀的显示名）拼路径 → 改用 `row.SourceName`（真实文件名），否则网盘子目录浏览和下载路径必错。
5. **其余审计通过**：全部 x:Bind 均有 x:DataType 且属性存在（TaskRow/NetdiskEntryRow/AccountRow 均为 public、位于 using:TaoDown.App）；全部事件处理器签名核对无误（Click=RoutedEventArgs、DoubleTapped=DoubleTappedRoutedEventArgs、KeyDown=KeyRoutedEventArgs、SelectionChanged=NavigationView 泛型签名）；无重复 x:Name（PageStatus 分属四个独立页面文件）；StaticResource 引用的 SubtitleTextBlockStyle 由 XamlControlsResources 提供；NumberBox/ToggleSwitch/ScrollViewer Padding 等 WinUI 3 属性均合法；XAML XML 转义与 xmlns 无误。
6. **验证**：修复后 dotnet test 全套 66/66 全绿；App 工程 C# 编译在沙箱推进至 XamlCompiler 步骤（沙箱无法运行 Windows 编译器，退出码 126 与真机 1 不同属环境差异）；XAML 修复项已在沙箱以同版本 WindowsAppSDK 1.5.240627000 复核属性表。请真机重跑 build.bat 确认。

## 2026-11-02 v2.48 真实浏览器 E2E 自检

1. **台架**：新增 tools/browser-e2e（run-e2e.mjs + stub-host.py + README），把未打包插件装进沙箱真实 Chromium 139 跑全链路：插件加载 + ID 一致、HTTP 直链→宿主收到下载请求、blob 分块抓取重组逐字节一致、data 链接后台解码重组一致。
2. **排查记录**：E2E 初跑直链/blob/data 全部超时。逐层排除后定位两层根因：
   - Playwright 用 CDP `Browser.setDownloadBehavior` 把下载收进自己的收件流程，`downloads.onDeterminingFilename` 整个不派发（裸 CDP 手工驱动时事件正常触发，计数埋点实证）。修法：在**浏览器级** CDP 会话把下载行为还原为默认（页面级会话还原无效）。
   - 插件抓完 blob 后立刻 `disconnect`，Native Messaging 是异步写管道，最后一条 `download-data-end` 被挤掉，宿主永远等不到收尾（消息流水实证：chunks 齐但 end 丢失）。修法：按「已发送未应答」计数等宿主回执，收到收尾应答再断开；断开时若收尾已发出视为完成，超时按失败发放弃指令。
3. **揪出真 bug（>32KB 文件必坏）**：内容脚本与后台 data 解码的分段 `btoa` 步长为 0x8000（非 3 的倍数），每段各自带 `=` 填充，拼出的 base64 含内嵌填充——宿主解码到第一处填充即截断，只有 ≤32KB 的文件碰巧没坏。修法：分段步长改 3 的倍数（32766），仅整条分块消息末段带填充；内容脚本同时改为 512KB 定长分块 + 跨读取携带余数，满足宿主单块 ≤512KB / base64 ≤1.4M 字符的硬约束。
4. **结果**：E2E 4/4 通过，连跑两轮稳定全绿；Core.Tests 233/233 绿；语法闸门 Build succeeded 0 错。
5. **扩展页地址真机确证回填**：夸克 `quark://extensions/?entry=mainmenu`（直接输 `quark://extensions` 是空白新标签页）、360 安全 `se://extensions`、360 极速 `chrome://extensions`；BrowserCatalog/BrowserGuide/设置页文案/单测/两份文档同步。
6. **打包**：v2.48 完整交付包 150 项（v2.47 清单 + browser-e2e 3 项），八组断言全过（源码 BOM、bat GBK+CRLF 六步标记、新扩展页锚点、verify_msi 插件门槛、2.48 版本、收尾回执与编码修复在包内、台架齐全、引擎两 exe 排包末尾）。
7. **边界如实**：Windows 注册表自动挂载与真实宿主（TaoDown.Host.exe）在 Linux 沙箱不可执行，真机验证步骤见 tools/browser-e2e/README.md 与 docs/BROWSER-EXTENSION.md；沙箱测试桩按同一 Native Messaging 协议回执。
## 2026-11-02 v2.49 真机四件套修复自检

1. **主题回切残留（水墨切走再切回导航窗格发黑）**：根因是旧「先反再落」两拍在切回浅色象时先把基调短暂落回深色，NavigationView 这类内部缓存配色的控件趁中间值把深色缓存住，回切水墨后窗格残留暗色（真机截图坐实：内容区 #FCFAF4 已浅、导航 #191715 仍黑）。修法对齐 TaoPad v76 母本（源码级核对过）：①ThemeService 两拍改「先归 Default 再落目标」（try/catch 包归零）；②MainWindow 钉色后新增 RefreshNavTheme 对导航控件同样归零再落，强制内部模板全量重取；③PinNavBrushes 取不到键时删掉旧钉值回落应用级字典，绝不把上一象颜色钉死在元素级。v2.46「禁归零」教训已复核：其失败前提是重绘依赖 ActualThemeChanged 事件，v2.47 起 Apply 两个分支都显式调 PaintTheme，不再赌事件，回归测试断言与注释同步改写。
2. **水墨默认色偏黄**：整字典对齐 Tao 系列母本标准：底 #F7F5EE→#FCFBF7、次底 #EFEBDF→#ECEAE2、内容层 #FCFAF4→#FEFDFB、标题栏/文字 #232323→#1C242C、卡片描边 #A89F8C→#D8D2C0、窗格 #EFEBDF→#F2F0EA、导航条目悬停/选中 #EAE5D6→#ECEAE2。黛青点缀 #52708A 与 SystemAccentColor 变体不改（与 TaoPad 同源）。53 键三字典键集一致校验过；剩余 R-B>14 的值均为暖墨文字/描边（与母本同值），非残留。
3. **插件弹窗恒显「响应异常」**：根因是弹窗发 {"type":"ping"}，宿主 Handle 无 ping 分支落入 default 被当下载请求解析失败回 ok:false。修法：TaoDown.Host 新增 `case "ping"` 回 {"ok":true,"detail":"pong"}；对真实构建出的宿主二进制按帧协议冒烟 3/3 过（ping/合法下载/坏地址），并固化为 tools/host-smoke.py 可重复断言。弹窗断连文案改为可执行的「重新安装或重启」指引；夸克扩展页红色「错误」与 Service Worker (无效) 防御性守卫：background.js 检测 downloads.onDeterminingFilename 缺失时降级告警不崩溃（个别内核裁剪 API 的兜底）。
4. **图标统一**：tools/icon/draw_icon.py 整文件重写为插件同款太极圆盘（icon128 实测几何 ×2：圆盘上下宣纸浅 #EEF0EB / 浓墨 #1C1E22、双鱼眼 r=8.5 圆心 ±28.5、宣纸色下载箭头轴 x=98.5），256 基底 4x 超采样 LANCZOS，重生成 src/TaoDown.App/Assets/taodown.ico 七档帧。图标链核对：csproj ApplicationIcon + Link=TaoDown.ico 发布名，AppWindow.SetIcon/托盘/MSI Icon TaoDownIco/ARPPRODUCTICON/桌面与开始菜单快捷方式全链路一文件全覆盖。
5. **全测**：Core.Tests 233/233 绿（ThemeDictionaryTests 断言同步新标准值）；宿主冒烟 3/3；真实浏览器 E2E 11/11 全绿（弹窗 ping 首次显示「已连接 TaoDown，可以接管下载。」——v2.48 时该断言被桩宿主一律回 ok 掩盖，本轮把桩宿主应答语义改为镜像真实宿主后仍全绿）；语法闸门 Build succeeded 0 错（桩体 ResourceDictionary 补齐真实 API Remove）。
6. **版本**：package.ps1 ScriptVersion v28-1102、PkgVersion 2.49.0.0；插件 manifest 1.1.0→1.1.1（key/扩展 ID 不变）。
## 2026-11-02 v2.50 零操作改造自检

1. **免操作全链路审计（用户要求「全部都分析一遍」）**：逐环节核对装完即用的每一跳，缺口与修法：
   - 登记时机：v2.49 只在主程序首启写登记 → 装完不启动就不挂载。修：MSI 安装期写机器级登记（ExtRegC 组件，HKMU，path 直指 [INSTALLFOLDER]extension\taodown.crx + version），卸载随组件自动清除；主程序启动再写 HKCU 兜底。
   - 登记内容：v2.47 目录型 path 只有 Chrome 认（Edge 官方文档确证 path 须指向打包文件，Edge 真机已证不认目录）。修：全部改写已签名 CRX3（tools/extension/pack_crx.py 按 Chromium 官方源码逐字节实现，自验回读+验签+ID 推导三方一致）。
   - 开关唯一真相：v2.47 插件弹窗开关与设置页并存（双开关错乱源）。修：precheck 协议（宿主回 takeover 状态），插件每次下载先问再取消；弹窗开关与 storage 权限移除；关掉后浏览器下载立即恢复正常。
   - 主程序未运行点下载：请求写进收件箱但永远没人处理。修：BrowserInboxWatcher.Start() 启动先扫存量按 LastWriteTime 排序处理（重叠靠文件已删跳过防重）。
   - 重复弹框：旧手动插件 + 新自动插件并存、个别内核双回调会双弹。修：内容级去重（url+filename 3 秒窗，>256 条清旧键）。
   - 升级残留：旧 ID 登记键不清理会双插件重复接管。修：CleanupLegacyExtensionEntries（HKCU+HKLM 全键清）+ 宿主 allowed_origins 新旧并存（旧插件转发不丢文件）。
2. **换钥匙定案**：旧私钥全工作区不存在（grep+find 双证）。新 RSA-2048：SPKI→manifest key，ID=dchnmdakoleaofgpkhddhmohcoomlimp（SHA256(SPKI)[:16].hex() 逐位映射 a-p；与 C# IdFromPublicKeySpki 同式核对）。用户唯一需要动手的一步：移除旧手动加载的插件副本（文档+引导弹窗已写明）。
3. **全测**：
   - Core.Tests 233/233 绿（BrowserCatalogTests 新公钥已知答案 + registry_paths.json 与 C# 推导逐键一致断言）。
   - 宿主冒烟（真实二进制）5/5：ping/合法下载/坏地址/precheck 无状态文件=接管/precheck 关闭=放行。
   - 真实浏览器 E2E 10/10 全绿（Chromium 139）：ID 一致（新 ID）、HTTP 直链、blob 分块重组、小 blob、data、弹窗无开关、ping 已连接、关闭放行、恢复接管、状态缺失默认接管。
   - 组包 155 项断言全过（v2.49 152 + taodown.crx + registry_paths.json + pack_crx.py；pem/pack_v2.49.py 排除）。
4. **MSI 断言升级**：verify_msi.ps1 加 taodown.crx 缺失硬门槛 + 落点断言 extension 目录 + Registry 表断言（registry_paths.json 全部 17+2 键必在、path 值必匹配 taodown\.crx），构建不绿不出包。
5. **边界如实**：沙箱无 Windows 注册表/pwsh/MSI 构建；登记真机行为由 build.bat + verify_msi.ps1 断言链与用户真机验收兜底；夸克/360 自动挂载不可行（内核只认开发者模式），保留手动加载 + 一键打开扩展页引导，属内核约束非产品缺口。


## 2026-11-07 网盘模块残留清理（v2.4.0）

1. **用户决定**：网盘整个模块砍掉，代码一并清理（此前 2026-11-02 已移除聚合服务/网盘浏览页/账号管理，本次清掉最后残留）。
2. **清理项**：移除 csproj 中 openlist.exe 随包引用与 Exists 保护；删除 docs/ref-OpenList-Integration.md；BATCH-PLAN 批次二标注取消并修正批次四打包描述；package.ps1 安装 Feature 描述去掉「网盘组件」；版本 2.3.0→2.4.0。
3. **保留项（属核心下载链，非网盘模块）**：LinkClassifier 对网盘签名下载直链的接管分类、浏览器接管询问窗、夸克接管链路（以 TaoDown 询问窗为准）。
4. **当次验证（沙箱）**：TaoDown.Core.Tests 全量 265/265 全绿（0 失败 0 跳过）；UiSyntaxCheck 编译 0 错误。Windows 端 MSI 打包与真机链路以 windows-ci 全绿为准。
