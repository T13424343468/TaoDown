// TaoDown 浏览器接管插件真实浏览器 E2E（v2.48 增设，回应「修完用真实浏览器模拟测试」要求）。
// 台架：沙箱内真实 Chromium + Playwright 持久上下文加载未打包插件 + Native Messaging 测试桩。
// 断言六件事：
//   1. 插件在真实浏览器里加载成功，扩展 ID 与注册表登记一致（dchnmdakoleaofgpkhddhmohcoomlimp）；
//   2. HTTP(S) 直链下载：点下载 → 宿主收到 {type:"download", url, filename}，浏览器自带下载被取消；
//   3. blob: 下载（1.5MB，跨 3 个 512KB 分块 + 余数）：页面内分块抓取 →
//      download-data-start/chunk/end 全链路，宿主重组字节与源逐字节一致；
//   4. 小 blob（777 字节，单分块余数路径）：同样逐字节一致；
//   5. data: 链接（512KB+321 字节，跨 2 个 512KB 分块）：后台解码分块，重组逐字节一致；
//   6. 接管开关（v2.50 唯一真相在宿主侧状态文件）：precheck 应答 takeover:false 时
//      直链放行给浏览器（宿主不收到请求），恢复 true 后接管；状态文件缺失按接管；
//      弹窗 ping 显示「已连接」，且弹窗不再有开关（唯一真相在 TaoDown 设置页）。
// 校验模式用 32 位乘法散列（Math.imul），字节随位置变化，能察觉错序/重复/截断；
// 旧版 (i*7+13)&0xff 周期恰好 256 字节，无法发现错序，已弃用。
// 说明：Windows 注册表自动挂载属于 Windows 机制，Linux 沙箱无法复现，真机验证步骤见 README。
// 运行：node run-e2e.mjs（需 node + playwright + Chromium 二进制；变量见下方默认值）。
import { spawn } from 'node:child_process';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import http from 'node:http';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';

const here = path.dirname(fileURLToPath(import.meta.url));
const extId = 'dchnmdakoleaofgpkhddhmohcoomlimp';
const extDir = path.resolve(here, '../../src/extension');

function loadPlaywright() {
  const tries = [
    process.env.PLAYWRIGHT_MODULE,
    '/root/.npm-global/lib/node_modules/playwright',
  ].filter(Boolean);
  for (const dir of tries) {
    try {
      return createRequire(path.join(dir, 'no-op.js'))('playwright');
    } catch (e) {
      // 换下一个候选
    }
  }
  throw new Error('找不到 playwright，可用 PLAYWRIGHT_MODULE 指定其所在目录');
}

// —— 位置敏感校验模式：byte(i) = (i * 0x9E3779B1) 的高 8 位 ——
// 页面内生成与台架期望必须同式；任何错序/重复/截断都会逐字节比对失败。
const byteFn = (i) => ((Math.imul(i, 0x9E3779B1) >>> 24) & 0xff);

// —— 期望字节（与页面内生成口径一致，用于逐字节比对）——
const blobSize = 1.5 * 1024 * 1024 + 777; // 3 个 512KB 分块 + 余数
const smallSize = 777; // 单分块余数路径
const dataSize = 512 * 1024 + 321; // 跨 2 个分块
function expectedBytes(size) {
  const arr = Buffer.alloc(size);
  for (let i = 0; i < size; i++) {
    arr[i] = byteFn(i);
  }
  return arr;
}

const results = [];
function report(name, ok, detail) {
  results.push({ name, ok, detail });
  console.log(`${ok ? 'PASS' : 'FAIL'}  ${name}${detail ? '  (' + detail + ')' : ''}`);
}

// —— 等待桩宿主日志出现匹配消息 ——
function pollLog(logPath, predicate, timeoutMs, label) {
  const t0 = Date.now();
  return new Promise((resolve, reject) => {
    const tick = () => {
      try {
        const text = fs.readFileSync(logPath, 'utf-8');
        const lines = text.split('\n').filter(Boolean);
        const hit = lines.map((l) => {
          try { return JSON.parse(l); } catch (e) { return null; }
        }).find((m) => m && predicate(m));
        if (hit) {
          resolve(hit);
          return;
        }
      } catch (e) {
        // 文件还没写出来，继续等
      }
      if (Date.now() - t0 > timeoutMs) {
        reject(new Error(`等待超时: ${label}`));
        return;
      }
      setTimeout(tick, 250);
    };
    tick();
  });
}

// —— 统计当前满足条件的消息条数（用于「不出现」断言）——
function countLog(logPath, predicate) {
  try {
    const text = fs.readFileSync(logPath, 'utf-8');
    return text.split('\n').filter(Boolean).map((l) => {
      try { return JSON.parse(l); } catch (e) { return null; }
    }).filter((m) => m && predicate(m)).length;
  } catch (e) {
    return 0;
  }
}

// —— 从日志提取某会话的全部分块并重组 ——
function reassembleFromLog(logPath, sessionId) {
  const chunks = [];
  const text = fs.readFileSync(logPath, 'utf-8');
  for (const line of text.split('\n').filter(Boolean)) {
    let m;
    try { m = JSON.parse(line); } catch (e) { continue; }
    if (m.type === 'download-data-chunk' && m.id === sessionId) {
      chunks[m.index] = m.data;
    }
  }
  const contiguous = chunks.length > 0 && chunks.every((c) => !!c);
  const reassembled = Buffer.concat(chunks.map((c) => Buffer.from(c, 'base64')));
  return { chunks, contiguous, reassembled };
}

async function main() {
  if (!fs.existsSync(path.join(extDir, 'manifest.json'))) {
    throw new Error('扩展源目录不存在: ' + extDir);
  }
  const work = fs.mkdtempSync(path.join(os.tmpdir(), 'taodown-e2e-'));
  const logDir = path.join(work, 'stub-log');
  fs.mkdirSync(logDir, { recursive: true });
  fs.mkdirSync(path.join(logDir, 'blobs'), { recursive: true });
  const logPath = path.join(logDir, 'host-messages.jsonl');

  // 1) 测试页 + 三类下载链接 + 弹窗开关用的第二条直链
  const port = 18234;
  const fileBody = expectedBytes(1024 * 64);
  const dataUrl =
    'data:application/octet-stream;base64,' +
    expectedBytes(dataSize).toString('base64');
  const pageHtml = `<!doctype html>
<meta charset="utf-8"><title>TaoDown E2E</title>
<p><a id="dl-http" href="/file.bin">HTTP 直链下载</a></p>
<p><a id="dl-http2" href="/file2.bin">第二条直链（开关测试）</a></p>
<p><a id="dl-blob" href="#" download="e2e-blob.bin">blob 下载</a></p>
<p><a id="dl-blob-small" href="#" download="e2e-blob-small.bin">小 blob 下载</a></p>
<p><a id="dl-data" href="${dataUrl}" download="e2e-data.bin">data 下载</a></p>
<script>
function makeBlobLink(id, n, name) {
  document.getElementById(id).addEventListener('click', () => {
    const arr = new Uint8Array(n);
    for (let i = 0; i < n; i++) arr[i] = (Math.imul(i, 0x9E3779B1) >>> 24) & 0xff;
    document.getElementById(id).href = URL.createObjectURL(new Blob([arr]));
  });
}
makeBlobLink('dl-blob', ${blobSize});
makeBlobLink('dl-blob-small', ${smallSize});
</script>`;
  const server = http.createServer((req, res) => {
    if (req.url === '/page.html') {
      res.writeHead(200, { 'Content-Type': 'text/html; charset=utf-8' });
      res.end(pageHtml);
    } else if (req.url === '/file.bin' || req.url === '/file2.bin') {
      res.writeHead(200, {
        'Content-Type': 'application/octet-stream',
        'Content-Disposition': `attachment; filename="${req.url === '/file.bin' ? 'e2e-http.bin' : 'e2e-http2.bin'}"`,
        'Content-Length': fileBody.length,
      });
      res.end(fileBody);
    } else {
      res.writeHead(404);
      res.end();
    }
  });
  await new Promise((r) => server.listen(port, '127.0.0.1', r));

  // 2) 桩宿主 + Native Messaging 登记
  // 注意：Chromium 在 Linux 上从「用户数据目录\NativeMessagingHosts」读清单，
  // 自定义资料目录时 ~/.config 下的全局清单不生效，两处都写。
  const hostScript = path.join(work, 'stub-host.py');
  fs.copyFileSync(path.join(here, 'stub-host.py'), hostScript);
  const wrapper = path.join(work, 'stub-host.sh');
  fs.writeFileSync(
    wrapper,
    `#!/bin/sh\nexport TAODOWN_E2E_LOG='${logDir}'\nexec python3 '${hostScript}'\n`,
    { mode: 0o755 },
  );
  fs.chmodSync(hostScript, 0o755);
  const manifest = {
    name: 'com.tao.taodown',
    description: 'TaoDown E2E stub host',
    path: wrapper,
    type: 'stdio',
    allowed_origins: [`chrome-extension://${extId}/`],
  };
  const manifestDirs = [
    path.join(work, 'profile', 'NativeMessagingHosts'),
    path.join(os.homedir(), '.config', 'chromium', 'NativeMessagingHosts'),
    path.join(os.homedir(), '.config', 'google-chrome', 'NativeMessagingHosts'),
  ];
  for (const dir of manifestDirs) {
    fs.mkdirSync(dir, { recursive: true });
    fs.writeFileSync(path.join(dir, 'com.tao.taodown.json'), JSON.stringify(manifest));
  }

  // 3) 启动真实 Chromium（加载未打包插件）
  const { chromium } = loadPlaywright();
  const browserBin = process.env.CHROMIUM_BIN || '/usr/local/bin/chromium';
  const context = await chromium.launchPersistentContext(path.join(work, 'profile'), {
    executablePath: browserBin,
    headless: true,
    args: [
      `--disable-extensions-except=${extDir}`,
      `--load-extension=${extDir}`,
      '--no-first-run',
      '--no-default-browser-check',
      '--disable-dev-shm-usage',
    ],
  });

  // 断言 1：扩展加载且 ID 与登记一致
  const sw = await new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(new Error('后台服务线程未在 30 秒内启动')), 30000);
    const found = context.serviceWorkers().find((w) => w.url().includes(extId));
    if (found) {
      clearTimeout(timer);
      resolve(found);
      return;
    }
    context.on('serviceworker', (w) => {
      if (w.url().includes(extId)) {
        clearTimeout(timer);
        resolve(w);
      }
    });
  });
  const actualId = sw.url().replace(/^chrome-extension:\/\//, '').replace(/\/.*/, '');
  report('插件加载 + ID 一致', actualId === extId, `实际 ID=${actualId}`);

  const page = await context.newPage();
  // 关键：Playwright 会用 CDP 的 Browser.setDownloadBehavior 把下载改写进自己的
  // 收件流程，导致 downloads.onDeterminingFilename 不派发（v2.48 沙箱实测根因，
  // 页面级会话还原无效）。必须在「浏览器级」CDP 会话上把下载行为还原为默认，
  // 接管事件才会像真机一样触发，插件随后取消浏览器下载并转发 TaoDown。
  const browserCdp = await context.browser().newBrowserCDPSession();
  await browserCdp.send('Browser.setDownloadBehavior', { behavior: 'default', eventsEnabled: false });
  await page.goto(`http://127.0.0.1:${port}/page.html`);

  try {
    // 断言 2：HTTP 直链接管
    await page.click('#dl-http');
    const got = await pollLog(logPath,
      (m) => m.type === 'download' && (m.url || '').endsWith('/file.bin'), 30000, 'HTTP 直链接管');
    const okA = (got.filename || '').includes('e2e-http.bin');
    report('HTTP 直链 → 宿主收到下载请求', okA, `filename=${got.filename}, referrer=${got.referrer || '无'}`);

    // 断言 3：blob 分块抓取全链路（1.5MB，跨 3 个 512KB 分块 + 余数）
    await page.click('#dl-blob');
    const start = await pollLog(logPath,
      (m) => m.type === 'download-data-start' && m.filename === 'e2e-blob.bin', 30000, 'blob start');
    const end = await pollLog(logPath,
      (m) => m.type === 'download-data-end' && m.id === start.id, 60000, 'blob end');
    const r = reassembleFromLog(logPath, start.id);
    const want = expectedBytes(blobSize);
    const blobOk = r.contiguous && r.reassembled.equals(want);
    report('blob 下载 → 分块抓取且重组一致', blobOk,
      `分块 ${r.chunks.length}（连续=${r.contiguous}），字节 ${r.reassembled.length}/${want.length}，逐字节一致=${r.reassembled.equals(want)}, 收尾=${end.filename}`);

    // 断言 4：小 blob（777 字节，单分块余数路径）
    await page.click('#dl-blob-small');
    const startS = await pollLog(logPath,
      (m) => m.type === 'download-data-start' && m.filename === 'e2e-blob-small.bin', 30000, '小 blob start');
    const endS = await pollLog(logPath,
      (m) => m.type === 'download-data-end' && m.id === startS.id, 60000, '小 blob end');
    const rS = reassembleFromLog(logPath, startS.id);
    const wantS = expectedBytes(smallSize);
    const smallOk = rS.contiguous && rS.reassembled.equals(wantS);
    report('小 blob 下载 → 余数路径重组一致', smallOk,
      `分块 ${rS.chunks.length}（连续=${rS.contiguous}），字节 ${rS.reassembled.length}/${wantS.length}，逐字节一致=${rS.reassembled.equals(wantS)}, 收尾=${endS.filename}`);

    // 断言 5：data 链接后台解码
    await page.click('#dl-data');
    const start2 = await pollLog(logPath,
      (m) => m.type === 'download-data-start' && m.filename === 'e2e-data.bin', 30000, 'data start');
    const end2 = await pollLog(logPath,
      (m) => m.type === 'download-data-end' && m.id === start2.id, 60000, 'data end');
    const r2 = reassembleFromLog(logPath, start2.id);
    const want2 = expectedBytes(dataSize);
    const dataOk = r2.contiguous && r2.reassembled.equals(want2);
    report('data 链接 → 后台解码且重组一致', dataOk,
      `分块 ${r2.chunks.length}（连续=${r2.contiguous}），字节 ${r2.reassembled.length}/${want2.length}，逐字节一致=${r2.reassembled.equals(want2)}, 收尾=${end2.filename}`);

    // 断言 6：接管开关（v2.50）——唯一真相在宿主侧状态文件，precheck 应答 takeover:false 时放行。
    // 弹窗页在后台标签里会被冻结/节流，必须带到前台再等状态（v2.48 沙箱实测）。
    const popup = await context.newPage();
    await popup.bringToFront();
    await popup.goto(`chrome-extension://${extId}/popup.html`);
    await popup.bringToFront();
    report('弹窗已无接管开关（唯一真相在 TaoDown 设置页）',
      (await popup.locator('#enabled').count()) === 0, 'checkbox 已移除');
    let pingOk = false;
    let pingDetail = '';
    for (let i = 0; i < 40 && !pingOk; i++) {
      await new Promise((r) => setTimeout(r, 250));
      try {
        pingDetail = await popup.locator('#status').textContent();
        if (pingDetail.includes('已连接')) {
          pingOk = true;
        }
      } catch (e) {
        pingDetail = '读取失败: ' + (e && e.message || e);
      }
    }
    report('弹窗 ping → 已连接宿主', pingOk, `状态文本=${pingDetail}`);

    const statePath = path.join(logDir, 'takeover_state.json');
    const writeState = (enabled) => {
      fs.writeFileSync(statePath, JSON.stringify({ browser_takeover: enabled, updated: 'e2e' }));
    };
    writeState(false);
    const beforeOff = countLog(logPath, (m) => m.type === 'download' && (m.url || '').endsWith('/file2.bin'));
    await page.click('#dl-http2');
    await new Promise((r) => setTimeout(r, 5000));
    const afterOff = countLog(logPath, (m) => m.type === 'download' && (m.url || '').endsWith('/file2.bin'));
    report('关闭接管 → 直链放行给浏览器', beforeOff === afterOff,
      `宿主收到 ${beforeOff}→${afterOff} 条（应不变）`);

    writeState(true);
    await page.click('#dl-http2');
    const got2 = await pollLog(logPath,
      (m) => m.type === 'download' && (m.url || '').endsWith('/file2.bin'), 30000, '恢复接管');
    const okRe = (got2.filename || '').includes('e2e-http2.bin');
    report('重新开启 → 恢复接管', okRe, `filename=${got2.filename}`);

    // 断言 7：状态文件缺失 → 默认接管（与真实宿主「读不到按开启」一致，不丢下载）
    fs.rmSync(statePath, { force: true });
    await page.click('#dl-http2');
    const got3 = await pollLog(logPath,
      (m) => m.type === 'download' && (m.url || '').endsWith('/file2.bin'), 30000, '状态缺失默认接管');
    report('状态文件缺失 → 默认接管不丢下载', (got3.filename || '').includes('e2e-http2.bin'),
      `filename=${got3.filename}`);
    await popup.close();
  } finally {
    await context.close().catch(() => {});
    server.close();
  }

  const failed = results.filter((r) => !r.ok);
  console.log(`\nE2E 结论: ${results.length - failed.length}/${results.length} 通过${failed.length ? '，存在失败项' : '，全过'}`);
  process.exitCode = failed.length ? 1 : 0;
}

main().catch((e) => {
  console.error('E2E 台架异常:', e && e.message ? e.message : e);
  process.exit(1);
});
