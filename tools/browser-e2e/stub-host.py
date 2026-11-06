#!/usr/bin/env python3
# TaoDown 本机宿主测试桩（真实浏览器 E2E 用）。
# 按 Native Messaging 协议收发：4 字节小端长度 + UTF-8 JSON。
# 收到的每条消息逐条追加到 $TAODOWN_E2E_LOG/host-messages.jsonl；
# blob/data 分块流在 download-data-end 时重组落盘到 $TAODOWN_E2E_LOG/blobs/，
# 供测试断言重组字节与源一致。应答语义镜像真实宿主（v2.49）：ping 回 pong，
# 坏下载地址回 ok:false，precheck 读 $TAODOWN_E2E_LOG/takeover_state.json（镜像真实宿主 v2.50）。
import base64
import json
import os
import struct
import sys
import time


def reply(obj):
    buf = json.dumps(obj).encode('utf-8')
    sys.stdout.buffer.write(struct.pack('<I', len(buf)))
    sys.stdout.buffer.write(buf)
    sys.stdout.buffer.flush()


def main():
    log_dir = os.environ.get('TAODOWN_E2E_LOG')
    if not log_dir:
        print('TAODOWN_E2E_LOG 未设置', file=sys.stderr)
        return 1
    os.makedirs(log_dir, exist_ok=True)
    blobs = os.path.join(log_dir, 'blobs')
    os.makedirs(blobs, exist_ok=True)
    log = os.path.join(log_dir, 'host-messages.jsonl')
    sessions = {}

    def read_exact(stream, k):
        buf = b''
        while len(buf) < k:
            chunk = stream.read(k - len(buf))
            if not chunk:
                with open(log + '.raw', 'a') as rf:
                    rf.write('pid=%s EOF after %d/%d bytes\n' % (os.getpid(), len(buf), k))
                return None
            buf += chunk
        return buf

    while True:
        raw = read_exact(sys.stdin.buffer, 4)
        if raw is None or len(raw) < 4:
            break
        (n,) = struct.unpack('<I', raw)
        if n == 0:
            break
        body = read_exact(sys.stdin.buffer, n)
        if body is None:
            break
        try:
            msg = json.loads(body.decode('utf-8'))
        except Exception as e:
            print('坏消息: %s' % e, file=sys.stderr)
            break
        with open(log, 'a', encoding='utf-8') as f:
            f.write(json.dumps({'_pid': os.getpid(), '_t': round(time.time(), 3), **msg}, ensure_ascii=False) + '\n')
        t = msg.get('type')
        # v2.49: 桩宿主镜像真实宿主（TaoDown.Host）的应答语义，不再对任何消息一律回 ok：
        # ping 只由真实宿主新增的 case 响应（沙箱已对真实二进制冒烟断言），
        # 桩内显式回 pong 保持弹窗断言语义一致；坏下载请求按真实宿主回 ok:false。
        if t == 'ping':
            reply({'ok': True, 'detail': 'pong'})
            continue
        if t == 'precheck':
            # v2.50: 镜像真实宿主——读状态文件, 缺失/损坏按接管开启
            enabled = True
            try:
                state_path = os.path.join(log_dir, 'takeover_state.json')
                with open(state_path, encoding='utf-8') as sf:
                    enabled = json.load(sf).get('browser_takeover') is not False
            except Exception:
                enabled = True
            reply({'ok': True, 'takeover': enabled})
            continue
        if t == 'download-data-start':
            sessions[msg.get('id')] = []
        elif t == 'download-data-chunk':
            sessions.setdefault(msg.get('id'), []).append(
                (msg.get('index'), msg.get('data') or ''))
        elif t == 'download-data-end':
            chunks = sessions.pop(msg.get('id'), [])
            chunks.sort(key=lambda c: c[0])
            data = b''.join(base64.b64decode(d) for _, d in chunks)
            name = msg.get('filename') or 'blob.bin'
            # 文件名只保留基本字符，避免路径注入（测试桩自我保护）
            safe = ''.join(c for c in name if c.isalnum() or c in '._-') or 'blob.bin'
            out = os.path.join(blobs, '%d_%s' % (time.time_ns(), safe))
            with open(out, 'wb') as g:
                g.write(data)
        elif t == 'download':
            if not (msg.get('url') or '').startswith(('http://', 'https://', 'ftp://',
                                                      'magnet:', 'ed2k:', 'thunder:')):
                reply({'ok': False, 'detail': '下载地址无效'})
                continue
        reply({'ok': True})
    with open(log, 'a', encoding='utf-8') as f:
        f.write(json.dumps({'_pid': os.getpid(), '_t': round(time.time(), 3), '_eof': True}) + '\n')
    return 0


if __name__ == '__main__':
    sys.exit(main())
