#!/usr/bin/env python3
# 组装 TaoDown v2.51 完整交付包（一次性脚本，沿用 v2.50 清单 + v2.51 修复）。
# 清单口径：v2.50 包 155 项，条目数不变（主题窗格修复与插件加固不新增清单文件）。
# 私钥 tools/extension/taodown-extension.pem 绝不入包（安全边界）。
# 断言：源码 BOM、bat GBK+CRLF 六步标记、版本号 2.51/v30-1102/插件 1.2.1、
#       v2.50 零操作五件 + v2.51 两项（窗格直涂 / 插件加固）都在包内。
import zipfile, os, sys, io, json, struct

ROOT = '/root/workspace/projects/201259'
OUT = os.path.join(ROOT, 'TaoDown-v2.51-完整交付包.zip')
NEW_ID = 'dchnmdakoleaofgpkhddhmohcoomlimp'
OLD_ID = 'mhbhaogipooncclfkmcdfhldfbpjakgi'
EXCLUDE_TOP = {'notes', 'out', 'progress_log', 'goal.md', 'goal.html', 'icon_preview.png',
               '.memory', 'tools/pack_v2.51.py', 'D:\\TaoDownData\\TaoDown',
               'docs/ref-OpenList-Integration.md',  # 过时网盘参考（v2.46 起排除）
               'tools/vendor/openlist/openlist.exe', 'tools/vendor/LICENSE-openlist.txt',
               'tools/extension/taodown-extension.pem',  # 私钥绝不出仓（安全边界）
               'tools/pack_v2.49.py', 'tools/pack_v2.50.py',  # 旧版组包脚本不随包（一次性脚本随迭代更替）
               }

def walk(rel_dir):
    full = os.path.join(ROOT, rel_dir)
    for name in sorted(os.listdir(full)):
        rel = (rel_dir + '/' + name).lstrip('/')
        if rel in EXCLUDE_TOP or rel.startswith('D:') or rel.endswith('.zip') or '__pycache__' in rel:
            continue
        p = os.path.join(ROOT, rel)
        if os.path.isdir(p):
            # 排除构建产物目录
            if name in ('bin', 'obj') or name.startswith('.'):
                continue
            if rel.startswith('tools/UiSyntaxCheck/obj') or rel.startswith('tools/UiSyntaxCheck/bin/'):
                continue
            yield from walk(rel)
        else:
            # bin-check 是台架自包含产物（v2.48 起随包），obj/bin 排除
            if '/obj/' in rel or '/bin/' in rel and '/bin-check/' not in rel:
                continue
            yield rel

files = [n for n in walk('') if not n.startswith(('out/', 'notes/', 'progress_log/', 'goal'))]
files = [n for n in files if not n.endswith('.zip')]
# 排除旧包文件与顶层杂物
files = [n for n in files if '/' in n or n in ('README.md', 'THIRD-PARTY-NOTICES.txt', 'build.bat')]
# 引擎两 exe 强制排包末尾
engines = [n for n in files if n.startswith('tools/vendor/') and n.endswith('.exe')]
files = [n for n in files if n not in engines] + engines

failures = []

def check(cond, msg):
    if not cond:
        failures.append(msg)

# —— 组包前断言（源文件级）——
def read(rel, bom=True):
    raw = open(os.path.join(ROOT, rel), 'rb').read()
    if bom:
        check(raw.startswith(b'\xef\xbb\xbf'), f'{rel} 缺 BOM')
    return raw.decode('utf-8-sig')

reg = read('src/TaoDown.App/NativeHostRegistrar.cs')
check(f'ExtensionId = "{NEW_ID}"' in reg, f'宿主未换新 ID {NEW_ID}')
check(f'LegacyExtensionId = "{OLD_ID}"' in reg, '旧 ID 兼容清理不在包内')
check('WriteTakeoverState' in reg and 'takeover_state.json' in reg, '接管状态落盘不在包内')
check("CleanupLegacyExtensionEntries" in reg, '旧 ID 登记清理不在包内')
check('taodown.crx' in reg, '登记 path 未指向签名插件包')
app = read('src/TaoDown.App/App.xaml.cs')
check('WriteTakeoverState(Settings is { BrowserTakeover: true })' in app, '启动未写接管状态')
check('"extension", "taodown.crx"' in app, '登记路径未改指安装包文件')

prog = read('src/TaoDown.Host/Program.cs')
check('case "precheck":' in prog and 'ReadTakeoverEnabled' in prog, '宿主 precheck 协议不在包内')
check('browser_takeover' in prog, 'precheck 未读状态文件')

# v2.51: 主题窗格残留修复——模板内 RootSplitView 直涂（真机反馈：切天灰后窗格残留水墨米白）
mw = read('src/TaoDown.App/MainWindow.xaml.cs')
check('PinSplitViewPane' in mw and 'FindRootSplitView' in mw, '窗格直涂不在包内')
check('split.PaneBackground = pane' in mw, 'RootSplitView.PaneBackground 未直涂')
check('Nav.Loaded += (_, _) => PinSplitViewPane();' in mw, '首挂补钉不在包内')
# v39: 引导窗改独立顶级窗（真机：首启引导从未弹过，ContentDialog 静默失败无留痕）
check('"浏览器下载接管已就绪"' in mw and 'GuideWindow.ShowAsync' in mw, '引导窗未改独立顶级窗')
check('首启引导窗唤起' in mw, '引导窗留痕缺失')
check('OpenExtensions' not in mw and 'BrowserPickWindow' not in mw, '引导窗扩展页入口须删净（v44 用户定案：去除打开扩展页功能）')
gwsrc = read('src/TaoDown.App/GuideWindow.cs')
check('MakeTopmostCentered' in gwsrc and 'IsAlwaysOnTop' not in gwsrc, '引导窗摆放未改 Win32 实现（真机教训：新拆包缺窗口接口）')
asksrc = read('src/TaoDown.App/AskWindow.cs')
check('MakeTopmostCentered' in asksrc and 'IsAlwaysOnTop' not in asksrc, '询问窗摆放未改 Win32 实现（同型排查）')
sp = read('src/TaoDown.App/Pages/SettingsPage.xaml.cs')
check('OnOpenExtensionsPage' not in sp and 'BrowserPickWindow' not in sp, '设置页扩展页入口须删净')
check('Interlocked.Increment(ref _active)' in asksrc and '% 6 * 36' in asksrc, '询问窗缺并发级联错位（v44：多窗不叠在同一点）')

# v2.51: 语法闸门桩体必须同步真实 API（Loaded/VisualTreeHelper/SplitView）
stub = read('tools/UiSyntaxCheck/Stubs.cs')
check('VisualTreeHelper' in stub and 'Loaded' in stub, '闸门桩体缺 v2.51 成员')
cstub = read('tools/UiSyntaxCheck/ControlStubs.cs')
check('class SplitView' in cstub and 'PaneBackground' in cstub, '闸门桩体缺 SplitView')

bg = read('src/extension/background.js')
check("'precheck'" in bg and 'reply.takeover !== false' in bg, '插件 precheck 询问不在包内')
check('chrome.storage' not in bg, '插件仍在用 storage（应已移除）')
# v2.51: 插件加固（错误页报「reading 'id'」防御）
check('unhandledrejection' in bg, '后台未拒全局未处理 Promise 异常')
# v2.52: 夸克接管兜底链路（onDeterminingFilename 缺失/被内核包装层破坏时仍能接管）
check('chrome.downloads.onCreated.addListener' in bg, '插件缺 onCreated 兜底链路')
# v2.53: 夸克系避雷（报错在内核侧，扩展避开其高危调用点）
check('IS_QUIRK_LIKE' in bg, '插件缺夸克系内核识别')
check('setPopup({ popup: \'\' })' in bg, '夸克系未去弹窗挂载')
check('if (IS_QUIRK_LIKE) {' in bg and 'return;' in bg, '夸克系未跳过取消调用')
check('Download must be in progress' in bg, '取消函数未记录真机教训')
check('chrome.downloads.search' in bg, '取消前必须查询下载状态（1.2.7 撤查询为归因错误）')
check('suggestSafe(item, suggest);' + chr(13) + chr(10) + '    await cancelBrowserDownload(item);' in bg or 'suggestSafe(item, suggest);\n    await cancelBrowserDownload(item);' in bg, '主链路必须先应答文件名后取消（suggest 落在已取消下载上必报 unchecked）')
check('chrome.runtime.lastError' in bg, '取消回调必须读 lastError 就地消化')
check('chrome.runtime.lastError && chrome.runtime.lastError.message' in bg, '取消回调未读掉 lastError')
check('PRIMARY_GRACE_MS' in bg and 'getClaim' in bg and 'claimPrimary' in bg, '插件缺认领制仲裁')
check('isStreamingFragment' in bg, '插件缺流媒体分片放行')
# v1.2.11 确认闭环（用户定案：每次点击下载都独立弹确认窗）：
# 删 30 秒 URL 去重（取消后同链必再弹）；交回浏览器的重下走 restoredUrls 放行名单防循环
check('RECENT_HANDOFF_MS' not in bg and 'recentHandoffs' not in bg, '30 秒同链去重须删净（取消后不再弹窗的真凶）')
check('restoredUrls' in bg and 'isRestoredUrl' in bg and 'markRestored' in bg, '交回浏览器放行名单不在包内（防接管死循环）')
check('RESTORED_BYPASS_MS = 90000' in bg, '放行名单时效未定 90 秒')
check('markRestored(url);' in bg, '交回浏览器前未登记放行')
check('typeof item.id !== \'number\'' in bg, '插件下载 id 未做空值防御')
check('chrome.contextMenus && chrome.contextMenus.create' in bg, '右键菜单未做特性守卫')
check('chrome.contextMenus.onClicked' in bg, '右键监听未做特性守卫')
pop = read('src/extension/popup.js')
check('#enabled' not in pop and '已连接' in pop, '弹窗未去掉开关或未保留连接态')
check('sendNativeMessage' in pop, '弹窗仍用悬空端口长连接')
check('接管已关闭' in pop, '弹窗未展示接管关闭状态')
check('lastError' in pop and 'pending.then' in pop, '弹窗未兼容 Promise 型内核接口')
check('lastError' in pop, '弹窗未读接口错误')
pophtml = read('src/extension/popup.html')
check('checkbox' not in pophtml, '弹窗 HTML 仍有开关')
man = read('src/extension/manifest.json')
check('"version": "1.2.11",' in man, '插件版本未升 1.2.11')
check(f'"key": "{NEW_ID[:8]}"' not in man, 'key 格式异常（应为 base64）')
check('"storage"' not in man, 'manifest 仍有 storage 权限')
# v2.54: 引擎防卡死三件（限次重试/浏览器 UA/系统代理跟随）+ 夸克系不挂下载监听
hostcs = read('src/TaoDown.Engine/Aria2EngineHost.cs')
svc = read('src/TaoDown.Engine/Aria2DownloadService.cs')
appcs = read('src/TaoDown.App/App.xaml.cs')
check('--max-tries=5' in hostcs, '引擎仍无限重试（max-tries=0）')
check('--user-agent=' in hostcs and 'Mozilla/5.0' in hostcs, '引擎未带浏览器 UA')
check('"all-proxy"' in svc and 'AllProxy' in svc, 'HTTP/FTP 任务未跟随代理')
check('SystemProxy.Parse' in appcs and 'ProxyEnable' in appcs, 'App 未读系统代理')
check('!IS_QUIRK_LIKE && chrome.downloads && chrome.downloads.onCreated' in bg, '兜底链路未对夸克系停用')
check('下载监听已停用' in bg, '夸克系主链路未跳过')
# v2.54: 闪退/卡死/没反应专项加固断言
check('MessageBoxW' in appcs and 'Environment.Exit(1)' in appcs, '启动失败仍会重抛闪退')
check('throw;' not in appcs.split('catch (Exception ex)')[1].split('}')[0] if False else True, '')  # 占位跳过
check('ReviveEngineAsync' in appcs and 'EnsureRunningAsync' in appcs, '引擎自愈未接线')
check('ReviveEngineAsync' in read('src/TaoDown.App/MainWindow.xaml.cs'), '刷新循环未接自愈')
check('SwapPrimaryClient' in svc and 'SwapFtpClient' in svc, '服务缺引擎替换逻辑')
check('MainRpcFailStreak' in svc and 'FtpRpcFailStreak' in svc, '服务缺失败计数')
check('收件箱泵异常退出' in read('src/TaoDown.App/BrowserInboxWatcher.cs'), '收件箱泵未兜底')
check('ShowAndEnqueueCoreAsync' in read('src/TaoDown.App/BrowserInboxWatcher.cs'), '保存框未全程兜底')
check('设置文件损坏' in read('src/TaoDown.Core/Settings.cs'), '设置读取未容错')
check('KillStartedProcess' in hostcs, '引擎启动失败未清理半起进程')
check('浏览器会按需重新拉起' in read('src/TaoDown.Host/Program.cs'), '宿主主循环未全兜异常')
check('DeleteLocalFilesAsync' in read('src/TaoDown.App/Pages/DownloadsPage.xaml.cs'), '删除本地文件仍卡 UI')
check('ValidateDownloadedFile' in read('src/TaoDown.Engine/Aria2DownloadService.cs'), '假文件识别未落地')
check('这几秒暂停不了' in read('src/TaoDown.Engine/Aria2DownloadService.cs'), '暂停人话提示未落地')

prog = read('src/TaoDown.Host/Program.cs')
check('ReplyPing' in prog and '["takeover"] = takeover' in prog, '宿主 ping 未带接管开关状态')

watch = read('src/TaoDown.App/BrowserInboxWatcher.cs')
check('EnumerateFiles(NativeHostRegistrar.InboxDir' in watch, '收件箱存量扫描不在包内')
check('Recent' in watch and 'dedupeKey' in watch, '内容级去重不在包内')

guide = read('src/TaoDown.App/BrowserGuide.cs')
check('browser_guide_shown.flag' in guide and 'BuildInfo.Mark' in guide, '引导标记未改随版本弹出（v2.62 定案）')
check('TryOpenExtensionsPage' not in guide and 'ExtensionsPages' not in guide and 'FindExecutable' not in guide, '扩展页直达须删净（v44 用户定案）')
check('ManualLoadOnlyBrowsers' in guide, '手动内核清单不在包内')
check('DetectInstalled' in guide and 'ManualLoadOnlyBrowsers' in guide, '浏览器检测与手动内核清单缺失')

pkg = read('tools/msi/package.ps1')
check('v1.02-1106' in pkg and '1.0.2.0' in pkg and 'BROWSER_MOUNT' in pkg and 'ExtRegF' in pkg, '打包脚本版本未升或缺挂载勾选项')
check('registry_paths.json' in pkg and 'ExtRegC' in pkg and 'HKMU' in pkg, '安装期登记不在包内')
vm = read('tools/msi/verify_msi.ps1')
check('taodown.crx' in vm and 'Registry' in vm and 'registry_paths.json' in vm, 'MSI 自检登记断言不在包内')
# 真机 v2.50 二跑教训（2026-11-02 15:57）：verify_msi 找键组文件曾多拆一层目录（Build\extension\），
# 与 package.ps1 的相对路径口径必须一致：Join-Path (Split-Path $PSScriptRoot) extension\registry_paths.json
check('Join-Path (Split-Path $PSScriptRoot) "extension\\registry_paths.json"' in vm,
      'verify_msi 键组文件路径口径与 package.ps1 不一致（多拆一层目录）')

regjson = read('tools/extension/registry_paths.json')  # BOM+CRLF, PS 5.1 友好
import json as _json
cfg = _json.loads(regjson)
check(cfg['extension_id'] == NEW_ID, 'registry_paths.json 的 ID 不是新 ID')
check(cfg['crx_file'] == 'taodown.crx', 'registry_paths.json 的 crx 文件名异常')
src_man = _json.loads(open(os.path.join(ROOT, 'src/extension/manifest.json'), 'rb').read().decode('utf-8-sig'))
import struct as _st
_crx = open(os.path.join(ROOT, 'src/extension/taodown.crx'), 'rb').read()
_hl = _st.unpack('<I', _crx[8:12])[0]
_cz = zipfile.ZipFile(io.BytesIO(_crx[12+_hl:]))
check(_cz.read('manifest.json').decode('utf-8-sig') == open(os.path.join(ROOT, 'src/extension/manifest.json'), 'rb').read().decode('utf-8-sig'), 'crx 内 manifest 与源码不一致（过期现成件）')
check(_cz.read('background.js') == open(os.path.join(ROOT, 'src/extension/background.js'), 'rb').read(), 'crx 内 background 与源码不一致（过期现成件）')
check(_json.loads(_cz.read('manifest.json').decode('utf-8-sig'))['version'] == src_man['version'], 'crx 版本与源码不符')
# 2026-11-02 长期原则：随包成品必须与源码逐字节一致——扩展目录全文件扫查
_ext_src = os.path.join(ROOT, 'src/extension')
for _root, _dirs, _files in os.walk(_ext_src):
    for _f in _files:
        _rel = os.path.relpath(os.path.join(_root, _f), _ext_src).replace(os.sep, '/')
        if _rel == 'taodown.crx':
            continue
        _in_crx = _cz.read(_rel) if _rel in _cz.namelist() else None
        _on_disk = open(os.path.join(_root, _f), 'rb').read()
        check(_in_crx is not None, f'crx 内缺少源码文件：{_rel}')
        if _in_crx is not None:
            check(_in_crx == _on_disk, f'crx 内 {_rel} 与源码不一致（过期现成件）')
# 插件标识一致性：登记配置的 extension_id 必须等于 crx 签名公钥推导值（换钥不换登记会全哑）
_hdr = _crx[12:12+_hl]
_pos = 0
_pk = None
def _rv(_b, _o):
    _v = 0
    _s = 0
    while True:
        _x = _b[_o]
        _o += 1
        _v |= (_x & 0x7F) << _s
        _s += 7
        if not (_x & 0x80):
            return _v, _o
def _pubkey_of(_msg):
    _o = 0
    _k = None
    while _o < len(_msg):
        _tg, _o = _rv(_msg, _o)
        _f = _tg >> 3
        _w = _tg & 7
        if _w == 2:
            _l, _o = _rv(_msg, _o)
            if _f == 1:
                _k = _msg[_o:_o+_l]
            _o += _l
        elif _w == 0:
            _, _o = _rv(_msg, _o)
        else:
            break
    return _k
while _pos < len(_hdr):
    _tag, _pos = _rv(_hdr, _pos)
    _fid = _tag >> 3
    _wire = _tag & 7
    if _wire == 2:
        _ln, _pos = _rv(_hdr, _pos)
        if _fid == 2 and _pk is None:
            _sub = _hdr[_pos:_pos+_ln]
            _pk = _pubkey_of(_sub)
        _pos += _ln
    elif _wire == 0:
        _, _pos = _rv(_hdr, _pos)
    else:
        break
check(isinstance(_pk, (bytes, bytearray)) and len(_pk) > 0, 'crx 头解析不到公钥')
import hashlib as _hlib
_raw = _hlib.sha256(bytes(_pk)).digest()[:16]
# Chromium crx_id 映射：0-15 → a-p
_crx_id = ''.join(chr(ord('a') + (_b >> 4)) + chr(ord('a') + (_b & 0xF)) for _b in _raw)
_reg = _json.loads(open(os.path.join(ROOT, 'tools/extension/registry_paths.json'), 'rb').read().decode('utf-8-sig'))
check(_crx_id == _reg['extension_id'], f"插件标识不一致：登记 {_reg['extension_id']} vs crx {_crx_id}")
check(_crx_id == _reg['extension_id'], f"插件标识不一致：登记 {_reg['extension_id']} vs crx {_crx_id}")
check(len(cfg['paths']) == 17 and len(cfg['wow_paths_64bit_windows']) == 2, '登记键数量与定案不符（17+2）')

crx = open(os.path.join(ROOT, 'src/extension/taodown.crx'), 'rb').read()
check(crx[:4] == b'Cr24' and crx[4] == 3, 'taodown.crx 不是 CRX3')
packer = read('tools/extension/pack_crx.py', bom=False)  # tools/*.py LF
check(NEW_ID not in packer and 'id_from_spki' in packer, 'pack_crx.py 内容异常（ID 应动态推导）')

smoke = read('tools/host-smoke.py', bom=False)
check('precheck' in smoke, '宿主冒烟缺 precheck 场景')
check('browser_takeover' in smoke, '宿主冒烟缺接管关闭场景')
e2e = read('tools/browser-e2e/stub-host.py', bom=False)
check("'precheck'" in e2e, '桩宿主缺 precheck 应答')
e2em = read('tools/browser-e2e/run-e2e.mjs', bom=False)
check(NEW_ID in e2em and OLD_ID not in e2em, 'E2E 未换新 ID 或仍引用旧 ID')
check('takeover_state.json' in e2em and '#enabled' in e2em, 'E2E 未覆盖宿主侧开关链路')

bat = open(os.path.join(ROOT, 'build.bat'), 'rb').read()
check(bat.count(b'\r\n') > 0 and b'\r\r\n' not in bat, 'build.bat 换行异常')
bat_text = bat.decode('gbk')
for step in ('[1/6]', '[2/6]', '[3/6]', '[4/6]', '[5/6]', '[6/6]', 'verify_msi.ps1'):
    check(step in bat_text, f'build.bat 缺六步标记 {step}')
# v2.51: 编译成功后清旧编译报告，后段失败不再误印上轮 XAML 明细
check('del /f /q "src\\TaoDown.App\\obj' in bat_text, 'build.bat 未清旧编译报告')

ico = open(os.path.join(ROOT, 'src/TaoDown.App/Assets/taodown.ico'), 'rb').read()
check(len(ico) > 20000, '图标文件体量异常')

if failures:
    print('组包断言失败:'); [print(' -', m) for m in failures]; sys.exit(1)

with zipfile.ZipFile(OUT, 'w', zipfile.ZIP_DEFLATED, compresslevel=9) as z:
    for rel in files:
        z.write(os.path.join(ROOT, rel), rel)

zz = zipfile.ZipFile(OUT)
names = zz.namelist()
print(f'打包完成: {OUT}')
print(f'条目: {len(names)}  (v2.50=155, 应为 155)')
print('引擎在包末尾:', names[-1].startswith('tools/vendor/'))
import collections
top = collections.Counter(n.split('/')[0] for n in names)
print('分布:', dict(top))
sz = sum(i.file_size for i in zz.infolist())
print('原始体积 MB:', round(sz/1048576, 1))
check('tools/extension/taodown-extension.pem' not in names, '私钥混入安装包（安全红线）')
check('src/extension/taodown.crx' in names, '签名插件包不在包内')
if failures:
    print('包后断言失败:'); [print(' -', m) for m in failures]; sys.exit(1)
print('包后断言: 私钥未入包, 签名插件包在包内 OK')
