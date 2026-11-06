// TaoDown 内容脚本：在页面上下文里抓取 blob: 链接的真实内容。
// blob 链接只在生成它的页面里有效，必须由页面本身读取后分块发回后台。
// 通道：chrome.runtime.onConnect 端口（多条分块消息，避开 onMessage 单次应答限制）。
'use strict';

const CHUNK_RAW = 512 * 1024; // 与宿主 BlobPayload.ChunkRawBytes 一致

chrome.runtime.onConnect.addListener((port) => {
  if (port.name !== 'taodown-blob') {
    return;
  }

  port.onMessage.addListener((msg) => {
    if (!msg || msg.type !== 'fetch' || !msg.url) {
      return;
    }

    (async () => {
      try {
        const resp = await fetch(msg.url);
        if (!resp.ok) {
          port.postMessage({ type: 'error', error: 'HTTP ' + resp.status });
          return;
        }

        const blob = await resp.blob();
        if (blob.size > 4 * 1024 * 1024 * 1024) {
          port.postMessage({ type: 'error', error: '文件超过 4GB 上限' });
          return;
        }

        // 流式读取 + 512KB 定长分块（与宿主 BlobPayload.ChunkRawBytes 一致）：
        // 每条 chunk 消息解码后不超过 512KB、base64 不超过宿主 1.4M 字符门槛；
        // 读取器给出的块大小不定，跨读取携带余数，凑满定长才发一条。
        const reader = blob.stream().getReader();
        let index = 0;
        let carry = new Uint8Array(0);
        const emit = (bytes) => {
          // 分段 btoa 防止 String.fromCharCode 爆栈；分段步长必须是 3 的倍数，
          // 且只有最后一段允许带填充——否则拼出的 base64 含内嵌 '='，
          // 宿主解码到第一处填充就会截断（v2.48 E2E 实测 >32KB 必坏）。
          let b64 = '';
          const n = bytes.length;
          const seg = 0x8000 - (0x8000 % 3); // 32766
          let i = 0;
          for (; i + seg <= n; i += seg) {
            b64 += btoa(String.fromCharCode.apply(null, bytes.subarray(i, i + seg)));
          }
          if (i < n) {
            b64 += btoa(String.fromCharCode.apply(null, bytes.subarray(i)));
          }
          port.postMessage({ type: 'chunk', index: index++, data: b64 });
        };
        for (;;) {
          const { done, value } = await reader.read();
          if (done) {
            break;
          }
          if (!value || value.length === 0) {
            continue;
          }

          let merged;
          if (carry.length > 0) {
            merged = new Uint8Array(carry.length + value.length);
            merged.set(carry, 0);
            merged.set(value, carry.length);
          } else {
            merged = value;
          }
          let off = 0;
          while (merged.length - off >= CHUNK_RAW) {
            emit(merged.subarray(off, off + CHUNK_RAW));
            off += CHUNK_RAW;
          }
          carry = merged.slice(off);
        }
        if (carry.length > 0) {
          emit(carry);
        }

        port.postMessage({ type: 'done', total: index });
      } catch (e) {
        port.postMessage({ type: 'error', error: String((e && e.message) || e) });
      }
    })();
  });
});
