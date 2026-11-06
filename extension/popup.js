// TaoDown 接管助手弹窗（v2.53）：
// sendNativeMessage 一次性调用——不保留悬空端口，全链 try/catch，
// 同时兼容「返回 Promise」的内核实现：拒绝也兜住，任何浏览器内核下
// 都不再产生「未捕获 promise」类错误；接管开关只在 TaoDown 设置页。
'use strict';

const HOST_NAME = 'com.tao.taodown';

document.addEventListener('DOMContentLoaded', () => {
  const status = document.getElementById('status');
  if (!status) {
    return;
  }

  status.textContent = '正在检查连接…';
  let api;
  try {
    api = chrome && chrome.runtime;
  } catch (e) {
    api = null;
  }
  if (!api || typeof api.sendNativeMessage !== 'function') {
    status.textContent = '未连接：请重新安装 TaoDown。';
    return;
  }

  // 回调与 Promise 两条路都走：回调型内核走回调，Promise 型内核走 Promise；
  // settled 保证只显示一次（个别内核两条都触发）。
  let settled = false;
  const show = (text) => {
    if (settled) {
      return;
    }
    settled = true;
    status.textContent = text;
  };

  try {
    const pending = api.sendNativeMessage(HOST_NAME, { type: 'ping' }, (reply) => {
      try {
        const err = chrome.runtime.lastError;
        if (err) {
          show('未连接：重启 TaoDown 后再试。');
          return;
        }
        if (reply && reply.ok === true) {
          show(reply.takeover === false
            ? '接管已关闭：在 TaoDown 设置页打开后即可接管下载。'
            : '已连接 TaoDown，点下载即接管。');
        } else {
          show('TaoDown 响应异常，请重启 TaoDown。');
        }
      } catch (e) {
        show('未连接：请重新安装 TaoDown。');
      }
    });
    if (pending && typeof pending.then === 'function') {
      pending.then(
        (reply) => {
          // 回调型实现通常已应答；Promise 兑现仅在回调未触发时生效
          if (!settled) {
            if (reply && reply.ok === true) {
              show(reply.takeover === false
                ? '接管已关闭：在 TaoDown 设置页打开后即可接管下载。'
                : '已连接 TaoDown，点下载即接管。');
            } else {
              show('TaoDown 响应异常，请重启 TaoDown。');
            }
          }
        },
        () => show('未连接：重启 TaoDown 后再试。'),
      );
    }
  } catch (e) {
    show('未连接：请重新安装 TaoDown。');
  }
});
