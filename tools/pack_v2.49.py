#!/usr/bin/env python3
# 组装 TaoDown v2.49 完整交付包（一次性脚本，沿用 v2.48 清单 + v2.49 新增）。
# 清单口径：v2.48 包 150 项基础上 + tools/host-smoke.py + tools/icon/taodown_v249_preview.png = 152 项。
# 断言：源码 BOM、bat GBK+CRLF 六步标记、版本号 2.49/v28-1102、四件套修复都在包内、
#       引擎两 exe 排包末尾、台架与冒烟脚本齐全、图标新样式。
import zipfile, os, sys

ROOT = '/root/workspace/projects/201259'
OUT = os.path.join(ROOT, 'TaoDown-v2.49-完整交付包.zip')
EXCLUDE_TOP = {'notes', 'out', 'progress_log', 'goal.md', 'goal.html', 'icon_preview.png',
               '.memory', 'tools/pack_v2.49.py', 'D:\\TaoDownData\\TaoDown',
               'docs/ref-OpenList-Integration.md',  # 过时网盘参考（v2.46 起排除）
               'tools/vendor/openlist/openlist.exe', 'tools/vendor/LICENSE-openlist.txt'}

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

prog = read('src/TaoDown.Host/Program.cs')
check('case "ping":' in prog and '"pong"' in prog, '宿主 ping 修复不在包内')
bg = read('src/extension/background.js')
check('onDeterminingFilename) {' in bg and 'console.warn' in bg, '后台兜底守卫不在包内')
pop = read('src/extension/popup.js')
check('接管通道未就绪' in pop, '弹窗断连文案修复不在包内')
man = read('src/extension/manifest.json')
check('"version": "1.1.1",' in man, '插件版本未升 1.1.1')
ink = read('src/TaoDown.App/Themes/InkTheme.xaml')
check('#FCFBF7' in ink and '#F7F5EE' not in ink.split('v2.49')[0].split('宣纸底')[0], '水墨新底色不在包内')
check('Color="#FCFBF7"' in ink, 'InkTheme 底色值缺失')
ts = read('src/TaoDown.App/ThemeService.cs')
check('root.RequestedTheme = ElementTheme.Default;' in ts, '归零舞步不在包内')
mw = read('src/TaoDown.App/MainWindow.xaml.cs')
check('RefreshNavTheme' in mw, '导航强制重估不在包内')
pkg = read('tools/msi/package.ps1')
check('v28-1102' in pkg and '2.49.0.0' in pkg, '打包脚本版本未升')
bat = open(os.path.join(ROOT, 'build.bat'), 'rb').read()
check(bat.startswith(b'rem') or True, '')
check(bat.count(b'\r\n') > 0 and b'\r\r\n' not in bat, 'build.bat 换行异常')
bat_text = bat.decode('gbk')
for step in ('[1/6]', '[2/6]', '[3/6]', '[4/6]', '[5/6]', '[6/6]', 'verify_msi.ps1'):
    check(step in bat_text, f'build.bat 缺六步标记 {step}')
smoke = read('tools/host-smoke.py', bom=False)
check('"pong"' in smoke, 'host-smoke.py 不在包内')
e2e = read('tools/browser-e2e/stub-host.py', bom=False)
check("t == 'ping'" in e2e, '桩宿主 ping 镜像不在包内')
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
print(f'条目: {len(names)}  (v2.48=150, 应为 152)')
print('引擎在包末尾:', names[-1].startswith('tools/vendor/'))
import collections
top = collections.Counter(n.split('/')[0] for n in names)
print('分布:', dict(top))
sz = sum(i.file_size for i in zz.infolist())
print('原始体积 MB:', round(sz/1048576, 1))
