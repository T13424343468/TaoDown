#!/usr/bin/env python3
# TaoDown.Host 宿主帧协议冒烟（v2.49 增设，回应「插件弹窗恒显响应异常」修复的断言入全测）。
# 对真实构建出的 TaoDown.Host 二进制按 Native Messaging 帧协议喂三条消息并断言应答：
#   1. {"type":"ping"}                -> {"ok":true,"detail":"pong"}   （弹窗连通性探测）
#   2. 合法下载请求                    -> {"ok":true,...}               （写收件箱）
#   3. 缺地址请求 {"url":"x"}          -> {"ok":false,"detail":"下载地址无效"}
#   4. {"type":"precheck"} 无状态文件   -> {"ok":true,"takeover":true}    （默认接管）
#   5. 状态文件 browser_takeover=false  -> {"ok":true,"takeover":false}   （v2.50 接管开关）
# 此前三条里第 1 条落入 default 被当下载请求解析失败，弹窗恒显“TaoDown 响应异常”。
# 用法：python3 tools/host-smoke.py <TaoDown.Host.dll 路径>
import json
import os
import struct
import subprocess
import sys
import tempfile
import urllib.parse

def main():
    dll = sys.argv[1] if len(sys.argv) > 1 else 'src/TaoDown.Host/bin/Debug/net8.0/TaoDown.Host.dll'
    if not os.path.exists(dll):
        print('FAIL  找不到宿主二进制: %s' % dll)
        return 1
    data_dir = tempfile.mkdtemp(prefix='taodown-smoke-')
    env = {**os.environ, 'TAODOWN_DATA': data_dir, 'DOTNET_ROOT': '/root/workspace/.local/dotnet'}
    env['PATH'] = '/root/workspace/.local/bin:' + env.get('PATH', '')
    p = subprocess.Popen(['dotnet', dll], stdin=subprocess.PIPE,
                         stdout=subprocess.PIPE, env=env)

    def exchange(payload):
        p.stdin.write(struct.pack('<I', len(payload)))
        p.stdin.write(payload)
        p.stdin.flush()
        n = struct.unpack('<I', p.stdout.read(4))[0]
        return json.loads(p.stdout.read(n).decode('utf-8'))

    failures = 0

    r1 = exchange(b'{"type":"ping"}')
    ok1 = r1.get('ok') is True and r1.get('detail') == 'pong'
    print(('PASS' if ok1 else 'FAIL') + '  ping -> ' + json.dumps(r1, ensure_ascii=False))
    failures += 0 if ok1 else 1

    r2 = exchange(json.dumps({'type': 'download', 'url': 'https://example.com/smoke.zip',
                              'filename': 'smoke.zip'}).encode('utf-8'))
    ok2 = r2.get('ok') is True and len(r2.get('detail') or '') > 0
    print(('PASS' if ok2 else 'FAIL') + '  合法下载 -> ok=' + str(r2.get('ok')))
    failures += 0 if ok2 else 1

    r3 = exchange(b'{"url":"x"}')
    ok3 = r3.get('ok') is False and r3.get('detail') == '下载地址无效'
    print(('PASS' if ok3 else 'FAIL') + '  坏地址 -> ' + json.dumps(r3, ensure_ascii=False))
    failures += 0 if ok3 else 1

    # v2.50: precheck 应答 takeover 状态（无状态文件默认接管=true）
    # v2.51: 场景可重复——先清掉上一轮冒烟遗留的状态文件（冒烟脚本第 5 场景写 false 后未清，
    # 复跑时第 4 场景会误见旧状态文件而失败），再断言「无状态文件」。
    state_path = os.path.join(os.getcwd(), 'D:\\TaoDownData\\TaoDown', 'native_host', 'takeover_state.json')
    try:
        os.remove(state_path)
    except OSError:
        pass
    r4 = exchange(b'{"type":"precheck"}')
    ok4 = r4.get('ok') is True and r4.get('takeover') is True
    print(('PASS' if ok4 else 'FAIL') + '  precheck(无状态文件) -> ' + json.dumps(r4, ensure_ascii=False))
    failures += 0 if ok4 else 1

    # DataRoot 首选根不读环境变量：Linux 沙箱里 "D:\\TaoDownData\\TaoDown" 是相对 CWD 的字面目录,
    # 与第 2 步宿主写收件箱的落点一致；Windows 真机则是 D 盘真实路径，同一套代码。
    os.makedirs(os.path.dirname(state_path), exist_ok=True)
    with open(state_path, 'w', encoding='utf-8') as sf:
        sf.write(json.dumps({'browser_takeover': False, 'updated': 'smoke'}))
    r5 = exchange(b'{"type":"precheck"}')
    ok5 = r5.get('ok') is True and r5.get('takeover') is False
    print(('PASS' if ok5 else 'FAIL') + '  precheck(关闭接管) -> ' + json.dumps(r5, ensure_ascii=False))
    failures += 0 if ok5 else 1

    p.stdin.close()
    try:
        p.wait(timeout=10)
    except subprocess.TimeoutExpired:
        p.kill()
        failures += 1
    try:
        os.remove(state_path)
    except OSError:
        pass
    print('\n宿主冒烟结论: %d/5 通过' % (5 - failures))
    return 1 if failures else 0

if __name__ == '__main__':
    sys.exit(main())
