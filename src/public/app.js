const invoke = window.__TAURI__ && window.__TAURI__.core && window.__TAURI__.core.invoke;

const statusEl = document.querySelector('#status');
const gameSelect = document.querySelector('#game-select');
const optimizeBtn = document.querySelector('#optimize');
const writeBtn = document.querySelector('#write-hosts');
const restoreBtn = document.querySelector('#restore-hosts');
const boostBtn = document.querySelector('#boost-game');
const rescanBtn = document.querySelector('#rescan');
const tuneBtn = document.querySelector('#tune-system');
const restoreTuneBtn = document.querySelector('#restore-tune');
const progressFill = document.querySelector('#progress-fill');
const progressText = document.querySelector('#progress-text');
const cardsEl = document.querySelector('#cards');
const hostsPathEl = document.querySelector('#hosts-path');
const logEl = document.querySelector('#log');

let allGames = [];
let currentGame = null;
let qosCapability = null;
let lastResult = null;
let busy = false;
let hasPendingUpdate = false;

async function invokeAction(args) {
  if (!invoke) throw new Error('桌面后端不可用（请通过桌面应用运行）');
  try {
    return await invoke('run_action', args);
  } catch (err) {
    const msg = typeof err === 'string' ? err : (err && (err.message || err.toString())) || '服务请求失败';
    throw new Error(msg);
  }
}

// Reads the same progress the bb-progress event would carry. Polling through invoke() is the
// reliable path: it shares the channel every other command uses, so the bar still tracks real
// backend work even if the webview's event API is missing.
async function invokeProgress() {
  if (!invoke) return null;
  try {
    return await invoke('get_progress');
  } catch (err) {
    return null;
  }
}

function escapeHtml(value = '') {
  const el = document.createElement('span');
  el.textContent = value;
  return el.innerHTML;
}

function setStatus(msg) { statusEl.textContent = msg; }

// Progress state: real percentages pushed from the backend win; the elapsed-time
// readout keeps the UI alive while PowerShell / UAC is still working.
let progressState = { percent: 0, message: '' };
let timerId = null;
let timerStart = 0;

function renderProgressText() {
  const parts = [];
  if (progressState.message) parts.push(progressState.message);
  parts.push(Math.round(progressState.percent) + '%');
  if (timerId) parts.push(Math.round((Date.now() - timerStart) / 1000) + 's');
  progressText.textContent = parts.join(' · ');
}

function paintProgress() {
  progressFill.style.width = progressState.percent + '%';
  progressFill.classList.toggle('busy', progressState.percent > 0 && progressState.percent < 100);
  renderProgressText();
}

function setProgress(pct, text) {
  progressState.percent = Math.max(0, Math.min(100, pct));
  if (typeof text === 'string') progressState.message = text;
  paintProgress();
}

// Apply a progress payload from either source (event or poll). Percentages only ever move
// forward, so the two paths cannot make the bar jump backwards.
function applyProgress(payload) {
  if (!payload) return;
  const percent = typeof payload.percent === 'number' ? payload.percent : null;
  if (percent !== null && percent > progressState.percent) {
    progressState.percent = Math.max(0, Math.min(100, percent));
  }
  if (payload.message) {
    const zh = localizeProgress(payload.message);
    if (zh) progressState.message = zh;
  }
  paintProgress();
}

let pollId = null;
let pollInFlight = false;

async function pollProgressOnce() {
  if (pollInFlight) return;
  pollInFlight = true;
  try {
    const payload = await invokeProgress();
    applyProgress(payload);
  } finally {
    pollInFlight = false;
  }
}

function startProgressPolling() {
  stopProgressPolling();
  pollId = setInterval(pollProgressOnce, 250);
}

function stopProgressPolling() {
  if (pollId) { clearInterval(pollId); pollId = null; }
}

function startTimer(label) {
  stopTimer();
  progressState.message = label;
  progressState.percent = 0;
  timerStart = Date.now();
  paintProgress();
  timerId = setInterval(renderProgressText, 1000);
  startProgressPolling();
}

function stopTimer() {
  if (timerId) { clearInterval(timerId); timerId = null; }
  stopProgressPolling();
}

// The PowerShell backend must stay pure ASCII, so it reports progress in English.
// Translate the known templates here so the status line reads naturally in Chinese.
const PROGRESS_ZH = [
  [/^Preparing (.+)$/, '准备中：$1'],
  [/^Starting (.+)$/, '开始：$1'],
  [/^Cloudflare: testing (\d+) candidate IPs$/, 'Cloudflare 候选 IP $1 个，开始测速'],
  [/^Latency screening: (\d+)\/(\d+) IPs$/, '延迟筛选 $1/$2 个候选'],
  [/^Download test on (\d+) IPs \((\d+) curated \+ (\d+) learned \+ (\d+) fastest\)$/, '下载测速 $1 个 IP（精选 $2 + 已学习 $3 + 延迟最快 $4）'],
  [/^Cloudflare download test: (\d+)\/(\d+) IPs$/, 'Cloudflare 下载测速 $1/$2'],
  [/^Resolving (.+)$/, '解析 $1'],
  [/^Probing (.+) \((\d+)\/(\d+)\)$/, '探测 $1（$2/$3 轮）'],
  [/^Done$/, '完成'],
  [/^Snapshotting current settings$/, '读取当前设置'],
  [/^Background bandwidth check$/, '后台占带宽检查'],
  [/^Applying network tier (\S+)$/, '套用网络参数档位 $1'],
  [/^Restoring (.+) to its original value$/, '还原 $1 为原始值'],
  [/^Writing game config (.+)$/, '写入游戏配置 $1'],
  [/^Removing Batter Babel blocks$/, '移除 Batter Babel 配置块'],
  [/^Restoring game config (.+)$/, '还原游戏配置 $1'],
  [/^System tune complete$/, '系统调优完成'],
  [/^Loading snapshot$/, '读取快照'],
  [/^Restoring TCP low latency$/, '还原 TCP 低延迟'],
  [/^Restoring TCP globals$/, '还原 TCP 全局参数'],
  [/^Restoring adapter properties$/, '还原网卡属性'],
  [/^Restoring adapter power management$/, '还原网卡电源管理'],
  [/^Restoring network throttling$/, '还原系统网络限流'],
  [/^Restoring Wi-Fi power mode$/, '还原 Wi-Fi 电源模式'],
  [/^System restore complete$/, '系统还原完成'],
  [/^Validating mappings$/, '校验映射'],
  [/^Hosts updated$/, 'hosts 已更新'],
  [/^Restoring hosts$/, '还原 hosts'],
  [/^Hosts restored$/, 'hosts 已还原'],
];

function localizeProgress(message) {
  for (const [re, zh] of PROGRESS_ZH) {
    const m = message.match(re);
    if (m) return zh.replace(/\$(\d)/g, (_, i) => (m[Number(i)] === undefined ? '' : m[Number(i)]));
  }
  return message;
}

// Two independent paths carry the same numbers: this event listener (low latency) and the
// get_progress poll started by startTimer (works even without the event API). Both feed
// applyProgress, which only lets the percentage move forward.
async function setupProgressListener() {
  const listen = window.__TAURI__ && window.__TAURI__.event && window.__TAURI__.event.listen;
  if (!listen) {
    appendLog('提示：事件接口不可用，进度将通过轮询获取。');
    return;
  }
  try {
    await listen('bb-progress', (event) => {
      applyProgress((event && event.payload) || {});
    });
  } catch (err) {
    // Events unavailable: the elapsed-time readout still keeps the user informed.
  }
}

function appendLog(lines) {
  for (const line of (Array.isArray(lines) ? lines : [lines])) {
    const div = document.createElement('div');
    div.className = 'log-line';
    div.textContent = line;
    logEl.appendChild(div);
  }
  logEl.scrollTop = logEl.scrollHeight;
}

function clearLog() { logEl.innerHTML = ''; }

// Why a probe target could not be used. The backend reports a structured reason (timeout vs
// certificate vs HTTP status vs business content) instead of collapsing everything into one
// "fallback DNS" line, so the UI can say what actually went wrong.
const PROBE_REASONS = {
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
  DeadlineExceeded: '超过时间上限未测完',
};

function probeReason(reason) {
  if (!reason) return '未知原因';
  if (PROBE_REASONS[reason]) return PROBE_REASONS[reason];
  const http = /^HttpStatus(\d{3})$/.exec(reason);
  if (http) return `HTTP ${http[1]}`;
  const curl = /^CurlExit(\d+)$/.exec(reason);
  if (curl) return `连接错误（curl ${curl[1]}）`;
  return reason;
}

function cardHtml(tag, label, domains, resultHtml, cls) {
  return `<article class="card" data-tag="${escapeHtml(tag || '')}">
      <h3>${escapeHtml(label || '')}</h3>
      <p class="domains">${escapeHtml(domains || '')}</p>
      <p class="result ${cls}">${resultHtml}</p>
    </article>`;
}

// Shown whenever ranking routes cannot improve the game experience itself. This is about
// APPLICABILITY, not genre: CS2/Apex/Hunt (UDP to assigned servers) and Don't Starve / Terraria
// (P2P between players) all qualify -- none of them talk to a rankable domain in-game.
// It also covers in-game DOWNLOADS (maps / mods / save syncing): Steam fetches content servers by
// IP after asking its Connection Manager, and P2P transfers never involve a domain at all, so
// hosts entries cannot speed those up either. The effective control is Steam's download region.
function scopeNotice() {
  if (!currentGame || currentGame.helpsGameplay) return '';
  const platformLine = currentGame.platform
    ? `本次优选加速的是<b>${currentGame.vendor}</b> 平台的登录 / 更新 / 好友 / 成就 / 商店，`
    : '本次优选加速的是<b>游戏平台与官方服务</b>：登录 / 更新 / 大厅 / 好友 / 成就 / 商店，';
  return '<div class="card notice">'
    + '<b>⚠️ 本游戏无法通过 hosts 优选改善的部分：</b><br>'
    + '• <b>游戏内延迟</b> —— 联机走 UDP 直连服务器或玩家主机（P2P），不经过域名。<br>'
    + '• <b>游戏内下载</b>（地图 / mod / 存档同步）—— Steam 内容服务器是「问一次 IP 后直连」，P2P 传输更是没有域名。<br>'
    + platformLine + '对局与地图本身不受影响。<br>'
    + '想加速 Steam 下载请用官方开关：<b>Steam → 设置 → 下载 → 下载区域</b>（比任何 hosts 优化都有效）。<br>'
    + '想降低游戏内延迟请用「<b>加速游戏</b>」（本地 QoS 优先级）。'
    + '</div>';
}

function renderPlaceholderCards() {
  const groups = (currentGame && currentGame.optGroups) || [];
  const notice = scopeNotice();
  if (!groups.length) {
    cardsEl.innerHTML = notice + '<div class="card placeholder">' + noTargetsHtml() + '</div>';
    return;
  }
  cardsEl.innerHTML = notice + groups.map(g => cardHtml(g.tag, g.label, (g.domains || []).join(' / '), '—', 'pending')).join('');
}

// Shown for games that end up with nothing to rank. Steam's own endpoints are no longer optimised
// (almost everyone already runs a Steam accelerator, and Steam's download region is the real
// control), so a Steam-only title legitimately has no routes left -- say so instead of leaving the
// user wondering whether detection failed.
function noTargetsHtml() {
  return '<b>该游戏没有可优选的线路。</b><br>'
    + '<span style="font-size:12px">Steam 自身的域名（登录 / 下载 CDN）已不再优选——大多数人本来就用加速器加速 Steam，'
    + '而 Steam 下载速度的官方控制项是「设置 → 下载 → 下载区域」。<br>'
    + '这个游戏请用「<b>加速游戏</b>」（本机 QoS 优先级）来改善对局延迟。</span>';
}

function renderResult(data) {
  const groups = data.groups || [];
  if (!groups.length) {
    cardsEl.innerHTML = '<div class="card placeholder">没有优选结果。</div>';
    return;
  }
  cardsEl.innerHTML = groups.map(g => {
    const domains = (g.domains || []).join(' / ');
    let resultHtml;
    let cls;
    if (g.mode === 'cloudflare') {
      if (g.bestIp) {
        resultHtml = `${escapeHtml(g.bestIp)}  (${g.downloadMbps} MB/s, ${g.latency} ms)`;
        cls = 'good';
      } else {
        resultHtml = '未测到可用 IP';
        cls = 'fallback';
      }
    } else {
      const items = g.items || [];
      resultHtml = items.map(it => it.ok
        ? `<span class="line">${escapeHtml(it.domain)} → ${escapeHtml(it.ip)}（${it.latency} ms）</span>`
        : `<span class="line bad">${escapeHtml(it.domain)} → 无可用线路（${escapeHtml(probeReason(it.reason))}）</span>`).join('');
      cls = items.some(it => it.ok) ? 'good' : 'fallback';
    }
    return cardHtml(g.tag, g.label, domains, resultHtml, cls);
  }).join('');
}

// Only INSTALLED games are listed. The backend already restricts the list to games that Steam's
// local metadata flags as having an online component, so this is the final filter: a game you do
// not have installed (and therefore cannot launch) has no business appearing in the picker.
// Conversely, anyone who installs Battlefield or Rainbow Six Siege sees it here automatically.
function computeGameList() {
  return allGames.filter(g => g.installed);
}

function renderGameOptions() {
  const list = computeGameList();
  const previous = gameSelect.value;
  gameSelect.innerHTML = '';
  if (!list.length) {
    gameSelect.innerHTML = '<option>未检测到游戏</option>';
    renderPlaceholderCards();
    return;
  }
  for (const g of list) {
    const opt = document.createElement('option');
    opt.value = g.id;
    const tags = [];
    if (g.standalone) tags.push('独立客户端');
    else if (!g.installed) tags.push('未安装');
    opt.textContent = g.name + (tags.length ? `（${tags.join('，')}）` : '');
    gameSelect.appendChild(opt);
  }
  const stillThere = list.some(g => g.id === previous);
  if (stillThere) {
    gameSelect.value = previous;
  } else {
    const preferred = list.find(g => g.id === 'limbus' && g.installed)
      || list.find(g => g.support && g.installed)
      || list.find(g => g.support)
      || list[0];
    gameSelect.value = preferred.id;
  }
  onGameChange();
}

async function loadGames(force) {
  try {
    // The backend caches the scan for 24 h, so a normal launch is instant. The 检测游戏 button passes
    // force=true, which makes the backend rescan from disk.
    if (force) setStatus('正在重新检测已安装的游戏…');
    const data = await invokeAction({ action: force ? 'scan-force' : 'scan' });
    allGames = data.games || [];
    qosCapability = data.qos || null;
    renderGameOptions();
    reportQosCapability();
    if (force) {
      const count = allGames.filter(g => g.installed).length;
      appendLog(`=== 检测游戏 ===`);
      appendLog(`重新扫描完成：${count} 个已安装的联网游戏${data.scannedAt ? '（' + new Date(data.scannedAt).toLocaleString() + '）' : ''}。`);
      setStatus(`检测完成，共 ${count} 个已安装的联网游戏。`);
    }
  } catch (err) {
    gameSelect.innerHTML = '<option>检测失败</option>';
    appendLog('游戏检测失败：' + err.message);
  }
}

// Tell the user up front what the boost can do on this machine, so nothing comes as a surprise
// after clicking. Home editions have no Group Policy QoS store: the rule still works, but it lives
// in the active store and is re-applied by a logon task after each restart.
function reportQosCapability() {
  if (!qosCapability) return;
  if (qosCapability.available === false) {
    appendLog('注意：' + (qosCapability.reason || '本机不支持 QoS 加速。'));
    appendLog('「加速游戏」可能无法生效；请检查网卡属性里是否勾选了「QoS 数据包计划程序」。');
  } else if (qosCapability.persistent === false) {
    appendLog('提示：本机 Windows 没有组策略 QoS 存储，加速规则将写入活动存储，重启后由计划任务自动恢复。');
  }
}

function onGameChange() {
  currentGame = allGames.find(g => g.id === gameSelect.value) || null;
  lastResult = null;
  hasPendingUpdate = false;
  writeBtn.disabled = true;
  setProgress(0, '等待开始');
  renderBoostButton();
  renderOptimizeButton();
  if (!currentGame) {
    cardsEl.innerHTML = '<div class="card placeholder">未检测到游戏。</div>';
    return;
  }
  if (!currentGame.support) {
    cardsEl.innerHTML = '<div class="card placeholder">该游戏没有可优选的线路，优选对它无效。<br>请改用「加速游戏」（QoS 优先级）或「系统调优」。</div>';
    setStatus(`${currentGame.name} 没有可优选的线路，「开始优选」已禁用；可用「加速游戏」或「系统调优」。`);
    return;
  }
  renderPlaceholderCards();
  if (currentGame.platform && !currentGame.helpsGameplay) {
    // A Steam copy of a publisher game gets both endpoint sets; name both so the user can see what
    // is actually being ranked.
    const extras = (currentGame.extraPlatforms || []).filter(Boolean);
    const platformLabel = extras.length
      ? `${currentGame.platform} + ${extras.join(' + ')}`
      : currentGame.platform;
    setStatus(`已选择 ${currentGame.name}（${platformLabel} 平台）：优选将加速该平台的登录 / 更新 / 好友等服务。`);
  } else if (!currentGame.helpsGameplay) {
    setStatus(`已选择 ${currentGame.name}：优选只加速游戏平台与官方服务，无法改善游戏内延迟。`);
  } else {
    setStatus(`已选择 ${currentGame.name}，点击「开始优选」测速其 CDN / API 线路。`);
  }
}

// One button that flips between 加速游戏 and 取消游戏加速 based on the live QoS rule state.
// Games without a profile (support = false) have no known executable, so they cannot be boosted
// safely -- the button explains that instead of guessing which exe to tag.
function renderBoostButton() {
  const on = !!(currentGame && currentGame.accelerated);
  boostBtn.textContent = on ? '取消游戏加速' : '加速游戏';
  boostBtn.classList.toggle('active', on);
  boostBtn.disabled = !currentGame || !currentGame.installed || !currentGame.support || busy;
  if (!currentGame) {
    boostBtn.title = '请先选择一个游戏';
  } else if (currentGame.standalone) {
    boostBtn.title = '独立客户端（非 Steam），无法自动定位可执行文件，暂不支持 QoS 加速；优选仍可使用';
  } else if (!currentGame.installed) {
    boostBtn.title = '该游戏未安装';
  } else if (!currentGame.support) {
    boostBtn.title = '该游戏还没有配置可执行文件，暂时无法创建 QoS 规则；可先用「系统调优」';
  } else {
    boostBtn.title = on
      ? '移除该游戏的 QoS 加速规则'
      : '为该游戏创建 QoS (DSCP 46) 加速规则，需要管理员授权';
  }
}

async function toggleBoost() {
  if (busy || !currentGame || !currentGame.installed) return;
  const turningOff = !!currentGame.accelerated;
  busy = true;
  boostBtn.disabled = true;
  optimizeBtn.disabled = true;
  setProgress(15, turningOff ? '取消加速中' : '加速中');
  startTimer(turningOff ? '取消加速中' : '加速中');
  setStatus(turningOff
    ? '正在取消游戏加速…如弹出「用户账户控制」窗口，请点「是」。'
    : '正在为该游戏创建 QoS 加速规则…如弹出「用户账户控制」窗口，请点「是」。');
  clearLog();
  appendLog(turningOff ? '=== 取消游戏加速 ===' : '=== 加速游戏 ===');
  try {
    const data = await invokeAction({
      action: turningOff ? 'restore' : 'optimize',
      gameId: currentGame.id,
    });
    if (data.ok === false) {
      appendLog(data.message || '操作未完成。');
      if (data.hint) appendLog('提示：' + data.hint);
      appendLog('（QoS 加速依赖网卡的「QoS 数据包计划程序」组件与系统 WMI 服务；两者异常时该功能不可用）');
      setStatus(data.message || '操作未完成（可能未授权或系统不支持 QoS），详见日志。');
    } else {
      currentGame.accelerated = !turningOff;
      if (data.executablePath) appendLog('目标程序：' + data.executablePath);
      appendLog(data.message || (turningOff ? '已取消游戏加速。' : '游戏加速已启用。'));
      appendLog(turningOff
        ? '（已移除该游戏的 QoS 规则，hosts 与 DNS 未受影响）'
        : '（QoS 仅匹配该游戏的可执行文件，DSCP 46 优先级，不影响其它程序）');
      // Home editions of Windows have no Group Policy QoS store, so the rule lands in ActiveStore
      // and disappears on reboot. Say so instead of letting the user think it is permanent.
      const notPersistent = !turningOff && data.persistent === false;
      if (notPersistent) {
        appendLog('注意：本机 Windows 没有组策略 QoS 存储（家庭版），规则只能写入活动存储。');
        appendLog('它对本次开机全程有效，但重启后需要回来重新点一次「加速游戏」。');
      }
      setProgress(100, turningOff ? '已取消加速' : '加速已启用');
      setStatus(turningOff
        ? '已取消游戏加速。'
        : (notPersistent
          ? '游戏加速已启用（本次开机有效，重启后需重新应用）。'
          : '游戏加速已启用，可点击按钮取消。'));
    }
  } catch (err) {
    appendLog('操作失败：' + err.message);
    setStatus('操作失败，详见日志。');
  } finally {
    stopTimer();
    busy = false;
    renderOptimizeButton();
    renderBoostButton();
  }
}

// 开始优选 only makes sense for games that actually have routes to rank. Games with no profile
// (support = false) get the button disabled with a reason, instead of letting the user run a
// benchmark that cannot produce anything.
function renderOptimizeButton() {
  const can = !!(currentGame && currentGame.support);
  optimizeBtn.disabled = !can || busy;
  if (!currentGame) {
    optimizeBtn.title = '请先选择一个游戏';
  } else if (!can) {
    optimizeBtn.title = `${currentGame.name} 没有可优选的线路，优选对它无效；请改用「加速游戏」或「系统调优」`;
  } else {
    optimizeBtn.title = `测试 ${currentGame.name} 的 CDN / API 线路并排序`;
  }
}

async function optimize() {
  if (busy || !currentGame || !currentGame.support) return;
  busy = true;
  optimizeBtn.disabled = true;
  writeBtn.disabled = true;
  hasPendingUpdate = false;
  lastResult = null;
  const groups = currentGame.optGroups || [];
  cardsEl.innerHTML = groups.map(g => cardHtml(g.tag, g.label, (g.domains || []).join(' / '), '测速中…', 'pending')).join('')
    || '<div class="card placeholder">' + noTargetsHtml() + '</div>';
  setProgress(15, '准备测速');
  startTimer('测速中');
  clearLog();
  setStatus(`正在测速 ${currentGame.name} 的线路…（需 1-2 分钟，进度条会实时推进，请稍候）`);
  try {
    const data = await invokeAction({ action: 'cf-optimize', gameId: currentGame.id });
    appendLog(data.log || []);
    if (data.ok === false) {
      // The backend refused the run (unknown profile, PowerShell error, …): say so instead of
      // rendering an empty result and claiming success.
      setProgress(0, '未完成');
      appendLog(data.message || '优选未完成。');
      setStatus(data.message || '优选未完成，详见日志。');
      return;
    }
    renderResult(data);
    lastResult = data;
    hasPendingUpdate = (data.groups || []).some(g => g.ok);
    writeBtn.disabled = !hasPendingUpdate;
    if (data.hostsPath) hostsPathEl.textContent = 'hosts: ' + data.hostsPath;
    setProgress(100, '测速完成');
    if (hasPendingUpdate) {
      if (!currentGame.helpsGameplay) {
        appendLog('注意：本游戏的游戏内延迟与游戏内下载（地图/mod/存档同步）都不经过域名，优选无法改善。');
        appendLog('本次优选加速的是游戏平台与官方服务（登录 / 更新 / 大厅 / 好友 / 成就 / 商店）。');
        appendLog('加速 Steam 下载请用官方开关：Steam → 设置 → 下载 → 下载区域。');
        appendLog('降低游戏内延迟请用「加速游戏」（本地 QoS DSCP 46）。');
        setStatus('优选完成（只加速平台与官方服务）。游戏内延迟/下载请见提示。');
      } else {
        setStatus('优选完成。可点击「写入 hosts」应用结果。');
      }
    } else {
      setStatus('优选完成，但没有可用结果。');
    }
  } catch (err) {
    appendLog('出错：' + err.message);
    setStatus('优选过程中出错，详见日志。');
  } finally {
    stopTimer();
    busy = false;
    renderOptimizeButton();
  }
}

function collectTriples(data) {
  const triples = [];
  for (const g of ((data && data.groups) || [])) {
    if (g.mode === 'cloudflare') {
      if (g.bestIp) for (const d of (g.domains || [])) triples.push(`${g.tag}|${g.bestIp}|${d}`);
    } else {
      for (const it of (g.items || [])) if (it.ok) triples.push(`${g.tag}|${it.ip}|${it.domain}`);
    }
  }
  return triples;
}

async function writeHosts() {
  if (!hasPendingUpdate || busy) return;
  const triples = collectTriples(lastResult);
  if (!triples.length) { setStatus('没有可写入的映射。'); return; }
  busy = true;
  writeBtn.disabled = true;
  restoreBtn.disabled = true;
  setProgress(20, '写入中');
  startTimer('写入中');
  setStatus('正在写入 hosts…如弹出「用户账户控制」窗口，请点「是」。');
  try {
    const data = await invokeAction({ action: 'cf-apply', domains: triples.join(';') });
    appendLog(['=== 写入 hosts ===', data.message]);
    if (data.ok === false) {
      // e.g. the UAC prompt was dismissed. Keep the pending result so the user can retry instead
      // of falsely reporting success and disabling the button.
      setProgress(0, '未写入');
      setStatus(data.message || '写入 hosts 未完成（可能未授权），可重试。');
    } else {
      hasPendingUpdate = false;
      writeBtn.disabled = true;
      setProgress(100, '写入完成');
      setStatus(data.message);
    }
  } catch (err) {
    setProgress(0, '已停止');
    appendLog('写入失败：' + err.message);
    setStatus('写入 hosts 失败，详见日志。');
  } finally {
    stopTimer();
    busy = false;
    restoreBtn.disabled = false;
  }
}

async function restoreHosts() {
  if (busy) return;
  busy = true;
  restoreBtn.disabled = true;
  setProgress(20, '还原中');
  startTimer('还原中');
  setStatus('正在还原 hosts…如弹出「用户账户控制」窗口，请点「是」。');
  try {
    const data = await invokeAction({ action: 'cf-restore' });
    appendLog(['=== 还原 hosts ===', data.message]);
    if (data.ok === false) {
      setProgress(0, '未还原');
      setStatus(data.message || '还原 hosts 未完成（可能未授权），可重试。');
    } else {
      hasPendingUpdate = false;
      writeBtn.disabled = true;
      setProgress(100, '还原完成');
      setStatus(data.message);
    }
  } catch (err) {
    setProgress(0, '已停止');
    appendLog('还原失败：' + err.message);
    setStatus('还原 hosts 失败，详见日志。');
  } finally {
    stopTimer();
    busy = false;
    restoreBtn.disabled = false;
  }
}

async function tuneSystem() {
  if (busy) return;
  busy = true;
  tuneBtn.disabled = true;
  restoreTuneBtn.disabled = true;
  setProgress(10, '调优中');
  startTimer('调优中');
  setStatus('正在执行系统调优…如弹出「用户账户控制」窗口，请点「是」。');
  clearLog();
  appendLog('=== 系统调优 ===');
  try {
    // The tune button is deliberately available with no game selected -- the UI offers 系统调优 as
    // the fallback when a game has no QoS profile -- so the generic path must send no gameId at
    // all. The backend rejects an empty string (is_valid_game_id requires a non-empty id), while an
    // absent key arrives as None and skips the check, which is how this worked before.
    const payload = { action: 'tune-system' };
    if (currentGame) payload.gameId = currentGame.id;
    const data = await invokeAction(payload);
    for (const item of (data.items || [])) {
      appendLog(`${item.ok ? '[OK]' : '[!]'} ${item.title}: ${item.detail}`);
    }
    appendLog(data.log || []);
    if (data.ok === false) {
      setProgress(0, '未完成');
      setStatus(data.message || '系统调优未完成（可能未授权），可重试。');
    } else {
      const bad = (data.items || []).filter(it => !it.ok).length;
      setProgress(100, '调优完成');
      setStatus(bad ? `系统调优完成，${bad} 项需要留意（详见日志）。` : '系统调优完成，全部成功。');
    }
  } catch (err) {
    setProgress(0, '已停止');
    appendLog('系统调优失败：' + err.message);
    setStatus('系统调优失败，详见日志。');
  } finally {
    stopTimer();
    busy = false;
    tuneBtn.disabled = false;
    restoreTuneBtn.disabled = false;
  }
}

async function restoreTune() {
  if (busy) return;
  busy = true;
  restoreTuneBtn.disabled = true;
  tuneBtn.disabled = true;
  setProgress(10, '还原中');
  startTimer('还原中');
  setStatus('正在还原系统调优…如弹出「用户账户控制」窗口，请点「是」。');
  clearLog();
  appendLog('=== 还原系统调优 ===');
  try {
    const data = await invokeAction({ action: 'restore-tune' });
    if (data.ok === false) {
      appendLog(data.log || []);
      appendLog(data.message || '没有找到可还原的快照。');
      setProgress(0, '无可还原');
      setStatus(data.message || '没有找到可还原的快照，请先执行一次系统调优。');
    } else {
      for (const item of (data.items || [])) {
        appendLog(`${item.ok ? '[OK]' : '[!]'} ${item.title}: ${item.detail}`);
      }
      appendLog(data.log || []);
      setProgress(100, '还原完成');
      setStatus('系统调优已还原为原始设置。');
    }
  } catch (err) {
    setProgress(0, '已停止');
    appendLog('还原系统调优失败：' + err.message);
    setStatus('还原系统调优失败，详见日志。');
  } finally {
    stopTimer();
    busy = false;
    restoreTuneBtn.disabled = false;
    tuneBtn.disabled = false;
  }
}

gameSelect.addEventListener('change', onGameChange);
optimizeBtn.addEventListener('click', optimize);
writeBtn.addEventListener('click', writeHosts);
restoreBtn.addEventListener('click', restoreHosts);
tuneBtn.addEventListener('click', tuneSystem);
restoreTuneBtn.addEventListener('click', restoreTune);
boostBtn.addEventListener('click', toggleBoost);
rescanBtn.addEventListener('click', async () => {
  if (busy) return;
  busy = true;
  rescanBtn.disabled = true;
  try {
    await loadGames(true);
  } finally {
    busy = false;
    rescanBtn.disabled = false;
  }
});

setupProgressListener();
loadGames(false);
