# OpenList 网盘聚合对接设计参考

适用范围：TaoDown 网盘聚合与账号管理模块。结论先行：不逐家适配网盘私有 API，统一通过用户自建/局域网 OpenList（AList 社区分支）实例对接，TaoDown 走其 HTTP API 与 WebDAV。

## 1. OpenList 是什么、为什么选它

- OpenList 是 AList 的社区分支（AGPL-3.0），保留多网盘挂载核心功能，支持 20+ 网盘（夸克、百度、阿里云盘、OneDrive 等），社区活跃、文档在 doc.oplist.org.cn。
- 默认端口 5244；管理页 `http://<host>:5244/@manage`；普通页面为根路径。
- 对 TaoDown 的价值：一份对接代码覆盖几十家网盘；账号凭据由 OpenList 统一保管，TaoDown 不接触各家 Cookie/Token，降低合规与维护负担。

## 2. 账号接入模式（TaoDown 侧）

- 连接信息三要素：地址（host:port）、用户名、密码（或已有 token）。存放在 TaoDown 本地凭据库，界面命名“网盘服务连接”。
- 建议引导文案：先部署 OpenList（Windows 版 openlist.exe / Docker / 一键脚本），在 OpenList 管理页挂载各家网盘，然后到 TaoDown 填连接信息。
- Token 获取两种模式：
  1. 账密模式：POST `/api/auth/login` {username, password} → 返回 token，之后请求头 `Authorization: <token>`。
  2. OAuth 网盘 Token 模式（OpenList 提供）：各家网盘的 OAuth token 通过 https://api.oplist.org/ 获取（访问令牌+刷新令牌），填入 OpenList 对应存储驱动配置；TaoDown 不参与此环节。
- 凭据安全：token 只存本地加密配置，不上报、不写日志（用户隐私红线）。

## 3. 文件浏览：HTTP API 为主

- 列目录：POST `/api/fs/list`，body `{path, page, per_page, password, refresh}`；返回 `content[]`（name, size, is_dir, modified, sign）。
- 详情/直链：POST `/api/fs/get`，body `{path, password}`；返回 `raw_url`（可直接交给下载引擎）与 `sign`。
- 元信息：POST `/api/fs/about`（容量）等；响应统一 `{code, message, data}`，code=200 为成功。
- 分页大目录：`per_page` 建议 100；`refresh=true` 强制穿透缓存。
- 加密目录：带 password 字段；sign 仅部分站点需要，get 返回的 sign 随 raw_url 拼接使用。

## 4. 备选通道：WebDAV

- OpenList 全站支持 WebDAV：`http://<host>:5244/dav/<挂载路径>`，账密同登录账号。
- 用途：作为 HTTP API 故障时的兜底，以及未来“挂载为本地盘”功能的通道；v1 不实现 WebDAV 客户端，仅预留抽象接口。

## 5. 与下载引擎的衔接

- 流程：fs/list 浏览 → 用户选择 → fs/get 取 raw_url → 交给 aria2 addUri（带原站 UA/Referer 由 OpenList 处理，TaoDown 侧无需伪装）→ 进度走统一任务中心。
- 转存场景（网盘互拷）v1 不做，记入后续批次候选。

## 6. 边界与提示

- 不绕过任何网盘官方限速；下载速度取决于网盘开放能力与 OpenList 中转，UI 速度说明需如实展示。
- 用户未部署 OpenList 时，网盘模块显示引导而非报错；连接失败区分“地址不通/凭据错误/版本不兼容”。

## 7. 测试要点

- 登录失败三类错误码；token 过期重登；大目录分页；raw_url 过期重取；断网重连。
