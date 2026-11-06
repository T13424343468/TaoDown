#!/usr/bin/env python3
# 组装 TaoDown v2.50 完整交付包（一次性脚本，沿用 v2.49 清单 + v2.50 新增）。
# 清单口径：v2.49 包 152 项基础上 + src/extension/taodown.crx（签名插件包）
#           + tools/extension/registry_paths.json + tools/extension/pack_crx.py = 155 项。
# 私钥 tools/extension/taodown-extension.pem 绝不入包（安全边界）。
# 断言：源码 BOM、bat GBK+CRLF 六步标记、版本号 2.50/v29-1102/插件 1.2.0、
#       零操作改造五件都在包内（precheck 协议/收件箱存量扫描/内容去重/机器级登记/新 ID 清旧）。
import zipfile, os, sys

ROOT = '/root/workspace/projects/201259'
OUT = os.path.join(ROOT, 'TaoDown-v2.50-完整交付包.zip')
NEW_ID = 'dchnmdakoleaofgpkhddhmohcoomlimp'
OLD_ID = 'mhbhaogipooncclfkmcdfhldfbpjakgi'
EXCLUDE_TOP = {'notes', 'out', 'progress_log', 'goal.md', 'goal.html', 'icon_preview.png',
               '.memory', 'tools/pack_v2.50.py', 'D:\\TaoDownData\\TaoDown',
               'docs/ref-OpenList-Integration.md',  # 过时网盘参考（v2.46 起排除）
               'tools/vendor/openlist/openlist.exe', 'tools/vendor/LICENSE-openlist.txt',
               'tools/extension/taodown-extension.pem',  # 私钥绝不出仓（v2.50 安全边界）
               'tools/pack_v2.49.py',  # 旧版组包脚本不随包（一次性脚本随迭代更替）
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

bg = read('src/extension/background.js')
check("'precheck'" in bg and 'reply.takeover !== false' in bg, '插件 precheck 询问不在包内')
check('chrome.storage' not in bg, '插件仍在用 storage（应已移除）')
pop = read('src/extension/popup.js')
check('#enabled' not in pop and '已连接' in pop, '弹窗未去掉开关或未保留连接态')
pophtml = read('src/extension/popup.html')
check('checkbox' not in pophtml, '弹窗 HTML 仍有开关')
man = read('src/extension/manifest.json')
check('"version": "1.2.0",' in man, '插件版本未升 1.2.0')
check(f'"key": "{NEW_ID[:8]}"' not in man, 'key 格式异常（应为 base64）')
check('"storage"' not in man, 'manifest 仍有 storage 权限')

watch = read('src/TaoDown.App/BrowserInboxWatcher.cs')
check('EnumerateFiles(NativeHostRegistrar.InboxDir' in watch, '收件箱存量扫描不在包内')
check('Recent' in watch and 'dedupeKey' in watch, '内容级去重不在包内')

guide = read('src/TaoDown.App/BrowserGuide.cs')
check('browser_guide_shown_v2.flag' in guide, '引导标记未升版')
check('ManualLoadOnlyBrowsers' in guide, '手动内核清单不在包内')
mw = read('src/TaoDown.App/MainWindow.xaml.cs')
check('"浏览器下载接管已就绪"' in mw and 'TryOpenExtensionsPage(browsers)' in mw, '引导弹窗按钮未改')
# 真机 v2.50 首跑教训（2026-11-02）：ContentDialogResult 只有 None/Primary/Secondary，
# 关闭按钮返回 None——沙箱不编译 App 工程，必须靠组包断言拦住这类 API 误用。
check('ContentDialogResult.None' in mw and 'ContentDialogResult.Close' not in mw,
      '对话框枚举误用（Close 不存在，关闭按钮应判 None）')

pkg = read('tools/msi/package.ps1')
check('v29-1102' in pkg and '2.50.0.0' in pkg, '打包脚本版本未升')
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
print(f'条目: {len(names)}  (v2.49=152, 应为 155)')
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
