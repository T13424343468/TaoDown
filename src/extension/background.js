// TaoDown 浏览器接管扩展（批次五）
// 拦截浏览器下载 -> 取消浏览器内置下载 -> 经 Native Messaging 转发 TaoDown
// TaoDown 主程序收到请求后弹出保存位置（默认选中设置页的下载根目录）
'use strict';

// v2.52 兜底：任何未捕获的 promise 拒绝/异常先记录再拦下，
// 不再冒泡到扩展错误页（夸克等内核会把它当扩展错误展示）。
self.addEventListener('unhandledrejection', (event) => {
  try {
    console.warn('[TaoDown] 未处理的 promise 拒绝：', event && event.reason);
  } catch (e) { /* 记录失败不影响 */ }
  if (event) {
    try { event.preventDefault(); } catch (e2) { /* 拦截失败不影响 */ }
  }
});

const HOST_NAME = 'com.tao.taodown';

// —— 夸克系内核识别（v2.53）——
// 夸克 PC 版收窄了扩展可用的下载接口（同 IDM 等接管类扩展在该内核的处境），
// 其下载包装层与弹窗包装层存在已知兼容问题：错误页「reading 'id'」的来源在内核侧，
// 扩展代码逐字节核对无此访问。识别到夸克系 UA 后避开两个高危调用点：
// ①不挂弹窗（报错上下文就在弹窗包装层，图标点击改为无弹窗，连接状态看徽标）；
// ②不调用 chrome.downloads.cancel（其包装层可能内部崩溃并归错到扩展头上）；
// ③不挂任何 downloads 监听（v2.54 收口：夸克真机多轮反馈接管始终哑火且错误页持续报错，
//   说明其下载包装层对扩展下载接口已裁剪/破坏——任何依赖都会成为报错来源）。
//   夸克上的接管只走右键菜单「用 TaoDown 下载」（contextMenus + 宿主通道，不碰下载接口），
//   程序内复制链接下载不受影响。
const IS_QUIRK_LIKE = (() => {
  try {
    return /quark|ubrowser|ucbrowser/i.test((navigator && navigator.userAgent) || '');
  } catch (e) {
    return false;
  }
})();

if (IS_QUIRK_LIKE && chrome.action && typeof chrome.action.setPopup === 'function') {
  try {
    chrome.action.setPopup({ popup: '' });
  } catch (e) {
    // 个别内核不支持：退化为默认弹窗
  }
}
const MENU_ID = 'taodown-download-link';

// —— 接管权认领（v2.52）——
// onDeterminingFilename 与 onCreated 对同一条下载先后触发，且派发顺序因内核而异
// （Chromium 139 实测 onCreated 先于 onDeterminingFilename）。用「认领制」仲裁：
// 先到者登记条目但不立即接管，给主链路 800ms 缓冲认领；主链路认领后兜底链路作废。
// 这样主链路（文件名完整）永远优先，主链路缺失（夸克等内核裁剪或包装层破坏）时
// 兜底链路 800ms 后接管，接管不丢。
const PRIMARY_GRACE_MS = 800;
const claims = new Map(); // downloadId -> { claimed, timer, t }

function getClaim(item, url) {
  const id = item && item.id !== undefined ? String(item.id) : 'no-id';
  let entry = claims.get(id);
  if (!entry) {
    entry = { claimed: false, timer: null, t: Date.now() };
    claims.set(id, entry);
    // 容量修剪：认领表只做短时仲裁，超量清理已过期条目
    if (claims.size > 300) {
      const now = Date.now();
      for (const [k, v] of claims) {
        if (now - v.t > 60000) {
          claims.delete(k);
        }
      }
    }
  }
  return entry;
}

// 主链路认领：清掉兜底链路的定时器，接管权归主链路
function claimPrimary(entry) {
  entry.claimed = true;
  entry.t = Date.now();
  if (entry.timer) {
    clearTimeout(entry.timer);
    entry.timer = null;
  }
}

// 安全应答：文件名缺失时回空建议（内核按默认命名），绝不阻塞浏览器下载链路
function suggestSafe(item, suggest) {
  try {
    suggest(item && item.filename ? { filename: item.filename } : {});
  } catch (e) {
    // 个别内核应答接口异常：放弃应答，浏览器按默认处理
  }
}

// 接管开关的唯一真相在 TaoDown 设置页：宿主 precheck 读取状态文件应答，
// 插件不再自存开关（两处开关会出现「关了还在接管」的错乱，v2.50 去掉）。
async function takeoverEnabled() {
  // v1.2.10 定案：宿主连不上或应答失败一律按「不接管」——先取消再转交失败会两头落空
  // （浏览器下载已取消、TaoDown 又没收到 = 文件凭空消失，真机 2026-11-02 实证）。
  // 宿主不可达意味着登记通道有问题，不是「主程序没开」：connectNative 本身会拉起宿主进程，
  // 拉不起就是通道坏了，此时让浏览器自己下载，文件绝不能丢。
  const reply = await forwardToTaoDown({ type: 'precheck' });
  if (reply && reply.ok === true) {
    return reply.takeover !== false; // 宿主应答缺字段按开启
  }

  return false;
}

function setBadge(state) {
  const map = {
    ok: { text: '✓', color: '#1a7f37' },
    fail: { text: '!', color: '#c62828' },
    idle: { text: '', color: '#666666' },
  };
  const badge = map[state] || map.idle;
  try {
    chrome.action.setBadgeText({ text: badge.text });
    chrome.action.setBadgeBackgroundColor({ color: badge.color });
  } catch (e) {
    // 个别内核没有 chrome.action，徽标属于外观增强
  }
}

// 把一个下载请求转发给 TaoDown 本机宿主；返回 promise<响应>
function forwardToTaoDown(payload) {
  return new Promise((resolve) => {
    let port;
    try {
      port = chrome.runtime.connectNative(HOST_NAME);
    } catch (e) {
      resolve({ ok: false, detail: '未连接到 TaoDown，请先启动 TaoDown 主程序' });
      return;
    }
    let settled = false;
    port.onMessage.addListener((msg) => {
      settled = true;
      resolve(msg || { ok: false, detail: '空响应' });
      try { port.disconnect(); } catch (e) { /* 已断开 */ }
    });
    port.onDisconnect.addListener(() => {
      if (!settled) {
        const err = chrome.runtime.lastError
          ? chrome.runtime.lastError.message
          : '宿主提前断开';
        resolve({ ok: false, detail: err });
      }
    });
    port.postMessage(payload);
  });
}

// v1.2.11 关键修正：去掉「同链接 30 秒只转交一次」的 URL 去重。
// 它当初防的是主链路与兜底链路对同一次下载的重复转交，但认领制（claims，按下载条目 id 仲裁）
// 已经完整覆盖这个场景；URL 去重却带来真问题——用户在询问窗点「暂不」后再点同一链接，
// 扩展会静默跳过转交，浏览器下载又被取消，两头落空，表现就是「取消后不再弹窗」。
// 每次点击下载都是独立的一次确认，互不干扰（用户定案 2026-11-03）。
// 真正需要按 URL 挡的只有一处：转交失败把下载交回浏览器重下时，这条重下的下载
// 会被 onDeterminingFilename 再次拦截造成循环——用 restoredUrls 放行名单挡住它。
const RESTORED_BYPASS_MS = 90000;
const restoredUrls = new Map(); // url -> 交回浏览器的时间戳

function isRestoredUrl(url) {
  if (!url) {
    return false;
  }
  const now = Date.now();
  for (const [k, ts] of restoredUrls) {
    if (now - ts > RESTORED_BYPASS_MS) {
      restoredUrls.delete(k);
    }
  }
  return restoredUrls.has(url);
}

function markRestored(url) {
  if (url) {
    restoredUrls.set(url, Date.now());
  }
}

// 通知 TaoDown 下载；成功打绿标，失败打红标
async function handOff(payload) {
  const msg = {
    type: 'download',
    url: payload.url,
    referrer: payload.referrer || '',
    filename: payload.filename || '',
  };
  const reply = await forwardToTaoDown(msg);
  setBadge(reply && reply.ok ? 'ok' : 'fail');
  return reply;
}

// —— 核心：接管浏览器内置下载 ——
// 只接管支持的类型；blob/data 由专门的分块抓取通道处理（见 captureBlobLike）
const TAKEOVER_SCHEMES = ['http:', 'https:', 'ftp:', 'magnet:', 'ed2k:', 'thunder:'];
const BLOB_CHUNK_RAW = 512 * 1024; // 与宿主 BlobPayload.ChunkRawBytes 一致
const BLOB_IDLE_TIMEOUT_MS = 120000; // 抓取最长空闲时间（大文件读流时每块都会刷新）

function isTakeoverUrl(url) {
  const i = (url || '').indexOf(':');
  if (i <= 0) {
    return false;
  }

  return TAKEOVER_SCHEMES.includes(url.slice(0, i + 1).toLowerCase());
}

function isBlobLikeUrl(url) {
  const u = (url || '').toLowerCase();
  return u.startsWith('blob:') || u.startsWith('data:');
}

// 流媒体分片等无法接管的请求（m3u8/ts 分片）放行给浏览器
function isStreamingFragment(item) {
  return /\.(m3u8|ts|mpd)(\?|$)/i.test((item && item.url) || '');
}

// —— blob/data 支持（v2.12）——
// blob: 链接只在生成它的页面里有效：让页面自己抓内容分块送回，经宿主落盘。
// data: 链接内容就在 URL 里，后台直接解码分块。

function newTransferId() {
  return 't' + Date.now().toString(36) + Math.random().toString(36).slice(2, 8);
}

// 找可抓取的标签页：优先当前活动页，再扫其余 http(s) 页
async function candidateTabs() {
  const tabs = await chrome.tabs.query({});
  const usable = tabs.filter((t) => t && /^https?:/i.test(t.url || ''));
  const active = usable.filter((t) => t.active && t.lastAccessed !== undefined);
  usable.sort((a, b) => (b.active ? 1 : 0) - (a.active ? 1 : 0) || (b.lastAccessed || 0) - (a.lastAccessed || 0));
  return usable.length > 0 ? usable : active;
}

// 在一个标签页里抓 blob 并实时转发给宿主；成功返回 true
function tryCaptureFromTab(tab, url, filename, transferId) {
  return new Promise((resolve) => {
    let settled = false;
    let portToPage = null;
    let portToHost = null;
    let bytesSeen = 0;
    let failTimer = null;

    const finish = (ok, detail) => {
      if (settled) {
        return;
      }

      settled = true;
      if (failTimer) {
        clearTimeout(failTimer);
      }

      if (!ok && portToHost) {
        try { portToHost.postMessage({ type: 'download-data-abort', id: transferId }); } catch (e) { /* 已断开 */ }
      }
      try { if (portToPage) portToPage.disconnect(); } catch (e) { /* 已断开 */ }
      try { if (portToHost) portToHost.disconnect(); } catch (e) { /* 已断开 */ }
      resolve(ok);
    };

    const armIdleTimer = () => {
      if (failTimer) {
        clearTimeout(failTimer);
      }
      failTimer = setTimeout(() => finish(false, '抓取超时'), BLOB_IDLE_TIMEOUT_MS);
    };

    let hport = null;
    try {
      hport = chrome.runtime.connectNative(HOST_NAME);
    } catch (e) {
      finish(false, '未连接到 TaoDown，请先启动 TaoDown 主程序');
      return;
    }
    portToHost = hport;
    // v2.48 实测要点：Native Messaging 是异步写管道，postMessage 后立刻 disconnect
    // 会把最后一条消息挤掉（E2E 中 download-data-end 丢失、blob 落不了盘）。
    // 按已发送未应答计数等宿主回执，收到收尾应答再断开。
    let pendingAcks = 0;
    let endPosted = false;
    let ackTimer = null;
    hport.onMessage.addListener((reply) => {
      if (!reply || !reply.ok) {
        finish(false, (reply && reply.detail) || '宿主拒绝');
        return;
      }
      pendingAcks = Math.max(0, pendingAcks - 1);
      if (endPosted && pendingAcks === 0) {
        if (ackTimer) {
          clearTimeout(ackTimer);
          ackTimer = null;
        }
        finish(true, '完成');
      }
    });
    hport.onDisconnect.addListener(() => {
      if (!settled) {
        if (endPosted) {
          // 收尾指令已按序写入管道后宿主才断开：视为完成
          finish(true, '完成');
          return;
        }
        const err = chrome.runtime.lastError ? chrome.runtime.lastError.message : '宿主断开';
        finish(false, err);
      }
    });

    hport.postMessage({ type: 'download-data-start', id: transferId, filename });
    pendingAcks += 1;

    // tab.id 缺失（个别内核不回填）直接按失败收尾，不读 undefined 的属性
    if (!tab || typeof tab.id !== 'number') {
      finish(false, '标签页不可用');
      return;
    }
    portToPage = chrome.tabs.connect(tab.id, { name: 'taodown-blob' });
    armIdleTimer();
    portToPage.onMessage.addListener((msg) => {
      if (msg.type === 'chunk') {
        armIdleTimer();
        bytesSeen += Math.floor((msg.data || '').length * 0.75);
        if (bytesSeen > 4 * 1024 * 1024 * 1024) {
          hport.postMessage({ type: 'download-data-abort', id: transferId });
          finish(false, '超过 4GB 上限');
          return;
        }
        hport.postMessage({ type: 'download-data-chunk', id: transferId, index: msg.index, data: msg.data });
        pendingAcks += 1;
      } else if (msg.type === 'done') {
        endPosted = true;
        hport.postMessage({ type: 'download-data-end', id: transferId, filename });
        pendingAcks += 1;
        // 等宿主对收尾指令的应答后再 finish（见 hport.onMessage 回执计数）；
        // 超时按失败收尾并发送放弃指令，避免会话悬挂
        ackTimer = setTimeout(() => {
          if (!settled) {
            finish(false, '宿主未确认收尾');
          }
        }, 15000);
      } else if (msg.type === 'error') {
        finish(false, msg.error || '页面抓取失败');
      }
    });
    portToPage.onDisconnect.addListener(() => {
      if (!settled) {
        finish(false, chrome.runtime.lastError ? chrome.runtime.lastError.message : '页面连接断开');
      }
    });
    portToPage.postMessage({ type: 'fetch', url });
  });
}

// data: 链接直接在后台解码分块（无需页面）
function captureDataUrl(url, filename, transferId) {
  let ended = false;
  return new Promise((resolve) => {
    const done = (ok) => {
      if (ended) {
        return;
      }
      ended = true;
      resolve(ok);
    };
    let portToHost = null;
    try {
      portToHost = chrome.runtime.connectNative(HOST_NAME);
    } catch (e) {
      resolve(false);
      return;
    }
    // v2.48 实测要点：与 tryCaptureFromTab 相同，收尾指令要等宿主回执后再断开，
    // 否则最后一条消息会被断开挤掉（E2E 实测 download-data-end 丢失、文件落不了盘）。
    let endPosted = false;
    let pendingAcks = 0;
    let ackTimer = null;
    let endStarted = false;
    const finish = (ok) => {
      if (ackTimer) {
        clearTimeout(ackTimer);
        ackTimer = null;
      }
      if (!ok && endStarted) {
        try { portToHost.postMessage({ type: 'download-data-abort', id: transferId }); } catch (e3) { /* 已断开 */ }
      }
      try { portToHost.disconnect(); } catch (e2) { /* 已断开 */ }
      done(ok);
    };
    portToHost.onMessage.addListener((reply) => {
      if (!reply || !reply.ok) {
        finish(false);
        return;
      }
      pendingAcks = Math.max(0, pendingAcks - 1);
      if (endPosted && pendingAcks === 0) {
        finish(true);
      }
    });
    portToHost.onDisconnect.addListener(() => {
      // 收尾指令已按序写入管道后宿主才断开：视为完成（宿主已处理或即将处理）
      if (endPosted) {
        finish(true);
        return;
      }
      finish(false);
    });
    try {
      endStarted = true;
      portToHost.postMessage({ type: 'download-data-start', id: transferId, filename });
      pendingAcks += 1;
      const m = /^data:[^,]*;base64,(.*)$/i.exec(url);
      let bytes;
      if (m) {
        const bin = atob(m[1]);
        bytes = new Uint8Array(bin.length);
        for (let i = 0; i < bin.length; i++) {
          bytes[i] = bin.charCodeAt(i);
        }
      } else {
        const comma = url.indexOf(',');
        const text = decodeURIComponent(url.slice(comma + 1));
        bytes = new TextEncoder().encode(text);
      }
      for (let i = 0; i < bytes.length; i += BLOB_CHUNK_RAW) {
        // 分段 btoa 步长取 3 的倍数，只有每条分块消息的末段带填充——
        // 内嵌 '=' 会让宿主解码到第一处填充就截断（v2.48 E2E 实测 >32KB 必坏）。
        let b64 = '';
        const end = Math.min(i + BLOB_CHUNK_RAW, bytes.length);
        const seg = 0x8000 - (0x8000 % 3); // 32766
        let j = i;
        for (; j + seg <= end; j += seg) {
          b64 += btoa(String.fromCharCode.apply(null, bytes.subarray(j, j + seg)));
        }
        if (j < end) {
          b64 += btoa(String.fromCharCode.apply(null, bytes.subarray(j, end)));
        }
        portToHost.postMessage({ type: 'download-data-chunk', id: transferId, index: i / BLOB_CHUNK_RAW, data: b64 });
        pendingAcks += 1;
      }
      endPosted = true;
      portToHost.postMessage({ type: 'download-data-end', id: transferId, filename });
      pendingAcks += 1;
      // 等宿主对收尾指令的应答后再断开；超时按失败收尾（与 blob 通道口径一致）
      ackTimer = setTimeout(() => {
        if (!ended) {
          endPosted = false;
          finish(false);
        }
      }, 15000);
    } catch (e) {
      endPosted = false;
      finish(false);
    }
  });
}

async function captureBlobLike(item) {
  const url = (item && (item.finalUrl || item.url)) || '';
  const filename = (item && item.filename) || '';
  const transferId = newTransferId();

  await cancelBrowserDownload(item);

  let ok = false;
  if (/^data:/i.test(url)) {
    ok = await captureDataUrl(url, filename, transferId);
  } else {
    const tabs = await candidateTabs();
    for (const tab of tabs) {
      // 每个候选标签页用独立会话 id，失败时宿主侧已随 abort 清理
      if (await tryCaptureFromTab(tab, url, filename, newTransferId())) {
        ok = true;
        break;
      }
    }
  }

  setBadge(ok ? 'ok' : 'fail');
}

// 交回浏览器重下（v1.2.10）：转交失败时用原始链接重新发起浏览器下载，
// 成功与否不影响主流程（失败说明链接已不可用，静默收场）。
async function restoreToBrowser(item) {
  try {
    const url = (item && (item.finalUrl || item.url)) || '';
    markRestored(url); // 放行名单：交回浏览器的这条重下不再拦截，避免接管循环
    if (url && chrome.downloads && typeof chrome.downloads.download === 'function') {
      await chrome.downloads.download({ url });
    }
  } catch (e) {
    try { console.warn('[TaoDown] 交回浏览器失败：', e); } catch (e2) { /* 忽略 */ }
  }
}

// 取消浏览器侧下载（能取消才取消，失败不阻塞接管；夸克系跳过——见顶部说明）
// v2.53 真机教训（Edge 错误页报「Download must be in progress」Unchecked lastError）：
// 下载太快时，等扩展来取消，文件可能已经下完，取消落空会被内核
// 记成扩展错误。两道防线：先查下载状态，不在进行中就不取消；取消调用本身
// 兼容回调式与 Promise 式两种内核实现，错误一律就地读掉，不再外漏。
async function cancelBrowserDownload(item) {
  if (IS_QUIRK_LIKE) {
    return;
  }
  if (!item || typeof item.id !== 'number') {
    return;
  }
  // v1.2.8：恢复「先查后取消」并收紧。1.2.7 撤掉查询是真机归因错误——错误页
  // 「Download must be in progress」Unchecked 条目的机制是取消落在已完成下载上，
  // 而其回调被内核丢弃（SW 回收或内核包装层）时 lastError 无人读，才记Unchecked。
  // 查询把绝大多数完成态挡在取消之前；查询→取消之间的微秒级残窗由回调读
  // lastError 与 Promise catch 双保险就地消化，不再外漏成错误页条目。
  try {
    const infos = await chrome.downloads.search({ id: item.id });
    const st = infos && infos[0] && infos[0].state;
    if (st && st !== 'in_progress') {
      return;
    }
  } catch (e) {
    // 查询失败不放弃取消尝试，由下方取消调用的兜底读错
  }
  try {
    const pending = chrome.downloads.cancel(item.id, () => {
      // 读取即视为已处理：下载可能刚好完成，属预期，不能外漏成错误页条目
      void (chrome.runtime.lastError && chrome.runtime.lastError.message);
    });
    if (pending && typeof pending.catch === 'function') {
      pending.catch(() => {
        // Promise 式内核：取消落空同样按预期吞掉
      });
    }
  } catch (e) {
    // 取消失败（可能已完成）时不重复接管
  }
}

// —— 主链路：onDeterminingFilename（Chromium 系标配拦截点）——
// 兜底：个别浏览器内核会裁剪 downloads.onDeterminingFilename，缺 API 不能让后台进程带错启动，
// 缺了就静默降级，由下方 onCreated 兜底链路接管。
// v2.54：夸克系内核整段跳过（见顶部说明），不碰任何下载接口。
if (IS_QUIRK_LIKE) {
  try { console.info('[TaoDown] 夸克系内核：下载监听已停用，接管走右键菜单。'); } catch (e) { /* 忽略 */ }
} else if (!chrome.downloads || !chrome.downloads.onDeterminingFilename) {
  console.warn('[TaoDown] 此浏览器内核未提供 downloads.onDeterminingFilename，走 onCreated 兜底链路。');
} else
chrome.downloads.onDeterminingFilename.addListener((item, suggest) => {
  // v2.52 竞态要点：认领必须在任何 await 之前同步完成。onCreated 派发顺序因内核而异，
  // 主链路先认领则接管权归这里（文件名完整）；若等 precheck 异步应答后再认领，
  // 兜底链路的 800ms 定时器可能抢先把接管做掉（E2E 实测收到空文件名）。
  const url = (item && (item.finalUrl || item.url)) || '';
  const entry = getClaim(item, url);
  claimPrimary(entry);
  (async () => {
    // 交回浏览器的重下放行（见 restoredUrls）：不取消、不转交，绝不循环
    if (isRestoredUrl(url)) {
      suggestSafe(item, suggest);
      return;
    }
    if (!(await takeoverEnabled())) {
      suggestSafe(item, suggest);
      return;
    }

    // blob/data：走专门的页面抓取通道（浏览器能下的网页内文件也能要过来）
    if (isBlobLikeUrl(url)) {
      suggestSafe(item, suggest);
      await captureBlobLike(item);
      return;
    }

    if (!isTakeoverUrl(url)) {
      suggestSafe(item, suggest);
      return;
    }
    // 流媒体分片等无法接管的请求（m3u8/ts 分片）放行给浏览器
    if (isStreamingFragment(item)) {
      suggestSafe(item, suggest);
      return;
    }
    // v1.2.9 顺序定案：先应答文件名、后取消。1.2.8 之前的真机错条目根因在此——
    // 取消落地后下载已不在进行中，再对它 suggest 必报「Download must be in progress」，
    // 且 suggest 无读错口，天生 Unchecked（三版全中、blob 通道先 suggest 后 cancel 从不报错，互为印证）。
    // 此刻下载必在进行中，应答绝不出错；随后取消交给 TaoDown（失败不阻塞，接管照走）。
    suggestSafe(item, suggest);
    await cancelBrowserDownload(item);
    const reply = await handOff({
      url: (item && (item.finalUrl || item.url)) || '',
      referrer: (item && item.referrer) || '',
      filename: (item && item.filename) || '',
    });
    if (!(reply && reply.ok)) {
      // v1.2.10 兜底：转交失败把下载还给浏览器重下，绝不静默丢文件
      await restoreToBrowser(item);
    }
  })().catch((e) => {
    // v2.52 兜底：接管链路任何异常不外抛，避免成为扩展错误页的未捕获 promise
    try { console.warn('[TaoDown] 接管链路异常：', e); } catch (e2) { /* 忽略 */ }
  });
  return true; // 异步应答 suggest
});

// —— 兜底链路：onCreated（v2.52）——
// 个别内核（非夸克系）的 downloads.onDeterminingFilename 可能缺失或被其下载管理包装层破坏
// （拦截点不触发=整条接管链路无反应）。onCreated 在下载刚创建时必触发，
// 缺主链路时由它补位：取消浏览器下载 + 转交 TaoDown。夸克系不挂（见顶部说明）。
if (!IS_QUIRK_LIKE && chrome.downloads && chrome.downloads.onCreated) {
  chrome.downloads.onCreated.addListener((item) => {
    // 只登记不立即接管：给主链路 800ms 缓冲认领（派发顺序因内核而异，可能这里先触发）；
    // 到点仍未被认领（夸克等内核主链路缺失）才由本链路接管。
    const url = (item && (item.finalUrl || item.url)) || '';
    if (!isTakeoverUrl(url) || isStreamingFragment(item)) {
      return; // 只兜底直链类；blob/data 仍由主链路专用通道处理
    }
    const entry = getClaim(item, url);
    if (entry.claimed) {
      return; // 主链路已认领
    }
    if (entry.timer) {
      clearTimeout(entry.timer);
    }
    entry.timer = setTimeout(() => {
      entry.timer = null;
      if (entry.claimed) {
        return; // 主链路在缓冲期内认领了
      }
      (async () => {
        if (isRestoredUrl(url)) {
          return; // 交回浏览器的重下放行（见 restoredUrls），不循环接管
        }
        if (!(await takeoverEnabled())) {
          return; // 接管已关闭：浏览器自下，不取消不转交
        }
        entry.claimed = true;
        entry.t = Date.now();
        // 兜底链路触发时文件名通常还没定：文件名留空，由 TaoDown 保存框按链接名预填
        await cancelBrowserDownload(item);
        const reply = await handOff({
          url: (item && (item.finalUrl || item.url)) || '',
          referrer: (item && item.referrer) || '',
          filename: (item && item.filename) || '',
        });
        if (!(reply && reply.ok)) {
          // v1.2.10 兜底：转交失败把下载还给浏览器重下
          await restoreToBrowser(item);
        }
      })().catch((e) => {
        // 兜底链路任何异常不外抛
        try { console.warn('[TaoDown] onCreated 兜底异常：', e); } catch (e2) { /* 忽略 */ }
      });
    }, PRIMARY_GRACE_MS);
  });
}

// —— 右键菜单：用 TaoDown 下载 ——
chrome.runtime.onInstalled.addListener(() => {
  try {
    if (chrome.contextMenus && chrome.contextMenus.create) {
      chrome.contextMenus.create({
        id: MENU_ID,
        title: '用 TaoDown 下载',
        contexts: ['link'],
      });
    }
  } catch (e) {
    // 个别内核没有 contextMenus，右键菜单属于增强
  }
  setBadge('idle');
});

if (chrome.contextMenus && chrome.contextMenus.onClicked) {
  chrome.contextMenus.onClicked.addListener(async (info) => {
    try {
      if (!info || info.menuItemId !== MENU_ID || !info.linkUrl) {
        return;
      }
      await handOff({
        url: info.linkUrl,
        referrer: info.pageUrl || '',
        filename: '',
      });
    } catch (e) {
      // v2.52 兜底：右键接管失败不影响浏览器
    }
  });
}
