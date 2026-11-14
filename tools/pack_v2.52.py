#!/usr/bin/env python3
# -*- coding: utf-8 -*-
# 组装 TaoDown v2.52 完整交付包（沿用 v2.51 清单口径 + 会话恢复第一刀）。
# 来源树：/root/workspace/taodown-work（2d85b51，云 CI 43351856523 全绿对应提交）。
# MSI：取自云 CI 安装链产物（同提交构建）。
# 私钥 tools/extension/taodown-extension.pem 绝不入包（安全边界）。
import zipfile, os, sys, hashlib

WORK = '/root/workspace/taodown-work'
MSI = '/root/workspace/.tmp/taodown-dl/multiview-evidence/out/TaoDown-2.4.0.0-x64.msi'
OUT = '/root/workspace/.tmp/TaoDown-v2.52-完整交付包.zip'
NEW_ID = 'dchnmdakoleaofgpkhddhmohcoomlimp'
OLD_ID = 'mhbhaogipooncclfkmcdfhldfbpjakgi'
EXCLUDE_TOP = {'notes', 'out', 'progress_log', 'goal.md', 'goal.html', 'icon_preview.png',
               '.memory', '.git', '.github', 'tools/pack_v2.51.py', 'D:\\TaoDownData\\TaoDown',
               'docs/ref-OpenList-Integration.md',
               'tools/vendor/openlist/openlist.exe', 'tools/vendor/LICENSE-openlist.txt',
               'tools/extension/taodown-extension.pem',
               'tools/pack_v2.49.py', 'tools/pack_v2.50.py', 'tools/pack_v2.52.py'}

def walk(rel_dir):
    full = os.path.join(WORK, rel_dir)
    for name in sorted(os.listdir(full)):
        rel = (rel_dir + '/' + name).lstrip('/')
        if rel in EXCLUDE_TOP or rel.startswith('D:') or rel.endswith('.zip') or '__pycache__' in rel:
            continue
        p = os.path.join(WORK, rel)
        if os.path.isdir(p):
            if name in ('bin', 'obj') or name.startswith('.'):
                continue
            if rel.startswith('tools/UiSyntaxCheck/obj') or rel.startswith('tools/UiSyntaxCheck/bin/'):
                continue
            yield from walk(rel)
        else:
            if '/obj/' in rel or '/bin/' in rel and '/bin-check/' not in rel:
                continue
            yield rel

files = [n for n in walk('') if not n.startswith(('out/', 'notes/', 'progress_log/', 'goal'))]
files = [n for n in files if not n.endswith('.zip')]
files = [n for n in files if '/' in n or n in ('README.md', 'THIRD-PARTY-NOTICES.txt', 'build.bat')]
engines = [n for n in files if n.startswith('tools/vendor/') and n.endswith('.exe')]
files = [n for n in files if n not in engines] + engines

failures = []
def check(cond, msg):
    if not cond: failures.append(msg)
def read(rel, bom=True):
    raw = open(os.path.join(WORK, rel), 'rb').read()
    if bom: check(raw.startswith(b'\xef\xbb\xbf'), f'{rel} 缺 BOM')
    return raw.decode('utf-8-sig')

# —— 组包前断言 ——
reg = read('src/TaoDown.App/NativeHostRegistrar.cs')
check(f'ExtensionId = "{NEW_ID}"' in reg, f'宿主未换新 ID {NEW_ID}')
check(f'LegacyExtensionId = "{OLD_ID}"' in reg, '旧 ID 兼容清理不在包内')
check('WriteTakeoverState' in reg and 'takeover_state.json' in reg, '接管状态落盘不在包内')
check("CleanupLegacyExtensionEntries" in reg, '旧 ID 登记清理不在包内')
check('taodown.crx' in reg, '登记 path 未指向签名插件包')

# v2.52: 任务列表会话恢复第一刀（2d85b51）
store = 'src/TaoDown.Engine/DownloadTaskStore.cs'
if store:
    sc = read(store)
    check('tasks.json' in sc, '任务快照落盘文件不在包内')
svc = read('src/TaoDown.Engine/Aria2DownloadService.cs')
check('RestorePendingAsync' in svc, '恢复入口不在包内')
app = read('src/TaoDown.App/App.xaml.cs')
check('RestorePendingAsync' in app or '恢复上次会话' in app, 'App 启动恢复接线不在包内')
# 版本号 2.4.0
cs = read('src/TaoDown.App/TaoDown.App.csproj')
check('<Version>2.4.0</Version>' in cs, '版本号不是 2.4.0')

if failures:
    print('组包断言失败：')
    for f in failures: print(' -', f)
    sys.exit(1)

if not os.path.exists(MSI):
    print(f'MSI 不存在: {MSI}'); sys.exit(1)

with zipfile.ZipFile(OUT, 'w', zipfile.ZIP_DEFLATED) as z:
    for n in files:
        z.write(os.path.join(WORK, n), 'TaoDown-v2.52/' + n)
    z.write(MSI, 'TaoDown-v2.52/TaoDown-2.4.0.0-x64.msi')

h = hashlib.md5(open(OUT, 'rb').read()).hexdigest()
m = hashlib.md5(open(MSI, 'rb').read()).hexdigest()
print(f'包内 {len(files) + 1} 项（源码 {len(files)} + MSI 1）')
print(f'zip md5: {h}')
print(f'msi md5: {m}')
print(OUT)
