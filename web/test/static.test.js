import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, readFileSync, readdirSync, rmSync, existsSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, relative } from 'node:path';
import { request } from 'node:http';
import { build, verify } from '../scripts/build.mjs';
import { makeServer } from '../scripts/serve.mjs';
import { BOX_CSP, BOX_PERMISSIONS_POLICY } from '../scripts/headers.mjs';
import { WEB_DIR } from '../testlib/helpers.js';

const SRC = join(WEB_DIR, 'src');
const walk = (d) => readdirSync(d, { withFileTypes: true }).flatMap((e) => (e.isDirectory() ? walk(join(d, e.name)) : [join(d, e.name)]));
const srcFiles = walk(SRC);
const stripComments = (s) => s.replace(/\/\*[\s\S]*?\*\//g, '').replace(/(^|[^:'"`\\])\/\/.*$/gm, '$1');

test('源码里没有网络调用/Worker/动态代码/innerHTML（零外联，解析与存储都在主线程）', () => {
  const banned = [
    [/\bfetch\s*\(/, 'fetch'], [/\bXMLHttpRequest\b/, 'XMLHttpRequest'], [/\bsendBeacon\b/, 'sendBeacon'],
    [/\bWebSocket\b/, 'WebSocket'], [/\bEventSource\b/, 'EventSource'], [/\bimportScripts\b/, 'importScripts'],
    [/\bnew\s+(Shared)?Worker\b/, 'Worker'], [/\bserviceWorker\b/, 'serviceWorker'], [/\beval\s*\(/, 'eval'],
    [/\bnew\s+Function\b/, 'new Function'], [/\binnerHTML\b/, 'innerHTML'], [/\bouterHTML\b/, 'outerHTML'],
    [/\binsertAdjacentHTML\b/, 'insertAdjacentHTML'], [/\bdocument\.write\b/, 'document.write'],
    [/\bimport\s*\(/, 'dynamic import'], [/https?:\/\//, 'absolute URL'], [/\blocalStorage\b|\bsessionStorage\b|document\.cookie/, 'other storage'],
  ];
  for (const f of srcFiles.filter((x) => /\.(js|html)$/.test(x))) {
    const s = stripComments(readFileSync(f, 'utf8'));
    for (const [re, name] of banned) assert.doesNotMatch(s, re, `${relative(SRC, f)} 含有 ${name}`);
  }
});

test('HTML：无内联脚本/样式/事件属性/<base>；脚本只有同源模块；样式只有同源外链', () => {
  for (const f of srcFiles.filter((x) => x.endsWith('.html'))) {
    const h = readFileSync(f, 'utf8');
    const name = relative(SRC, f);
    assert.doesNotMatch(h, /<script(?![^>]*\bsrc=)/i, `${name} 内联 script`);
    assert.doesNotMatch(h, /<style\b/i, `${name} 内联 style`);
    assert.doesNotMatch(h, /\son[a-z]+\s*=/i, `${name} 事件属性`);
    assert.doesNotMatch(h, /\sstyle\s*=/i, `${name} style 属性`);
    assert.doesNotMatch(h, /<base\b|<iframe|<object|<embed|<form\b/i, `${name} 禁用元素`);
    for (const m of h.matchAll(/<script[^>]*\bsrc="([^"]+)"/g)) assert.match(m[1], /^\/box\/[\w./-]+\.js$/);
    for (const m of h.matchAll(/<link[^>]*\bhref="([^"]+)"/g)) assert.match(m[1], /^(\/box\/[\w./-]+\.css|data:,)$/);
  }
});

test('CSS 里没有 @import / 外部 url()', () => {
  const css = readFileSync(join(SRC, 'style.css'), 'utf8');
  assert.doesNotMatch(css, /@import|url\s*\(/i);
});

test('蓝牙请求只在点击处理器里发起（requestDevice 只出现在 ble.js，main.js 同步调用 requestAndConnect）', () => {
  const uses = srcFiles.filter((f) => f.endsWith('.js') && !f.includes('/i18n/')).filter((f) => /requestDevice/.test(stripComments(readFileSync(f, 'utf8'))));
  assert.deepEqual(uses.map((f) => relative(SRC, f)), ['core/ble.js']);
  const main = readFileSync(join(SRC, 'main.js'), 'utf8');
  assert.match(main, /\$\('btn-connect'\)\.addEventListener\('click', \(\) => \{ S\.ctl\.requestAndConnect\(/);
});

test('页面必须带的诚实标注：实验性 / 演示数据 / 限制说明', async () => {
  const zh = (await import('../src/i18n/zh.js')).default;
  const en = (await import('../src/i18n/en.js')).default;
  assert.equal(zh['exp.label'], '实验性（仅按规范实现，未真机验证）');
  assert.equal(zh['tag.demo'], '演示数据');
  assert.match(zh['limits.item6'], /实验性（仅按规范实现，未真机验证）/);
  assert.match(zh['limits.item5'], /暂未支持/);
  assert.match(en['limits.item6'], /not verified on real hardware/);
  const h = readFileSync(join(SRC, 'index.html'), 'utf8');
  assert.match(h, /data-i18n="limits\.heading"/);
  assert.match(zh['limits.heading'], /兼容性与限制/);
  assert.match(h, /data-i18n="exp\.label"/);
  assert.match(h, /data-i18n="demo\.tag"/);
});

test('构建：内容哈希文件名、import 改写、产物自检通过；同一输入两次构建结果一致', () => {
  const a = mkdtempSync(join(tmpdir(), 'sbweb-a-'));
  const b = mkdtempSync(join(tmpdir(), 'sbweb-b-'));
  try {
    build({ outDir: a, commit: 'testcommit' });
    build({ outDir: b, commit: 'testcommit' });
    const names = walk(a).map((f) => relative(a, f)).sort();
    assert.deepEqual(names, walk(b).map((f) => relative(b, f)).sort());
    for (const n of names) assert.ok(readFileSync(join(a, n)).equals(readFileSync(join(b, n))), `不确定的输出：${n}`);
    assert.ok(names.some((n) => /^main\.[0-9a-f]{10}\.js$/.test(n)));
    assert.ok(names.some((n) => /^style\.[0-9a-f]{10}\.css$/.test(n)));
    assert.ok(names.includes('index.html') && names.includes('privacy.html') && names.includes('build-info.json'));
    const html = readFileSync(join(a, 'index.html'), 'utf8');
    assert.match(html, /src="\/box\/main\.[0-9a-f]{10}\.js"/);
    assert.match(html, /href="\/box\/style\.[0-9a-f]{10}\.css"/);
    const info = JSON.parse(readFileSync(join(a, 'build-info.json'), 'utf8'));
    assert.equal(info.commit, 'testcommit');
    assert.ok(info.files.every((f) => /^[0-9a-f]{64}$/.test(f.sha256)));
    const main = readFileSync(join(a, names.find((n) => /^main\./.test(n))), 'utf8');
    assert.match(main, /from '\.\/core\/ble\.[0-9a-f]{10}\.js'/);
    assert.doesNotMatch(main, /from '\.\/core\/ble\.js'/);
    verify(a);
  } finally { rmSync(a, { recursive: true, force: true }); rmSync(b, { recursive: true, force: true }); }
});

test('哈希随内容变：改一个叶子模块，依赖它的所有入口的哈希都变', () => {
  const a = mkdtempSync(join(tmpdir(), 'sbweb-c-'));
  const b = mkdtempSync(join(tmpdir(), 'sbweb-d-'));
  try {
    build({ outDir: a, commit: 'one' });
    build({ outDir: b, commit: 'two' }); // build-info.js 是 main.js 的依赖，commit 变了 → main.js 哈希变
    const m = (d) => readdirSync(d).find((n) => /^main\..*\.js$/.test(n));
    assert.notEqual(m(a), m(b));
    const dec = (d) => readdirSync(join(d, 'core')).find((n) => /^decimal\./.test(n));
    assert.equal(dec(a), dec(b), '未改动的模块哈希不变');
  } finally { rmSync(a, { recursive: true, force: true }); rmSync(b, { recursive: true, force: true }); }
});

function get(port, path, method = 'GET') {
  return new Promise((resolve, reject) => {
    const r = request({ host: '127.0.0.1', port, path, method }, (res) => {
      const chunks = [];
      res.on('data', (c) => chunks.push(c));
      res.on('end', () => resolve({ status: res.statusCode, headers: res.headers, body: Buffer.concat(chunks).toString('utf8') }));
    });
    r.on('error', reject);
    r.end();
  });
}

test('本地预览服务器：/box 与 /box/ 均 200，带 CSP 与 bluetooth=(self)；哈希文件长缓存、HTML no-cache；路径穿越不可用', async () => {
  const dir = mkdtempSync(join(tmpdir(), 'sbweb-e-'));
  build({ outDir: dir, commit: 'x' });
  const srv = makeServer(dir);
  await new Promise((r) => srv.listen(0, '127.0.0.1', r));
  const port = srv.address().port;
  try {
    for (const p of ['/box', '/box/', '/box/index.html', '/box/privacy.html']) {
      const r = await get(port, p);
      assert.equal(r.status, 200, p);
      assert.equal(r.headers['content-security-policy'], BOX_CSP);
      assert.equal(r.headers['permissions-policy'], BOX_PERMISSIONS_POLICY);
      assert.match(r.headers['permissions-policy'], /bluetooth=\(self\)/);
      assert.equal(r.headers['cache-control'], 'no-cache');
    }
    const css = readdirSync(dir).find((n) => /^style\./.test(n));
    const r = await get(port, `/box/${css}`);
    assert.equal(r.headers['cache-control'], 'public, max-age=31536000, immutable');
    assert.match(r.headers['content-type'], /text\/css/);
    for (const p of ['/box/../package.json', '/box/%2e%2e/package.json', '/box/..%2fpackage.json', '/boxer/index.html', '/box/nope.js', '/box/core']) {
      assert.equal((await get(port, p)).status, 404, p);
    }
    assert.equal((await get(port, '/box/', 'POST')).status, 405);
  } finally { srv.close(); rmSync(dir, { recursive: true, force: true }); }
});

test('CSP 常量：满足需求 1 的严格策略，且没有 unsafe-* / worker-src / 通配', () => {
  for (const d of ["default-src 'self'", "script-src 'self'", "connect-src 'self'", "style-src 'self'", "img-src 'self' data:", "object-src 'none'", "base-uri 'none'", "frame-ancestors 'none'"]) {
    assert.ok(BOX_CSP.split('; ').includes(d), d);
  }
  assert.doesNotMatch(BOX_CSP, /unsafe|worker-src|\*|https?:/);
  assert.ok(!existsSync(join(SRC, 'sw.js')));
});

test('解析层是纯 JS：不引用 DOM/浏览器全局，也不依赖 ble/store', () => {
  for (const f of ['decimal', 'bytes', 'protocol', 'measurement', 'pipeline', 'export', 'view', 'fake']) {
    const s = stripComments(readFileSync(join(SRC, 'core', `${f}.js`), 'utf8'));
    assert.doesNotMatch(s, /\b(document|window|navigator|indexedDB|localStorage|location|HTMLElement|BluetoothDevice|BluetoothRemote\w*)\b/, `core/${f}.js 依赖了浏览器 API`);
    for (const m of s.matchAll(/from '\.\/([\w-]+)\.js'/g)) assert.ok(!['ble', 'store', 'detect'].includes(m[1]), `core/${f}.js 依赖 ${m[1]}`);
  }
});

test('自动化测试用的稳定选择器：关键元素都有 id 与 data-testid', () => {
  const h = readFileSync(join(SRC, 'index.html'), 'utf8');
  for (const id of ['btn-connect', 'btn-reconnect', 'btn-disconnect', 'chk-all', 'conn-state', 'conn-device', 'conn-info', 'conn-error',
    'demo-buttons', 'latest-body', 'history-table', 'history-rows', 'history-count', 'history-empty', 'btn-clear-demo', 'btn-clear-all',
    'btn-export-csv', 'btn-export-json', 'import-file', 'import-text', 'btn-import', 'import-result', 'export-msg',
    'set-unit', 'lang-select', 'dbg-char', 'dbg-hex', 'btn-dbg-parse', 'btn-dbg-save', 'dbg-out', 'support-msg', 'limits-list']) {
    assert.match(h, new RegExp(`id="${id}"[^>]*data-testid="${id}"`), id);
  }
  const main = readFileSync(join(SRC, 'main.js'), 'utf8');
  for (const tid of ['latest-weight', 'history-row', 'history-weight', 'history-delete']) assert.ok(main.includes(`'data-testid': '${tid}'`), tid);
  assert.match(main, /dataset\.state = st/);
});

test('选择器只能由真实点击触发：main.js 里 requestAndConnect 只出现在 btn-connect 的 click 处理器里', () => {
  const main = stripComments(readFileSync(join(SRC, 'main.js'), 'utf8'));
  const uses = [...main.matchAll(/requestAndConnect/g)].length;
  assert.equal(uses, 1);
  assert.doesNotMatch(main, /\.click\(\)\s*;?[^\n]*btn-connect|btn-connect[^\n]*\.click\(\)/);
  assert.doesNotMatch(main, /setTimeout\([^)]*requestAndConnect|DOMContentLoaded[^\n]*requestAndConnect/);
});
