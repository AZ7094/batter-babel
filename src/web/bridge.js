// Java 版桌面桥（BB-410）。
//
// 这一页原来是 Tauri 的前端：它只碰 window.__TAURI__ 两处（core.invoke 与 event.listen），
// 所以复用它不需要重写界面，只需要把这个旧名字接到 Java 后端上。这个文件就是那层胶水：
//
//   core.invoke(command, args)  ->  Java 的 BridgeHost.invoke(callId, command, argsJson)
//   后端答案                   ->  window.__bbDeliver(callId, ok, payloadJson)
//   后端事件                   ->  window.__bbEmit(eventName, payloadJson)
//
// 三条纪律，和 Rust 版一致：
//   1. 请求"成立了"就 resolve（哪怕业务上是失败：{ok:false} 或 status:"FAILED" 都算答案）；
//      只有请求根本没成立（后端缺席、调用抛错、超时）才 reject —— 前端靠这条区分"没做成"和"没发出"。
//   2. app.js 在顶层就取 window.__TAURI__，因此本文件必须先于 app.js 求值。
//   3. 通道存在就立即发送；排队只是"宿主还没挂上通道"时的暂存，不再是正常路径。
//      WebView2 宿主在导航完成后只会通知一次 ready，旧实现把后续请求全留在 queued 里，
//      于是第二次以后的 invoke 永远发不出去（P0 复现的缺陷）。
(function () {
  'use strict';

  // In Tauri the real bridge already exists. Keep both desktop entry points runnable.
  if (window.__TAURI__ && window.__TAURI__.core
      && typeof window.__TAURI__.core.invoke === 'function') { return; }

  if (document.body) { document.body.classList.add('bb-webview'); }

  // 答复预算按命令/动作区分（架构约定：普通只读 RPC 10 秒、长任务单独预算）。
  // 一刀切 10 分钟会让 get_progress 之类的短请求在宿主异常时把界面挂住十分钟，
  // 而 cf-optimize 真跑起来要 1-2 分钟，不能用 10 秒误杀。超时只拒绝、绝不自动重发。
  var SHORT_TIMEOUT_MS = 10 * 1000;
  var SCAN_FORCE_TIMEOUT_MS = 30 * 1000;
  var WRITE_TIMEOUT_MS = 2 * 60 * 1000;
  var LONG_TIMEOUT_MS = 10 * 60 * 1000;
  var OVERRIDE_MIN_MS = 500;
  var OVERRIDE_MAX_MS = 15 * 60 * 1000;
  var MAX_PENDING = 64;
  // 极简沙箱（Node vm）里没有定时器；没有就退化成"不设时限"，其余语义不变。
  var setTimer = typeof setTimeout === 'function' ? setTimeout : function () { return null; };
  var clearTimer = typeof clearTimeout === 'function' ? clearTimeout : function () {};

  // 每页唯一前缀：刷新后新页面的 callId 与旧页面不可能相同，因此旧页面的迟到答复
  // 只会命中"查无此单"（被忽略），不会把新页面的请求提前结算掉。
  var pageToken = 'p' + Date.now().toString(36) + Math.random().toString(36).slice(2, 8);
  var counter = 0;
  var pending = Object.create(null);
  var pendingCount = 0;
  var queued = [];
  var listeners = Object.create(null);
  var disposed = false;

  function parse(text) {
    if (!text) { return null; }
    try { return JSON.parse(text); } catch (err) { return null; }
  }

  function describe(value) {
    if (typeof value === 'string') { return value; }
    if (value && typeof value.message === 'string') { return value.message; }
    return '桌面后端拒绝了这次请求';
  }

  // 解析 args（字符串或对象）拿 action；解析失败按短预算处理，宿主会回结构化失败。
  function actionOf(args) {
    var value = args;
    if (typeof value === 'string') { value = parse(value); }
    return value && typeof value.action === 'string' ? value.action : '';
  }

  // 测试覆盖只接受有限正数且落在 [0.5s, 15min] 内；范围外忽略并回落到默认策略，
  // 避免页面用 0/Infinity/负数把预算改成"永不超时"。
  function overrideMs() {
    var configured = window.__bbRequestTimeoutMs;
    if (typeof configured !== 'number' || !isFinite(configured) || configured <= 0) { return 0; }
    var rounded = Math.round(configured);
    if (rounded < OVERRIDE_MIN_MS || rounded > OVERRIDE_MAX_MS) { return 0; }
    return rounded;
  }

  function budgetFor(command, args) {
    var override = overrideMs();
    if (override) { return override; }
    if (command === 'get_progress' || command === 'start_job') { return SHORT_TIMEOUT_MS; }
    if (command === 'run_action') {
      var action = actionOf(args);
      if (action === 'cf-optimize' || action === 'cf-optimize-recheck') { return LONG_TIMEOUT_MS; }
      if (action === 'scan') { return SHORT_TIMEOUT_MS; }
      if (action === 'scan-force') { return SCAN_FORCE_TIMEOUT_MS; }
      if (action) { return WRITE_TIMEOUT_MS; }
    }
    return SHORT_TIMEOUT_MS;
  }

  // 结算一张单子：从 pending 与 queued 里同时摘掉，回滚 pendingCount 并清掉定时器。
  // 返回 null 表示这张单子已经被别的路径结算过（重复答复、超时后迟到），调用方必须停手。
  function drop(callId) {
    var entry = pending[callId];
    if (!entry) { return null; }
    delete pending[callId];
    pendingCount--;
    if (entry.timer) { clearTimer(entry.timer); }
    for (var i = 0; i < queued.length; i++) {
      if (queued[i][0] === callId) { queued.splice(i, 1); break; }
    }
    return entry;
  }

  function abandonAll(reason) {
    var ids = Object.keys(pending);
    for (var i = 0; i < ids.length; i++) {
      var entry = drop(ids[i]);
      if (entry) { entry.reject(reason); }
    }
    queued.length = 0;
  }

  // 宿主回填答案（ok=true 表示后端给出了答案，ok=false 表示请求没有成立）。
  window.__bbDeliver = function (callId, ok, payloadJson) {
    var entry = drop(callId);
    if (!entry) { return; }
    var value = parse(payloadJson);
    if (ok) { entry.resolve(value); } else { entry.reject(describe(value)); }
  };

  // 宿主推送事件（当前只有一个：bb-progress；前端同时也在轮询 get_progress）。
  window.__bbEmit = function (name, payloadJson) {
    var set = listeners[name];
    if (!set || !set.length) { return; }
    var payload = parse(payloadJson);
    set.slice().forEach(function (handler) {
      try { handler({ event: name, payload: payload }); } catch (err) { console.error(err); }
    });
  };

  function send(command, args) {
    return new Promise(function (resolve, reject) {
      if (disposed) { reject('页面已关闭，请求未发送'); return; }
      var callId = pageToken + '-' + (++counter);
      var body;
      try {
        body = JSON.stringify(args === undefined || args === null ? {} : args);
      } catch (err) {
        reject('参数无法序列化：' + describe(err));
        return;
      }
      if (pendingCount >= MAX_PENDING) {
        reject('桌面请求排队已满（' + MAX_PENDING + '），请稍后重试');
        return;
      }

      var entry = { resolve: resolve, reject: reject, timer: null };
      pending[callId] = entry;
      pendingCount++;

      var limit = budgetFor(command, args, body);
      entry.timer = setTimer(function () {
        var expired = drop(callId);
        if (!expired) { return; }
        // 措辞必须诚实：超时只说明"没有收到答复"，不能断言请求没发出。
        expired.reject('未收到答复，结果未知（等待 ' + Math.round(limit / 1000) + ' 秒）');
      }, limit);

      // 通道在就直接发；没有通道（JavaFX 尚未注入 __bbHost 等）才暂存等 ready。
      try {
        if (!transmit(callId, command, body)) { queued.push([callId, command, body]); }
      } catch (err) {
        var failed = drop(callId);
        if (failed) { failed.reject(describe(err)); }
      }
    });
  }

  // 出站传输（BB-411）：同一个页面要能活在三种宿主里，答案一律由宿主 executeScript 回填
  // （window.__bbDeliver / window.__bbEmit），只有"请求怎么发出去"不同：
  //   __bbHost      JavaFX WebView 的 Java 对象直连（进程内、最快）
  //   chrome.webview  WebView2：postMessage 一个 JSON 文本，宿主用 WebMessageReceived 收
  //   cefQuery      JCEF：CefMessageRouter 的同步往返（请求体同样是 JSON 文本）
  // 三种都没有时返回 false，由调用方排队，等宿主调用 window.__bbReady()。
  function transmit(callId, command, body) {
    var message = JSON.stringify({ callId: callId, command: command, args: body });
    if (window.__bbHost && typeof window.__bbHost.invoke === 'function') {
      window.__bbHost.invoke(callId, command, body);
      return true;
    }
    if (window.chrome && window.chrome.webview
        && typeof window.chrome.webview.postMessage === 'function') {
      window.chrome.webview.postMessage(message);
      return true;
    }
    if (typeof window.cefQuery === 'function') {
      window.cefQuery({ request: message, onSuccess: function () {}, onFailure: function () {} });
      return true;
    }
    return false;
  }

  function flush() {
    while (queued.length) {
      var item = queued.shift();
      if (!pending[item[0]]) { continue; } // 已经超时/被放弃的单子不再发送
      try {
        if (!transmit(item[0], item[1], item[2])) { queued.unshift(item); break; }
      } catch (err) {
        var entry = drop(item[0]);
        if (entry) { entry.reject(describe(err)); }
      }
    }
  }

  // WebView2 的入站通道：宿主也可以用 PostWebMessageAsJson 把答案推回来。
  if (window.chrome && window.chrome.webview
      && typeof window.chrome.webview.addEventListener === 'function') {
    window.chrome.webview.addEventListener('message', function (event) {
      var data = typeof event.data === 'string' ? parse(event.data) : event.data;
      if (!data || !data.callId) { return; }
      window.__bbDeliver(data.callId, data.ok !== false, data.payload === undefined ? null : JSON.stringify(data.payload));
    });
  }

  function invoke(command, args) {
    return send(command, args);
  }

  function listen(event, handler) {
    var set = listeners[event];
    if (!set) { set = []; listeners[event] = set; }
    set.push(handler);
    if (window.__bbHost && typeof window.__bbHost.subscribe === 'function') {
      try { window.__bbHost.subscribe(event); } catch (err) { /* 订阅失败不影响轮询通道 */ }
    }
    return Promise.resolve(function () {
      var current = listeners[event] || [];
      var index = current.indexOf(handler);
      if (index >= 0) { current.splice(index, 1); }
    });
  }

  // 宿主挂上 __bbHost 之后调用它，把排队中的请求按序发出（重复调用是幂等的）。
  window.__bbReady = flush;

  // 只读自省：核对某条命令/动作会拿到多少答复预算（不发请求、不改状态）。
  // 供宿主自检与前端回归断言使用，避免测试去翻闭包变量。
  window.__bbTimeoutPolicy = function (command, args) {
    return budgetFor(command, args);
  };

  // 页面销毁：宿主（或卸载钩子）可以显式收尾，别让挂起的 Promise 永远悬着。
  window.__bbDispose = function () {
    disposed = true;
    abandonAll('页面已关闭，请求已取消');
  };

  if (window.addEventListener) {
    window.addEventListener('pagehide', function () {
      abandonAll('页面已关闭，请求已取消');
    });
  }

  window.__TAURI__ = {
    core: { invoke: invoke },
    event: { listen: listen }
  };
})();
