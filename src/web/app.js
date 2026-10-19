/* Batter Babel · 定稿白底界面（app.js）
 *
 * 只做三件事：
 *   1. 把 scan / cf-optimize / get_progress 的结果画进固定结构；
 *   2. 写动作原样发给宿主，宿主拒绝就如实显示（不假装成功）；
 *   3. 没有宿主时（浏览器直接打开）用带 FIXTURE 标记的模拟数据把状态跑通。
 *
 * 桥：src/public/bridge.js 提供 window.__TAURI__.core.invoke / event.listen。
 */
(function () {
  'use strict';

  var api = window.__TAURI__;
  if (!api || !api.core) { return; }

  var $ = function (id) { return document.getElementById(id); };
  var els = {
    game: $('game-select'), hint: $('hint'), fill: $('fill'), track: $('track'), pct: $('pct'),
    cards: $('cards'), hosts: $('hosts-path'), log: $('log'), logMeta: $('log-meta'),
    note: $('inline-note'),
    detect: $('btn-detect'), optimize: $('optimize'), apply: $('btn-apply'),
    restore: $('btn-restore'), boost: $('btn-boost'), boostLabel: $('boost-label'),
    tune: $('btn-tune'), restoreTune: $('btn-restore-tune')
  };

  var state = {
    games: [], game: null, running: false, boostBusy: false,
    groups: null, canApply: false, hostsPath: '', renderKey: ''
  };
  var stickToBottom = true;
  var logIntent = false;
  var fixture = false;

  // ?demo=running|failed 只给浏览器预览用（必须没有桌面宿主），且必须打 [FIXTURE] 标记，
  // 不能把模拟状态当成真实测速结果。真实宿主里这个参数会被忽略。
  var demo = (function () {
    var m = /[?&]demo=([a-z]+)/.exec(String(location.search || ''));
    return m ? m[1] : '';
  })();

  /* ── 小工具 ─────────────────────────────────────────── */

  function esc(value) {
    return String(value == null ? '' : value).replace(/[&<>"']/g, function (c) {
      return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c];
    });
  }

  function now() {
    var d = new Date();
    var p = function (n) { return n < 10 ? '0' + n : '' + n; };
    return p(d.getHours()) + ':' + p(d.getMinutes()) + ':' + p(d.getSeconds());
  }

  function log(text, tone) {
    var li = document.createElement('li');
    if (tone) { li.dataset.tone = tone; }
    li.innerHTML = '<span class="t">[' + now() + ']</span><span class="m">' + esc(text) + '</span>';
    // 用户只有在主动滚上去看历史时才脱离「跟底」；默认（含内容由短变长）一直跟。
    els.log.appendChild(li);
    if (stickToBottom) { els.log.scrollTop = els.log.scrollHeight; }
    els.logMeta.textContent = els.log.children.length + ' 条';
  }

  // 只有用户的手势（滚轮/拖动滚动条/触摸）才算「主动翻历史」；程序滚到底不改变跟随状态。
  function markLogIntent() { logIntent = true; }
  els.log.addEventListener('wheel', markLogIntent, { passive: true });
  els.log.addEventListener('touchmove', markLogIntent, { passive: true });
  els.log.addEventListener('pointerdown', markLogIntent);
  els.log.addEventListener('scroll', function () {
    if (!logIntent) { return; }
    logIntent = false;
    stickToBottom = els.log.scrollHeight - els.log.scrollTop - els.log.clientHeight < 16;
  });

  function setProgress(percent, message) {
    var value = Math.max(0, Math.min(100, Math.round(Number(percent) || 0)));
    els.fill.style.transform = 'scaleX(' + (value / 100) + ')';
    els.track.setAttribute('aria-valuenow', String(value));
    // 扫光只在「真的还在跑」（0 < p < 100）时出现，停在 0% 或 100% 都收掉。
    var busy = value > 0 && value < 100;
    els.track.classList.toggle('busy', busy);
    els.track.setAttribute('aria-busy', busy ? 'true' : 'false');
    els.pct.textContent = (message || '等待开始') + ' · ' + value + '%';
  }

  function setHint(text) { els.hint.textContent = text; }

  function showNote(text) {
    if (!text) { els.note.hidden = true; els.note.textContent = ''; return; }
    els.note.hidden = false;
    els.note.textContent = text;
  }

  function messageOf(data, fallback) {
    return (data && typeof data.message === 'string' && data.message) || fallback || '未提供原因';
  }

  function failed(data) {
    return !data || typeof data !== 'object' || data.ok !== true
      || ['FAILED', 'CANCELLED', 'TIMED_OUT'].indexOf(data.status) >= 0;
  }

  function isProbe(group) {
    return group && (group.mode === 'probe' || (group.tag && group.tag !== 'cf' && group.tag !== 'amazon' && group.mode !== 'cloudflare'));
  }

  /* ── 卡片 ───────────────────────────────────────────── */

  function domainList(domains) {
    return (domains || []).map(function (d) { return '<span>' + esc(d) + '</span>'; }).join('');
  }

  // 宿主把「为什么这条线路不可用」作为结构化原因返回，页面翻成界面文案（与发布版同义）。
  var PROBE_REASONS = {
    None: '正常',
    NoResponse: '无响应',
    NoStatus: '没有返回 HTTP 状态',
    DnsFailure: '域名解析失败',
    ConnectFailure: '连接被拒绝',
    Timeout: '请求超时',
    TlsHandshake: 'TLS 握手失败',
    Certificate: '证书校验失败',
    MissingHeader: '响应缺少该服务的特征头',
    BusinessContent: '返回内容不是该服务的正常响应',
    NoCandidate: '没有解析到候选地址',
    DeadlineExceeded: '超过时间上限未测完'
  };

  function probeReason(reason) {
    var key = String(reason == null ? '' : reason).trim();
    if (!key) { return '未知原因'; }
    if (Object.prototype.hasOwnProperty.call(PROBE_REASONS, key)) { return PROBE_REASONS[key]; }
    var http = /^HttpStatus(\d{3})$/.exec(key);
    if (http) { return 'HTTP ' + http[1]; }
    var curl = /^CurlExit(\d+)$/.exec(key);
    if (curl) { return '连接错误（curl ' + curl[1] + '）'; }
    return key;
  }

  function lineHtml(ip, meta) {
    return '<div class="line"><span class="line-ip">' + esc(ip) + '</span>'
      + '<span class="line-meta">' + esc(meta) + '</span></div>';
  }

  // probe 组一行 = 域名 → 结果；失败条目给出来源原因，不用红色铺满整行。
  function probeLineHtml(item) {
    var domain = '<span class="line-domain">' + esc(item.domain || '') + '</span>'
      + '<span class="line-arrow" aria-hidden="true">→</span>';
    if (item.ok === true && item.ip) {
      // T 版 probe 行只显示「域名 → IP（延迟）」；吞吐只用于 cloudflare 组。
      var meta = '(' + item.latency + ' ms)';
      return '<div class="line line-probe">' + domain
        + '<span class="line-ip">' + esc(item.ip) + '</span>'
        + '<span class="line-meta">' + esc(meta) + '</span></div>';
    }
    return '<div class="line line-probe line-bad">' + domain
      + '<span class="line-meta">无法使用（' + esc(probeReason(item.reason)) + '）</span></div>';
  }

  function cardHtml(kind, title, domains, bodyHtml, status) {
    return '<article class="card" data-kind="' + kind + '">'
      + (status ? '<span class="card-status" data-state="' + status + '" aria-hidden="true"></span>' : '')
      + '<h3 class="card-title">' + esc(title) + '</h3>'
      + '<div class="card-domains"><span class="link-badge" aria-hidden="true">'
      + '<svg viewBox="0 0 24 24"><path d="M10 14a4 4 0 0 0 5.7 0l2.6-2.6a4 4 0 0 0-5.7-5.7L11.4 7"/>'
      + '<path d="M14 10a4 4 0 0 0-5.7 0l-2.6 2.6a4 4 0 0 0 5.7 5.7L12.6 17"/></svg></span>'
      + '<div class="domain-list">' + domainList(domains) + '</div></div>'
      + bodyHtml
      + '</article>';
  }

  function placeholderBody() {
    return '<p class="card-empty pending">—</p>';
  }

  // 测速中：3 条错相位骨架条，不写死任何 IP / 速率（结果只能来自宿主）。
  function skeletonBody() {
    return '<div class="skeleton" role="status" aria-label="测速中"><span></span><span></span><span></span></div>';
  }

  var NO_ROUTES_NOTE = '该游戏没有可优选的线路。';

  function renderPlaceholders() {
    if (!state.game || noRoutes(state.game)) {
      // 没有线路分组时仍保留两张占位卡（Cloudflare / CloudFront），只用一行中性说明，
      // 不显示「—」也不编造 IP：优选对它真的没有可测的东西。
      var body = state.game ? '<p class="card-empty">' + NO_ROUTES_NOTE + '</p>' : placeholderBody();
      els.cards.innerHTML = cardHtml('cloudflare', 'Cloudflare (download CDN)', [], body)
        + cardHtml('probe', 'CloudFront (API)', [], body);
      return;
    }
    var html = (state.game.optGroups || []).map(function (g) {
      return cardHtml(isProbe(g) ? 'probe' : 'cloudflare', g.label, g.domains, placeholderBody());
    }).join('');
    els.cards.innerHTML = html;
  }

  function linesBody(group, fresh) {
    var lines = [];
    if (group.mode === 'probe' || (Array.isArray(group.items) && !group.bestIp)) {
      (group.items || []).forEach(function (item) {
        if (!item) { return; }
        if (item.ok === true && !item.ip) { return; }
        lines.push(probeLineHtml(item));
      });
    } else {
      var candidates = Array.isArray(group.lines) && group.lines.length
        ? group.lines
        : (group.bestIp ? [{ ip: group.bestIp, downloadMbps: group.downloadMbps, latency: group.latency }] : []);
      candidates.forEach(function (line) {
        if (!line || !line.ip) { return; }
        var meta = line.downloadMbps != null && line.latency != null
          ? '(' + line.downloadMbps + ' MB/s, ' + line.latency + ' ms)'
          : (line.latency != null ? '(' + line.latency + ' ms)' : '');
        lines.push(lineHtml(line.ip, meta));
      });
    }
    if (!lines.length) {
      // 整组没有可用 IP：保留占位 —，行下给一条弱化说明，不伪造地址。
      return '<p class="card-empty pending">—</p><p class="card-note">未测到可用线路</p>';
    }
    // is-fresh 只在内容真的变了时由 renderResult 打上，重复渲染同一份结果不会重播动画。
    return '<div class="lines' + (fresh ? ' is-fresh' : '') + '">' + lines.join('') + '</div>';
  }

  function renderResult(groups) {
    state.groups = groups || [];
    var key = JSON.stringify(state.groups);
    var fresh = key !== state.renderKey;
    state.renderKey = key;
    var html = state.groups.map(function (g) {
      return cardHtml(isProbe(g) ? 'probe' : 'cloudflare', g.label || '', g.domains || [], linesBody(g, fresh));
    }).join('');
    if (!html) { renderPlaceholders(); return; }
    els.cards.innerHTML = html;
  }

  /* ── 宿主调用 ───────────────────────────────────────── */

  var mock = null;

  function invoke(command, args) {
    if (mock) { return mock(command, args); }
    return api.core.invoke(command, args);
  }

  function listen(event, handler) {
    if (mock) { return Promise.resolve(function () {}); }
    return api.event.listen(event, handler);
  }

  function anyUsable(groups) {
    return (groups || []).some(function (g) {
      if (!g) { return false; }
      if (g.bestIp) { return true; }
      return (g.items || []).some(function (i) { return i && i.ok; });
    });
  }

  /* ── 数据流 ─────────────────────────────────────────── */

  // 发布版语义（Tauri app.js computeGameList）：下拉列出**全部已安装**游戏，按名字排序。
  // support=false 只代表「没有 CDN / API 线路分组」，不代表性不能加速：它仍然要能选来做
  // 「加速游戏」或写入游戏配置，所以不隐藏、不禁用，只在选项文本后加后缀说明。
  function selectable(games) {
    return (games || []).filter(function (g) {
      return g && g.installed === true;
    }).map(function (g) {
      var copy = {};
      Object.keys(g).forEach(function (k) { copy[k] = g[k]; });
      // 没有 accelerated 字段的游戏一律按 false（不猜「已加速」）。
      copy.accelerated = g.accelerated === true;
      return copy;
    }).sort(function (a, b) {
      return String(a.name || '').localeCompare(String(b.name || ''), 'zh-CN');
    });
  }

  // 「有没有可优选的线路」是比 support 标志更可靠的事实：宿主给 cs2 留过 support=true 的特例，
  // 但它的 optGroups 是空的。两者任一成立就按「没有可优选线路」处理，界面不伪造结果。
  function noRoutes(game) {
    return !game || game.support === false || !(game.optGroups && game.optGroups.length);
  }

  async function scan(force) {
    els.detect.disabled = true;
    setHint('正在检测已安装的游戏…');
    try {
      var data = await invoke('run_action', { action: force ? 'scan-force' : 'scan' });
      if (failed(data)) { throw new Error(messageOf(data, '检测游戏失败')); }
      fixture = !!(data && data.fixture);
      state.games = selectable(data && data.games);
      var all = (data && data.games) || [];
      var routed = state.games.filter(function (g) { return !noRoutes(g); }).length;
      log('检测到 ' + state.games.length + ' 个已安装游戏，其中 ' + routed + ' 个有线路分组'
        + (state.games.length > routed ? '（其余可加速游戏或写入游戏配置）' : ''));
      if (state.games.length === 0) {
        state.game = null;
        renderPlaceholders();
        setHint('没有检测到已安装的游戏。请确认游戏已安装，再点「检测游戏」。');
        setProgress(0, '没有已安装的游戏');
        els.game.innerHTML = '<option value="">没有检测到已安装的游戏</option>';
        renderBoostButton();
        return;
      }
      var previous = state.game ? state.game.id : '';
      // 默认选「画像里的第一个有线路分组游戏」（目录顺序，不是按名字排后的第一个）：
      // 名字排序只影响下拉展示顺序，不该改变用户打开界面时看到的默认游戏。
      var fallback = ((data && data.games) || []).filter(function (g) {
        return g && g.installed === true && !noRoutes(g);
      })[0] || state.games[0];
      var keep = state.games.some(function (g) { return g.id === previous; }) ? previous : fallback.id;
      els.game.innerHTML = state.games.map(function (g) {
        // 无线路分组只加后缀：选项本身照常可选，不做 disabled。
        return '<option value="' + esc(g.id) + '">' + esc(g.name)
          + (g.support === false ? '（无线路分组）' : '') + '</option>';
      }).join('');
      selectGame(keep);
      reportQos(data && data.qos);
    } catch (err) {
      var text = String((err && err.message) || err);
      log('检测游戏失败：' + text, 'fail');
      showNote('检测游戏失败：' + text);
      setHint('检测游戏失败，可点「检测游戏」重试。');
      renderPlaceholders();
    } finally {
      els.detect.disabled = false;
    }
  }

  function reportQos(qos) {
    if (!qos) { return; }
    if (qos.available === false) {
      log('注意：' + (qos.reason || '本机未接入 QoS 能力探测') + '。「加速游戏」可能无法生效。', 'warn');
    } else if (qos.persistent === false) {
      log('提示：本机 Windows 没有组策略 QoS 存储，加速规则将写入活动存储，重启后由计划任务自动恢复。', 'warn');
    }
  }

  // 「加速游戏」按钮只按真实状态画（发布版 renderBoostButton）：文案随 accelerated 切换，
  // 可用性只看 installed && executablePath —— 与有没有线路分组 / 有没有先测速无关。
  function renderBoostButton() {
    var game = state.game;
    var on = !!(game && game.accelerated === true);
    if (els.boostLabel) { els.boostLabel.textContent = on ? '取消加速' : '加速游戏'; }
    // 已加速时按钮换一套配色：只换文字的话「加速游戏」和「取消加速」长得一模一样。
    els.boost.classList.toggle('is-on', on);
    els.boost.disabled = !game || game.installed !== true || !game.executablePath
      || state.running || state.boostBusy;
    if (!game) {
      els.boost.title = '请先选择一个游戏';
    } else if (!game.installed) {
      els.boost.title = '该游戏未安装';
    } else if (!game.executablePath) {
      els.boost.title = '未找到可执行文件，无法创建 QoS 加速规则';
    } else {
      els.boost.title = on ? '移除该游戏的加速规则' : '为该游戏创建 QoS (DSCP 46) 加速规则，需要管理员授权';
    }
  }

  function selectGame(id) {
    // 下拉的显示值必须跟着状态走：默认选中的「第一个有线路分组的游戏」在按名字排序后
    // 未必是第一个 option，不同步会出现「显示 Apex、提示 Limbus」的错位。
    if (els.game.value !== id) { els.game.value = id; }
    state.game = state.games.filter(function (g) { return g.id === id; })[0] || null;
    state.canApply = false;
    state.groups = null;
    state.applyEntries = null;
    state.renderKey = '';
    els.apply.disabled = true;
    els.restore.disabled = state.running;
    showNote('');
    if (!state.game) { renderBoostButton(); renderPlaceholders(); return; }
    setProgress(0, '等待开始');
    if (noRoutes(state.game)) {
      // 仍可选、仍能「加速游戏」：状态行如实说明这条游戏能做什么。
      setHint(state.game.name + ' 没有可优选的 CDN / API 线路，但可以加速游戏或写入游戏配置。');
    } else {
      setHint('已选择 ' + state.game.name + '，点击「开始优选」测速其 CDN / API 线路。');
    }
    if (state.game.executablePath) {
      // 已知 exe 才有资格加速；缺失时按钮禁用，但原因写在 note 里，不只靠 title。
    } else {
      showNote('未找到可执行文件，「加速游戏」暂不可用。');
    }
    renderPlaceholders();
    renderBoostButton();
  }

  function setRunning(running) {
    state.running = running;
    els.optimize.disabled = running;
    els.restore.disabled = running;
    els.game.disabled = running;
    els.detect.disabled = running || els.detect.disabled;
    renderBoostButton();
  }

  function runningCards() {
    if (!state.game) { return; }
    var groups = state.game.optGroups || [];
    if (!groups.length) { renderPlaceholders(); return; }
    els.cards.innerHTML = groups.map(function (g) {
      return cardHtml(isProbe(g) ? 'probe' : 'cloudflare', g.label, g.domains, skeletonBody(), 'running');
    }).join('');
  }

  async function optimize() {
    if (!state.game || state.running) { return; }
    showNote('');
    setRunning(true);
    runningCards();
    setProgress(0, '准备测速');
    setHint('正在测速 ' + state.game.name + '…');
    log('开始优选：' + state.game.name + ' (start_job + run_action cf-optimize)');

    var started = Date.now();
    var jobId = '';
    var stopped = false;
    var lastSeq = -1;
    var timer = 0;
    var pollInFlight = false;
    var unlisten = null;

    function tick(payload) {
      if (stopped || !payload) { return; }
      if (payload.jobId && jobId && payload.jobId !== jobId) { return; }
      if (typeof payload.sequence === 'number') {
        if (payload.sequence < lastSeq) { return; }
        lastSeq = payload.sequence;
      }
      setProgress(payload.percent || 0, payload.message || '测速中');
    }

    try {
      var job = await invoke('start_job', { action: 'cf-optimize', gameId: state.game.id });
      jobId = (job && job.jobId) || '';
    } catch (err) {
      jobId = '';
    }

    try {
      unlisten = await listen('bb-progress', function (event) { tick(event && event.payload); });
    } catch (err) { unlisten = null; }

    timer = setInterval(async function () {
      if (stopped || pollInFlight) { return; }
      pollInFlight = true;
      try { tick(await invoke('get_progress', { jobId: jobId })); } catch (err) { /* 轮询失败不致命 */ }
      finally { pollInFlight = false; }
    }, 300);

    try {
      var data = await invoke('run_action', { action: 'cf-optimize', gameId: state.game.id, jobId: jobId });
      var elapsed = ((Date.now() - started) / 1000).toFixed(1);
      if (failed(data)) {
        var reason = messageOf(data, '优选没有完成');
        renderPlaceholders();
        setProgress(0, '未完成');
        setHint('优选未完成：' + reason);
        showNote('优选未完成：' + reason);
        log('优选未完成：' + reason, 'fail');
        return;
      }
      var groups = (data && data.groups) || [];
      // 只把宿主标记为可写的映射留给 cf-apply；页面不再用展示行反推 IP。
      state.applyEntries = (data && Array.isArray(data.entries)) ? data.entries : null;
      renderResult(groups);
      setProgress(100, '测速完成');
      if (!groups.length && noRoutes(state.game)) {
        // 没有线路分组：宿主只做游戏配置写入 —— 不假装「可以写 hosts」。
        setHint('该游戏没有可优选的线路，本次只写入了游戏配置。');
      } else {
        setHint('优选完成，可点击「写入 hosts」应用结果。');
      }
      log('优选完成：' + groups.length + ' 组 · ' + elapsed + ' s', 'ok');
      if (data && data.hostsPath) {
        state.hostsPath = data.hostsPath;
        els.hosts.textContent = data.hostsPath;
      }
      if (data && data.fixture) {
        log('注意：以上为固定样本（FIXTURE），不是真实网络测速结果。', 'warn');
      }
      if (data && Array.isArray(data.configs)) {
        data.configs.forEach(function (item) {
          var where = item.path ? '：' + item.path : '';
          log('游戏配置' + where + ' → ' + item.message, item.ok ? 'ok' : 'warn');
        });
      }
      state.canApply = anyUsable(groups);
      els.apply.disabled = !state.canApply;
      els.restore.disabled = false; // 还原不依赖本次是否有结果：只要以前写过就该能撤
      renderBoostButton();
    } catch (err) {
      var text = String((err && err.message) || err);
      renderPlaceholders();
      setProgress(0, '未完成');
      setHint('没有收到宿主答复，结果未知。');
      showNote('没有收到宿主答复：' + text);
      log('优选请求失败：' + text, 'fail');
    } finally {
      stopped = true;
      if (timer) { clearInterval(timer); }
      if (typeof unlisten === 'function') { try { unlisten(); } catch (err) { /* 忽略 */ } }
      setRunning(false);
      els.detect.disabled = false;
    }
  }

  // 宿主沿用发布版（Rust/PS）的英文结果文案，页面只做展示层翻译，不改返回体。
  // 认出已知格式就换成中文；认不出就原样显示——不许把没见过的句子猜成成功。
  var HOST_MESSAGES = [
    [/^Wrote (\d+) hosts? mapping\(s\)(?: \(backup: (.+)\))?\.?$/,
      function (m) { return '已写入 ' + m[1] + ' 条 hosts 映射' + (m[2] ? '（备份：' + m[2] + '）' : ''); }],
    [/^No valid mappings to write \((\d+) rejected\)\.?$/,
      function (m) { return '没有可写入的有效映射（已拒绝 ' + m[1] + ' 条）'; }],
    [/^No mappings to write\.?$/, function () { return '没有可写入的映射'; }],
    [/^Restored, removed (\d+) host line\(s\)\.?$/,
      function (m) { return '已还原，移除 ' + m[1] + ' 行 hosts 记录'; }],
    [/^Raised scheduling priority for (\d+) (.+?) process\(es\)$/,
      function (m) { return '已提升 ' + m[1] + ' 个 ' + m[2] + ' 进程的调度优先级'; }],
    [/^(.+) is not running$/, function (m) { return m[1] + ' 未在运行'; }]
  ];

  function localize(message) {
    if (typeof message !== 'string' || !message) { return message; }
    for (var i = 0; i < HOST_MESSAGES.length; i++) {
      var found = HOST_MESSAGES[i][0].exec(message.trim());
      if (found) { return HOST_MESSAGES[i][1](found); }
    }
    return message;
  }

  async function act(action, extra, button) {
    var args = { action: action };
    if (extra) { Object.keys(extra).forEach(function (k) { args[k] = extra[k]; }); }
    if (button) { button.disabled = true; }
    showNote('正在执行：' + action + '…');
    try {
      var data = await invoke('run_action', args);
      // 系统调优是「多项分别落地」：PARTIAL 不是拒绝，如实把哪几项没成写进日志。
      var partial = data && data.ok !== true && (data.status === 'PARTIAL' || data.status === 'NO_SNAPSHOT');
      if (failed(data) && !partial) {
        var reason = localize(messageOf(data, '宿主拒绝了这次操作'));
        showNote(reason);
        log(action + ' 被拒绝：' + reason, 'warn');
        return;
      }
      if (partial) {
        var partly = localize(messageOf(data, '部分完成'));
        showNote(partly);
        log(action + '：' + partly, 'warn');
        return;
      }
      var message = localize(messageOf(data, '已完成'));
      showNote('');
      log(action + '：' + message, 'ok');
      if (data && data.hostsPath) { els.hosts.textContent = data.hostsPath; }
    } catch (err) {
      var text = String((err && err.message) || err);
      showNote('没有收到宿主答复，结果未知。');
      log(action + ' 请求失败：' + text, 'fail');
    } finally {
      if (button) { button.disabled = false; }
    }
  }

  // 加速 / 取消加速：一个按钮按 accelerated 双向切换（发布版 toggleBoost）。
  // 只要求 installed && executablePath；失败时按钮保持原状，原因照实写进 note。
  async function toggleBoost() {
    var game = state.game;
    if (!game || game.installed !== true || !game.executablePath) { return; }
    if (state.running || state.boostBusy) { return; }
    var turningOff = game.accelerated === true;
    state.boostBusy = true;
    showNote((turningOff ? '正在取消 ' : '正在加速 ') + game.name + '…如弹出「用户账户控制」窗口，请点「是」。');
    log((turningOff ? '取消加速：' : '加速游戏：') + game.name
      + ' → run_action (action:"' + (turningOff ? 'restore' : 'optimize') + '")');
    renderBoostButton();
    try {
      var data = await invoke('run_action', {
        action: turningOff ? 'restore' : 'optimize',
        gameId: game.id
      });
      if (failed(data)) {
        var reason = localize(messageOf(data, '宿主拒绝了这次操作'));
        showNote(reason);
        log((turningOff ? 'restore' : 'optimize') + ' 被拒绝：' + reason, 'warn');
        return;
      }
      // 宿主明确回了 accelerated 就以后端为准，否则按本次切换方向更新。
      game.accelerated = typeof (data && data.accelerated) === 'boolean' ? data.accelerated : !turningOff;
      if (turningOff) {
        log('restore：已取消 ' + game.name + ' 的加速', 'ok');
      } else {
        log('optimize：已加速 ' + game.name, 'ok');
      }
      if (!turningOff && data && data.persistent === false) {
        log('注意：本机 Windows 没有组策略 QoS 存储，规则只写入活动存储，重启后需要重新点一次「加速游戏」。', 'warn');
      }
      if (data && data.executablePath) { game.executablePath = data.executablePath; }
      showNote('');
    } catch (err) {
      var text = String((err && err.message) || err);
      showNote('没有收到宿主答复，结果未知。');
      log((turningOff ? 'restore' : 'optimize') + ' 请求失败：' + text, 'fail');
    } finally {
      state.boostBusy = false;
      renderBoostButton();
    }
  }

  // 发布版 cf-apply 契约：domains = "TAG|ip|domain;TAG|ip|domain;…"（tag 大写）。
  // 只从**当前渲染结果**生成：cloudflare 组用 bestIp，probe 组只取 ok===true 的条目；
  // 没有 bestIp 的组整组跳过，失败条目绝不写进 hosts。去重后为空就不发请求。
  function collectDomains(groups) {
    var seen = Object.create(null);
    var list = [];
    // 宿主返回的 entries 是「本次真正可写的映射」；优先用它，页面不再自己猜 IP。
    var entries = state.applyEntries;
    if (Array.isArray(entries) && entries.length) {
      entries.forEach(function (entry) {
        if (!entry || !entry.domain || !entry.ip) { return; }
        var tag = String(entry.tag || "").toUpperCase();
        if (!tag) { return; }
        var triple = tag + "|" + entry.ip + "|" + entry.domain;
        if (seen[triple]) { return; }
        seen[triple] = true;
        list.push(triple);
      });
      return list;
    }
    (groups || []).forEach(function (group) {
      if (!group) { return; }
      var tag = String(group.tag == null ? '' : group.tag).toUpperCase();
      if (!tag) { return; }
      var rows = [];
      if (isProbe(group)) {
        (group.items || []).forEach(function (item) {
          if (item && item.ok === true && item.ip && item.domain) { rows.push([item.ip, item.domain]); }
        });
      } else if (group.bestIp) {
        (group.domains || []).forEach(function (domain) {
          if (domain) { rows.push([group.bestIp, domain]); }
        });
      }
      rows.forEach(function (row) {
        var ip = String(row[0]);
        var domain = String(row[1]);
        if (!ip || !domain) { return; }
        // 分隔符属于协议本身：带进值里会拼出别人没打算写的映射，直接跳过。
        if (/[|;]/.test(ip) || /[|;]/.test(domain)) { return; }
        var triple = tag + '|' + ip + '|' + domain;
        if (seen[triple]) { return; }
        seen[triple] = true;
        list.push(triple);
      });
    });
    return list;
  }

  async function applyHosts() {
    var domains = collectDomains(state.groups);
    if (!domains.length) {
      showNote('还没有可写入的优选结果');
      log('写入 hosts 未发送：当前结果里没有可写入的映射。', 'warn');
      return;
    }
    var args = { action: 'cf-apply', domains: domains.join(';') };
    if (state.game && state.game.id) { args.gameId = state.game.id; }
    els.apply.disabled = true;
    showNote('正在写入 hosts…如弹出「用户账户控制」窗口，请点「是」。');
    log('写入 hosts：' + domains.length + ' 条映射 → run_action (action:"cf-apply")');
    try {
      var data = await invoke('run_action', args);
      if (failed(data)) {
        var reason = localize(messageOf(data, '宿主拒绝了这次操作'));
        showNote(reason);
        log('cf-apply 被拒绝：' + reason, 'warn');
        return;
      }
      var message = localize(messageOf(data, '已完成'));
      // 门禁与宿主自检都匹配「已写入」这个前缀；宿主 message 自己带前缀时不能念两遍。
      var written = /^已写入/.test(message) ? message : '已写入 ' + message;
      showNote('');
      log('cf-apply：' + written, 'ok');
      if (data && data.hostsPath) { els.hosts.textContent = data.hostsPath; }
    } catch (err) {
      var text = String((err && err.message) || err);
      showNote('没有收到宿主答复，结果未知。');
      log('cf-apply 请求失败：' + text, 'fail');
    } finally {
      els.apply.disabled = !state.canApply;
    }
  }

  /* ── 事件绑定 ───────────────────────────────────────── */

  els.game.addEventListener('change', function () { selectGame(els.game.value); });
  els.detect.addEventListener('click', function () { scan(true); });
  els.optimize.addEventListener('click', optimize);
  els.apply.addEventListener('click', applyHosts);
  els.restore.addEventListener('click', function () { act('cf-restore', { gameId: state.game && state.game.id }, els.restore); });
  els.boost.addEventListener('click', toggleBoost);
  els.tune.addEventListener('click', function () { act('tune-system', null, els.tune); });
  els.restoreTune.addEventListener('click', function () { act('restore-tune', null, els.restoreTune); });

  /* ── 没有宿主时的模拟数据（FIXTURE，仅用于浏览器预览） ── */

  function mockHost() {
    var progress = { percent: 0, message: '准备测速', status: 'RUNNING', sequence: 0 };
    var games = [{
      id: 'limbus', name: 'Limbus Company', installed: true, support: true, standalone: true, vendor: 'Steam',
      // 预览里也把「可加速」这一层如实带出来；真正的写操作仍然由下面的 FIXTURE 拒绝。
      executablePath: 'C:\\Games\\Limbus Company\\LimbusCompany.exe', accelerated: false,
      optGroups: [
        { tag: 'cf', label: 'Cloudflare (download CDN)', mode: 'cloudflare',
          domains: ['download.limbuscompanycdn.org', 'downloadcommon.limbuscompanycdn.org', 'downloadfmod.limbuscompanycdn.org'] },
        { tag: 'amazon', label: 'CloudFront (API)', mode: 'probe',
          domains: ['www.limbuscompanyapi.com', 'notice.limbuscompanyapi.com'] }
      ]
    }];
    // 预览「已加速」配色：仅浏览器 mock，打 FIXTURE 横幅。
    if (demo === 'accelerated') { games[0].accelerated = true; }
    var result = {
      ok: true, status: 'SUCCEEDED', hostsPath: 'C:\\Windows\\System32\\drivers\\etc\\hosts', fixture: true,
      groups: [
        { tag: 'cf', label: 'Cloudflare (download CDN)', mode: 'cloudflare', domains: games[0].optGroups[0].domains,
          bestIp: '104.16.32.11', downloadMbps: 16, latency: 70,
          lines: [
            { ip: '104.16.32.11', downloadMbps: 16, latency: 70 },
            { ip: '104.16.32.58', downloadMbps: 14, latency: 76 }
          ] },
        { tag: 'amazon', label: 'CloudFront (API)', mode: 'probe', domains: games[0].optGroups[1].domains,
          items: [
            { ok: true, domain: 'www.limbuscompanyapi.com', ip: '172.64.41.20', downloadMbps: 18, latency: 68 },
            { ok: true, domain: 'notice.limbuscompanyapi.com', ip: '172.64.42.17', downloadMbps: 15, latency: 74 }
          ] }
      ]
    };
    return function (command, args) {
      return new Promise(function (resolve) {
        if (command === 'start_job') { resolve({ jobId: 'mock-job', protocolVersion: 1 }); return; }
        if (command === 'run_action' && args && args.action === 'scan') {
          setTimeout(function () {
            resolve({ ok: true, scannedAt: new Date().toISOString(), fixture: true,
              games: games, qos: { available: false, persistent: false, reason: '浏览器模拟：未接入 QoS 能力探测' } });
          }, 500);
          return;
        }
        if (command === 'run_action' && args && args.action === 'cf-optimize') {
          if (demo === 'running') {
            // 停在「测速中」：进度 46%，永远不返回结果，用来观察骨架 + 状态点 + 扫光。
            progress = { jobId: 'mock-job', percent: 46, message: '比较候选线路', status: 'RUNNING', sequence: progress.sequence + 1 };
            return new Promise(function () {});
          }
          if (demo === 'mixed') {
            // 固定样本：一条可用 + 两条失败原因 + 整组无可用 IP 的占位，用来核对失败行排版。
            setTimeout(function () {
              resolve({
                ok: true, status: 'SUCCEEDED', fixture: true,
                groups: [
                  { tag: 'cf', label: 'Cloudflare (download CDN)', mode: 'cloudflare',
                    domains: games[0].optGroups[0].domains, bestIp: null, lines: [] },
                  { tag: 'amazon', label: 'CloudFront (API)', mode: 'probe',
                    domains: games[0].optGroups[1].domains,
                    items: [
                      { ok: true, domain: 'www.limbuscompanyapi.com', ip: '172.64.41.20', downloadMbps: 18, latency: 68 },
                      { ok: false, domain: 'notice.limbuscompanyapi.com', reason: 'Timeout' },
                      { ok: false, domain: 'api.limbuscompanyapi.com', reason: 'HttpStatus403' },
                      { ok: false, domain: 'cdn.limbuscompanyapi.com', reason: 'CurlExit28' }
                    ] }
                ]
              });
            }, 900);
            return;
          }

          if (demo === 'failed') {
            setTimeout(function () {
              resolve({ ok: false, status: 'FAILED', message: '浏览器模拟：宿主拒绝了这次优选（FIXTURE，未做真实测速）' });
            }, 900);
            return;
          }
          var p = 0;
          progress = { percent: 0, message: '准备测速', status: 'RUNNING', sequence: 0 };
          var timer = setInterval(function () {
            p = Math.min(95, p + 17);
            progress = { jobId: 'mock-job', percent: p, message: p < 60 ? '采样延迟' : '比较候选线路', status: 'RUNNING', sequence: progress.sequence + 1 };
          }, 220);
          setTimeout(function () {
            clearInterval(timer);
            progress = { jobId: 'mock-job', percent: 100, message: '测速完成', status: 'SUCCEEDED', sequence: progress.sequence + 1 };
            resolve(result);
          }, 1500);
          return;
        }
        if (command === 'get_progress') {
          if (demo === 'running') { progress.sequence = progress.sequence + 1; }
          resolve(progress);
          return;
        }
        if (command === 'run_action') {
          resolve({ ok: false, status: 'FAILED', message: '浏览器模拟：本版不做系统写操作' });
          return;
        }
        resolve({ ok: false, message: '未知命令：' + command });
      });
    };
  }

  /* ── 启动 ───────────────────────────────────────────── */

  // 浏览器预览专用：等游戏列表就绪后自动点一次「开始优选」，把状态停在 running / failed / mixed。
  function startDemo() {
    log('[FIXTURE] demo=' + demo + '：模拟状态，不是真实网络结果。', 'warn');
    var tries = 0;
    var timer = setInterval(function () {
      if (state.game && !state.running) { clearInterval(timer); optimize(); }
      else if (++tries > 60) { clearInterval(timer); }
    }, 100);
  }

  var hasHost = !!(window.chrome && window.chrome.webview) || !!window.__bbHost || typeof window.cefQuery === 'function';
  if (!hasHost) {
    mock = mockHost();
    log('浏览器模拟 · FIXTURE：当前没有桌面宿主，数据为固定样本。', 'warn');
  }

  renderPlaceholders();
  scan(false);

  if (demo) {
    if (mock) { startDemo(); }
    else { log('demo 参数已忽略：当前有桌面宿主，演示模式只在浏览器预览中可用。', 'warn'); }
  }
})();
