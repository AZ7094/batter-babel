/* Batter Babel · 中英文界面文案（src/web/i18n.js）
 *
 * 约定：中文原文就是 key —— t('开始优选')；英文词典只做「中文 → 英文」映射。
 * 这样 app.js / index.html 里不需要再起一套 key 名，宿主自检里断言的中文原文也原样保留。
 *
 * 语言解析顺序：?lang= 查询参数 > localStorage['bb-lang'] > 默认 zh。
 * 切换语言：setLang('en'|'zh') 写 localStorage、刷新 <html data-lang>、重写静态文案，
 * 再通知订阅者（app.js）重画动态部分；当前测速结果、日志、选中游戏都不丢。
 *
 * 对外接口：window.I18N = { lang, t(key, vars), setLang(lang), applyStatic(),
 *                            onChange(fn), dictionary }。
 */
(function () {
  'use strict';

  var STORAGE_KEY = 'bb-lang';
  var SUPPORTED = { zh: true, en: true };

  // 英文词典：key 是中文原文（可带 {name} 占位符），value 是英文（或函数，做单复数）。
  var EN = {
    /* ── 页面静态文案 ── */
    'Batter Babel · 游戏网络优选': 'Batter Babel · Game Network Optimizer',
    '游戏网络优选': 'Game Network Optimizer',
    '正在检测游戏…': 'Detecting games…',
    '主要操作': 'Main actions',
    '游戏': 'Game',
    '选择游戏': 'Select game',
    '检测中…': 'Detecting…',
    '检测游戏': 'Detect games',
    '开始优选': 'Start optimization',
    '写入 hosts': 'Write hosts',
    '还原 hosts': 'Restore hosts',
    '加速游戏': 'Boost game',
    '取消加速': 'Stop boost',
    '系统调优': 'System tuning',
    '还原系统调优': 'Restore tuning',
    '切换语言': 'Switch language',
    '等待开始 · 0%': 'Ready · 0%',
    '等待开始': 'Ready',
    '开始优选：测试所选游戏的 CDN / API 线路，测速过程不会修改 hosts。':
      "Start optimization: measures the selected game's CDN / API routes; nothing is written to hosts while testing.",
    '写入 hosts：将优选结果应用到系统 hosts，需要同意管理员授权。':
      'Write hosts: applies the optimization result to the system hosts file; administrator approval is required.',
    '还原 hosts：': 'Restore hosts: ',
    '如果应用后遇到问题，可移除 Batter Babel 写入的 hosts 标记块。':
      'if something goes wrong after applying, you can remove the hosts block written by Batter Babel.',
    '线路优选结果': 'Route optimization results',
    '运行日志': 'Activity log',
    '0 条': '0 entries',
    '{n} 条': function (vars) {
      var n = Number(vars && vars.n);
      if (n === 1) { return '1 entry'; }
      return (isFinite(n) ? n : 0) + ' entries';
    },

    /* ── 测速线路失败原因（PROBE_REASONS） ── */
    '正常': 'OK',
    '无响应': 'No response',
    '没有返回 HTTP 状态': 'No HTTP status returned',
    '域名解析失败': 'DNS lookup failed',
    '连接被拒绝': 'Connection refused',
    '请求超时': 'Request timed out',
    'TLS 握手失败': 'TLS handshake failed',
    '证书校验失败': 'Certificate validation failed',
    '响应缺少该服务的特征头': "Response is missing this service's signature header",
    '返回内容不是该服务的正常响应': 'Response is not a normal reply from this service',
    '没有解析到候选地址': 'No candidate address resolved',
    '超过时间上限未测完': 'Time limit exceeded before the test finished',
    '未知原因': 'Unknown reason',
    '未提供原因': 'No reason provided',
    '连接错误（curl {code}）': 'Connection error (curl {code})',
    '无法使用（{reason}）': 'Unavailable — {reason}',
    '该游戏没有可优选的线路。': 'This game has no routes to optimize.',
    '未测到可用线路': 'No usable route found',
    '测速中': 'Testing',
    '准备测速': 'Preparing',
    '采样延迟': 'Sampling latency',
    '比较候选线路': 'Comparing routes',
    '测速完成': 'Test complete',
    '未完成': 'Not completed',

    /* ── 检测游戏 ── */
    '正在检测已安装的游戏…': 'Detecting installed games…',
    '检测到 {total} 个已安装游戏，其中 {routed} 个有线路分组': function (vars) {
      var total = Number(vars && vars.total);
      var routed = Number(vars && vars.routed);
      return 'Detected ' + total + ' installed game' + (total === 1 ? '' : 's')
        + ', ' + routed + ' with route groups';
    },
    '检测到 {total} 个已安装游戏，其中 {routed} 个有线路分组（其余可加速游戏或写入游戏配置）': function (vars) {
      var total = Number(vars && vars.total);
      var routed = Number(vars && vars.routed);
      return 'Detected ' + total + ' installed game' + (total === 1 ? '' : 's')
        + ', ' + routed + ' with route groups (the rest can be boosted or get a game config)';
    },
    '没有检测到已安装的游戏。请确认游戏已安装，再点「检测游戏」。':
      'No installed games detected. Make sure the game is installed, then click “Detect games”.',
    '没有已安装的游戏': 'No installed games',
    '没有检测到已安装的游戏': 'No installed games detected',
    '（无线路分组）': ' (no route groups)',
    '检测游戏失败': 'Game detection failed',
    '检测游戏失败：{reason}': 'Game detection failed: {reason}',
    '检测游戏失败，可点「检测游戏」重试。': 'Game detection failed. Click “Detect games” to retry.',

    /* ── QoS 提示 ── */
    '注意：{reason}。「加速游戏」可能无法生效。': 'Note: {reason}. “Boost game” may not take effect.',
    '本机未接入 QoS 能力探测': 'QoS capability probing is not connected on this machine',
    '提示：本机 Windows 没有组策略 QoS 存储，加速规则将写入活动存储，重启后由计划任务自动恢复。':
      'Tip: this Windows install has no Group Policy QoS store; boost rules go to the active store and a scheduled task restores them after a reboot.',
    '注意：本机 Windows 没有组策略 QoS 存储，规则只写入活动存储，重启后需要重新点一次「加速游戏」。':
      'Note: this Windows install has no Group Policy QoS store; the rule only goes to the active store and must be re-applied after a reboot.',

    /* ── 加速按钮 ── */
    '请先选择一个游戏': 'Select a game first',
    '该游戏未安装': 'This game is not installed',
    '未找到可执行文件，无法创建 QoS 加速规则': 'No executable found; cannot create a QoS boost rule',
    '移除该游戏的加速规则': 'Remove this game’s boost rule',
    '为该游戏创建 QoS (DSCP 46) 加速规则，需要管理员授权':
      'Create a QoS (DSCP 46) boost rule for this game; administrator approval is required',

    /* ── 选择游戏 ── */
    '{name} 没有可优选的 CDN / API 线路，但可以加速游戏或写入游戏配置。':
      '{name} has no CDN / API routes to optimize, but it can still be boosted or get a game config.',
    '已选择 {name}，点击「开始优选」测速其 CDN / API 线路。':
      'Selected {name}. Click “Start optimization” to test its CDN / API routes.',
    '未找到可执行文件，「加速游戏」暂不可用。': 'No executable found; “Boost game” is unavailable.',

    /* ── 开始优选 ── */
    '正在测速 {name}…': 'Testing {name}…',
    '开始优选：{name} (start_job + run_action cf-optimize)':
      'Start optimization: {name} (start_job + run_action cf-optimize)',
    '优选没有完成': 'Optimization did not complete',
    '优选未完成：{reason}': 'Optimization incomplete: {reason}',
    '该游戏没有可优选的线路，本次只写入了游戏配置。':
      'This game has no routes to optimize; only the game config was written this time.',
    '优选完成，可点击「写入 hosts」应用结果。':
      'Optimization complete. Click “Write hosts” to apply the result.',
    '优选完成：{count} 组 · {seconds} s': function (vars) {
      var count = Number(vars && vars.count);
      return 'Optimization complete: ' + count + ' group' + (count === 1 ? '' : 's')
        + ' · ' + vars.seconds + ' s';
    },
    '注意：以上为固定样本（FIXTURE），不是真实网络测速结果。':
      'Note: the data above is a fixed sample (FIXTURE), not a real network measurement.',
    '游戏配置：{path} → {message}': 'Game config: {path} → {message}',
    '游戏配置 → {message}': 'Game config → {message}',
    '没有收到宿主答复，结果未知。': 'No response from the host; the result is unknown.',
    '没有收到宿主答复：{reason}': 'No response from the host: {reason}',
    '优选请求失败：{reason}': 'Optimization request failed: {reason}',

    /* ── 通用动作结果 ── */
    '正在执行：{action}…': 'Running: {action}…',
    '宿主拒绝了这次操作': 'The host refused this action',
    '{action} 被拒绝：{reason}': '{action} was rejected: {reason}',
    '部分完成': 'Partially completed',
    '{action}：{message}': '{action}: {message}',
    '已完成': 'Completed',
    '{action} 请求失败：{reason}': '{action} request failed: {reason}',

    /* ── 加速 / 取消加速 ── */
    '正在取消 {name}…如弹出「用户账户控制」窗口，请点「是」。':
      'Stopping the boost for {name}… if a User Account Control prompt appears, click “Yes”.',
    '正在加速 {name}…如弹出「用户账户控制」窗口，请点「是」。':
      'Boosting {name}… if a User Account Control prompt appears, click “Yes”.',
    '取消加速：{name} → run_action (action:"restore")':
      'Stop boost: {name} → run_action (action:"restore")',
    '加速游戏：{name} → run_action (action:"optimize")':
      'Boost game: {name} → run_action (action:"optimize")',
    'restore：已取消 {name} 的加速': 'restore: boost stopped for {name}',
    'optimize：已加速 {name}': 'optimize: {name} boosted',

    /* ── 写入 / 还原 hosts ── */
    '还没有可写入的优选结果': 'No optimization result to write yet',
    '写入 hosts 未发送：当前结果里没有可写入的映射。':
      'Write hosts was not sent: the current result has no writable mapping.',
    '正在写入 hosts…如弹出「用户账户控制」窗口，请点「是」。':
      'Writing hosts… if a User Account Control prompt appears, click “Yes”.',
    '写入 hosts：{count} 条映射 → run_action (action:"cf-apply")': function (vars) {
      var count = Number(vars && vars.count);
      return 'Write hosts: ' + count + ' mapping' + (count === 1 ? '' : 's')
        + ' → run_action (action:"cf-apply")';
    },
    'cf-apply 被拒绝：{reason}': 'cf-apply was rejected: {reason}',
    'cf-apply：{message}': 'cf-apply: {message}',
    'cf-apply 请求失败：{reason}': 'cf-apply request failed: {reason}',

    /* ── 浏览器预览（FIXTURE） ── */
    '浏览器模拟：宿主拒绝了这次优选（FIXTURE，未做真实测速）':
      'Browser mock: the host rejected this optimization (FIXTURE, no real measurement).',
    '浏览器模拟：本版不做系统写操作': 'Browser mock: this build performs no system writes.',
    '未知命令：{command}': 'Unknown command: {command}',
    '[FIXTURE] demo={demo}：模拟状态，不是真实网络结果。':
      '[FIXTURE] demo={demo}: simulated state, not a real network result.',
    '浏览器模拟 · FIXTURE：当前没有桌面宿主，数据为固定样本。':
      'Browser mock · FIXTURE: no desktop host; the data is a fixed sample.',
    'demo 参数已忽略：当前有桌面宿主，演示模式只在浏览器预览中可用。':
      'The demo parameter was ignored: a desktop host is present; demo mode only works in the browser preview.',
    '浏览器模拟：未接入 QoS 能力探测': 'Browser mock: QoS capability probing is not connected',
  };

  var lang = 'zh';

  function normalize(value) {
    return value === 'en' || value === 'zh' ? value : '';
  }

  function fromQuery() {
    try {
      var m = /[?&]lang=(zh|en)(?=&|#|$)/.exec(String(window.location.search || ''));
      return m ? m[1] : '';
    } catch (err) { return ''; }
  }

  function fromStorage() {
    try {
      var store = window.localStorage;
      return store ? normalize(store.getItem(STORAGE_KEY)) : '';
    } catch (err) { return ''; }
  }

  function resolveLang() {
    return fromQuery() || fromStorage() || 'zh';
  }

  function format(text, vars) {
    if (!vars) { return text; }
    return text.replace(/\{(\w+)\}/g, function (whole, name) {
      return Object.prototype.hasOwnProperty.call(vars, name) && vars[name] != null
        ? String(vars[name])
        : whole;
    });
  }

  function t(key, vars) {
    var text = String(key == null ? '' : key);
    if (lang === 'en' && Object.prototype.hasOwnProperty.call(EN, text)) {
      var value = EN[text];
      if (typeof value === 'function') { return value(vars || {}); }
      return format(value, vars);
    }
    return format(text, vars);
  }

  function each(selector, fn) {
    if (typeof document === 'undefined' || !document || typeof document.querySelectorAll !== 'function') {
      return;
    }
    var nodes = document.querySelectorAll(selector);
    for (var i = 0; i < nodes.length; i++) { fn(nodes[i]); }
  }

  function applyStatic() {
    if (typeof document === 'undefined' || !document) { return; }
    var root = document.documentElement;
    if (root && root.setAttribute) {
      root.setAttribute('data-lang', lang);
      root.setAttribute('lang', lang === 'en' ? 'en' : 'zh-CN');
    }
    each('[data-i18n]', function (el) { el.textContent = t(el.getAttribute('data-i18n')); });
    each('[data-i18n-html]', function (el) { el.innerHTML = t(el.getAttribute('data-i18n-html')); });
    each('[data-i18n-aria]', function (el) {
      el.setAttribute('aria-label', t(el.getAttribute('data-i18n-aria')));
    });
    each('[data-i18n-title]', function (el) {
      el.setAttribute('title', t(el.getAttribute('data-i18n-title')));
    });
    each('[data-i18n-placeholder]', function (el) {
      el.setAttribute('placeholder', t(el.getAttribute('data-i18n-placeholder')));
    });
    if (typeof document.getElementById === 'function') {
      var toggle = document.getElementById('lang');
      if (toggle && toggle.setAttribute) {
        toggle.setAttribute('aria-pressed', lang === 'en' ? 'true' : 'false');
      }
    }
  }

  var listeners = [];

  function onChange(fn) {
    if (typeof fn !== 'function') { return function () {}; }
    listeners.push(fn);
    return function off() {
      var at = listeners.indexOf(fn);
      if (at >= 0) { listeners.splice(at, 1); }
    };
  }

  function setLang(next) {
    var wanted = normalize(next);
    if (!wanted) { return lang; }
    lang = wanted;
    I18N.lang = wanted;
    try {
      var store = window.localStorage;
      if (store) { store.setItem(STORAGE_KEY, wanted); }
    } catch (err) { /* 隐私模式 / file:// 下不允许持久化，不影响本次切换 */ }
    applyStatic();
    var pending = listeners.slice();
    for (var i = 0; i < pending.length; i++) {
      try { pending[i](wanted); } catch (err) { /* 单个订阅者失败不阻断其他重绘 */ }
    }
    return lang;
  }

  function bindToggle() {
    if (typeof document === 'undefined' || !document || typeof document.getElementById !== 'function') {
      return;
    }
    var toggle = document.getElementById('lang');
    if (!toggle || toggle.__bbLangBound || typeof toggle.addEventListener !== 'function') { return; }
    toggle.__bbLangBound = true;
    toggle.addEventListener('click', function () {
      setLang(lang === 'en' ? 'zh' : 'en');
    });
  }

  var I18N = {
    lang: lang,
    t: t,
    setLang: setLang,
    applyStatic: applyStatic,
    onChange: onChange,
    dictionary: EN
  };

  window.I18N = I18N;
  lang = I18N.lang = resolveLang();

  function boot() {
    applyStatic();
    bindToggle();
  }

  if (typeof document !== 'undefined' && document && document.readyState === 'loading'
    && typeof document.addEventListener === 'function') {
    document.addEventListener('DOMContentLoaded', boot);
  } else {
    boot();
  }
})();
