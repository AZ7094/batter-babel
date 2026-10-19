'use strict';
// Production page and DOM event handlers; no network, process or system writes.
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const source = fs.readFileSync(process.env.BB_UI_SOURCE || path.resolve(__dirname, '../src/web/app.js'), 'utf8');
const settle = () => new Promise(resolve => setImmediate(resolve));
const game = { id: 'limbus', name: 'Limbus', installed: true, support: true,
  executablePath: 'C:/fixture/game.exe', optGroups: [{ tag: 'CF', mode: 'cloudflare', domains: ['example.test'] }] };

async function page(answer) {
  const elements = new Map();
  function element() {
    const handlers = {};
    return { handlers, children: [], style: {}, dataset: {}, disabled: false, hidden: false,
      textContent: '', innerHTML: '', value: '', scrollTop: 0, scrollHeight: 100, clientHeight: 100,
      classList: { toggle() {}, add() {}, remove() {} }, setAttribute() {},
      appendChild(child) { this.children.push(child); },
      addEventListener(name, handler) { handlers[name] = handler; }
    };
  }
  const get = id => { if (!elements.has(id)) elements.set(id, element()); return elements.get(id); };
  const timers = new Map();
  const requests = [];
  let timerId = 0;
  const invoke = (command, args = {}) => {
    requests.push({ command, args });
    if (args.action === 'scan') return Promise.resolve({ ok: true, games: [game] });
    if (command === 'start_job') return Promise.resolve({ jobId: 'j1', protocolVersion: 1 });
    return Promise.resolve(answer(command, args));
  };
  vm.runInNewContext(source, {
    window: { chrome: { webview: {} }, __TAURI__: { core: { invoke }, event: { listen: async () => () => {} } } },
    document: { getElementById: get, createElement: element }, location: { search: '' }, console,
    setInterval(handler) { timers.set(++timerId, handler); return timerId; },
    clearInterval(id) { timers.delete(id); }, setTimeout, clearTimeout
  });
  await settle();
  return { get, requests, timers,
    async click(id) { assert.equal(get(id).disabled, false, id + ' should be enabled'); get(id).handlers.click(); await settle(); },
    logs() { return get('log').children.map(row => row.innerHTML).join('\n'); }
  };
}

test('missing or contradictory success confirmation never turns boost on', async () => {
  for (const result of [null, {}, { status: 'SUCCEEDED' }, { ok: true, status: 'FAILED' },
    { ok: true, status: 'CANCELLED' }, { ok: true, status: 'TIMED_OUT' }]) {
    const ui = await page(() => result);
    await ui.click('btn-boost');
    assert.equal(ui.get('boost-label').textContent, '加速游戏', JSON.stringify(result));
    assert.equal(ui.logs().includes('optimize：已加速'), false);
    assert.equal(ui.get('inline-note').hidden, false);
  }
});

test('empty response is not reported as a completed system action', async () => {
  const ui = await page(() => null);
  await ui.click('btn-tune');
  assert.equal(ui.logs().includes('tune-system：已完成'), false);
  assert.equal(ui.get('inline-note').hidden, false);
});

test('hosts recovery remains available after launch without requiring a new optimization', async () => {
  const ui = await page(() => ({ ok: true, message: 'restored' }));
  await ui.click('btn-restore');
  assert.equal(ui.requests.filter(r => r.args.action === 'cf-restore').length, 1);
  assert.equal(ui.requests.some(r => r.args.action === 'cf-optimize'), false);
});

test('confirmed success, business rejection and partial results keep their meaning', async () => {
  for (const result of [{ ok: true, status: 'APPLIED', message: 'confirmed' },
    { ok: false, status: 'FAILED', message: 'denied' },
    { ok: false, status: 'PARTIAL', message: 'partly' },
    { ok: false, status: 'NO_SNAPSHOT', message: 'nothing to restore' }]) {
    const ui = await page(() => result);
    await ui.click('btn-tune');
    assert.equal(ui.logs().includes(result.message), true);
    assert.equal(ui.get('btn-tune').disabled, false);
    assert.equal(ui.get('inline-note').hidden, result.ok);
  }
  const ui = await page(() => ({ ok: true, accelerated: true }));
  await ui.click('btn-boost');
  assert.equal(ui.get('boost-label').textContent, '取消加速');
});

test('slow polling stays single-flight, recovers after rejection and stops after completion', async () => {
  let finish;
  const work = new Promise(resolve => { finish = resolve; });
  const polls = [];
  const ui = await page(command => command === 'get_progress'
    ? new Promise((resolve, reject) => polls.push({ resolve, reject })) : work);
  await ui.click('optimize');
  assert.equal(ui.get('btn-restore').disabled, true, 'recovery is disabled during optimization');
  assert.equal(ui.timers.size, 1);
  const tick = [...ui.timers.values()][0];
  for (let i = 0; i < 10; i++) tick();
  assert.equal(polls.length, 1, '10 ticks while the first request is pending must produce one request');
  polls[0].reject(new Error('temporary transport failure'));
  await settle();
  tick();
  assert.equal(polls.length, 2, 'polling resumes after a rejected request');
  finish({ ok: true, groups: [] });
  await settle();
  assert.equal(ui.timers.size, 0);
  assert.equal(ui.get('btn-restore').disabled, false);
  const finalText = ui.get('pct').textContent;
  polls[1].resolve({ jobId: 'j1', sequence: 5, percent: 12, message: 'late' });
  await settle();
  assert.equal(ui.get('pct').textContent, finalText, 'late progress cannot repaint a finished action');
  tick();
  assert.equal(polls.length, 2);
});
