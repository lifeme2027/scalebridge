import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync, readdirSync } from 'node:fs';
import { join } from 'node:path';
import zh from '../src/i18n/zh.js';
import en from '../src/i18n/en.js';
import { makeT, resolveLang, applyDom } from '../src/i18n/index.js';
import { DEMO_SCENARIOS } from '../src/core/fake.js';
import { WEB_DIR } from '../testlib/helpers.js';

const src = (p) => readFileSync(join(WEB_DIR, 'src', p), 'utf8');
const keys = (d) => Object.keys(d).sort();

test('中英字典键集完全一致', () => {
  assert.deepEqual(keys(zh), keys(en));
  assert.ok(keys(zh).length > 150);
});

test('所有文案非空；占位符 {x} 在两种语言里一致；中文文案里要有中文，英文里不能整句是中文', () => {
  const ph = (s) => [...s.matchAll(/\{(\w+)\}/g)].map((m) => m[1]).sort().join(',');
  const NEUTRAL = /^(app\.title|u\.|support\.detail\.|settings\.noteItem|lang\.)/;
  for (const k of keys(zh)) {
    assert.ok(zh[k].trim() !== '' && en[k].trim() !== '', k);
    assert.equal(ph(zh[k]), ph(en[k]), `placeholder mismatch: ${k}`);
    if (!NEUTRAL.test(k) && !/^(u\.|f\.bmi$|tag\.|src\.)/.test(k)) assert.doesNotMatch(en[k], /[\u4e00-\u9fff]/, `en has CJK: ${k}`);
  }
});

const ALL = readdirSync(join(WEB_DIR, 'src'), { recursive: true }).map(String);

/** 页面/脚本里实际用到的键（静态字面量 + 动态前缀按枚举展开）。 */
function usedKeys() {
  const used = new Set();
  for (const f of ['index.html', 'privacy.html']) {
    const h = src(f);
    for (const m of h.matchAll(/data-i18n="([^"]+)"/g)) used.add(m[1]);
    for (const m of h.matchAll(/data-i18n-attr="([^"]+)"/g)) for (const p of m[1].split(';')) used.add(p.split(':')[1].trim());
  }
  for (const f of ['main.js', 'privacy.js', 'core/view.js']) {
    const s = src(f);
    for (const m of s.matchAll(/\bt\('([\w.\-]+)'/g)) used.add(m[1]);
    for (const m of s.matchAll(/(?:key|k): '([\w.\-]+)'/g)) used.add(m[1]);
    for (const m of s.matchAll(/add\('(f\.[\w]+)'/g)) used.add(m[1]);
    for (const m of s.matchAll(/'([a-z][\w\-]*(?:\.[\w\-]+)+)'/g)) if (Object.hasOwn(zh, m[1])) used.add(m[1]); // 三元表达式等里的字面量
  }
  const dyn = (prefix, list) => list.forEach((x) => used.add(prefix + x));
  dyn('connect.state.', ['idle', 'requesting', 'connecting', 'connected', 'reconnecting', 'disconnected']);
  const bleSrc = src('core/ble.js');
  const bleCodes = new Set([...bleSrc.matchAll(/(?:return|code =|code:) '([a-zA-Z]+)'/g)].map((m) => m[1]));
  for (const c of ['noSupportedService', 'reconnectFailed', 'connectionLost', 'notSupported', 'cancelled', 'noDevice']) bleCodes.add(c);
  dyn('err.', [...bleCodes]);
  dyn('support.', ['ok', 'noApi', 'insecure', 'noAdapter']);
  dyn('support.avail.', ['true', 'false']);
  dyn('demo.s.', DEMO_SCENARIOS.map((s) => s.id));
  dyn('src.', ['weight-scale', 'body-composition']);
  for (let i = 1; i <= 8; i++) used.add(`limits.item${i}`);
  const imp = src('core/import.js');
  for (const m of imp.matchAll(/error: '([a-zA-Z]+)'/g)) { used.add(`io.fatal.${m[1]}`); used.add(`io.row.${m[1]}`); }
  for (const c of ['storeFailed']) used.add(`io.fatal.${c}`);
  for (const m of src('core/bytes.js').matchAll(/error: '([a-zA-Z]+)'/g)) used.add(`hex.${m[1]}`);
  return used;
}

test('代码与页面里用到的每个键都在字典里；字典里没有无人使用的键', () => {
  const used = usedKeys();
  const dict = new Set(keys(zh));
  const missing = [...used].filter((k) => !dict.has(k) && !k.startsWith('io.fatal.') && !k.startsWith('io.row.') && !k.startsWith('hex.'));
  assert.deepEqual(missing, [], '缺失的键');
  const unused = [...dict].filter((k) => !used.has(k) && !/^io\.(fatal|row)\.|^hex\./.test(k));
  assert.deepEqual(unused, [], '无人使用的键');
  // io.fatal/io.row/hex 是“代码字面量的超集”（同一个字面量既可能是致命错误也可能是行错误）：每个字面量至少要在其中一类里有文案
  const imp = src('core/import.js');
  for (const m of imp.matchAll(/error: '([a-zA-Z]+)'/g)) {
    assert.ok(dict.has(`io.fatal.${m[1]}`) || dict.has(`io.row.${m[1]}`), `import error code without text: ${m[1]}`);
  }
  for (const m of src('core/bytes.js').matchAll(/error: '([a-zA-Z]+)'/g)) assert.ok(dict.has(`hex.${m[1]}`), `hex error without text: ${m[1]}`);
});

test('makeT：占位符替换、缺键回退、未知语言回退英文', () => {
  const t = makeT('zh');
  assert.equal(t('connect.state.reconnecting', { attempt: 2, max: 3 }), '连接中断，正在重连（第 2/3 次）…');
  assert.equal(makeT('en')('history.count', { n: 5, demo: 2 }), '5 records (2 of them demo data)');
  assert.equal(t('no.such.key'), 'no.such.key');
  assert.equal(makeT('fr')('lang.label'), 'Language');
  assert.equal(t('io.exported', { n: 1 }), '已导出 1 条记录：{file}');
});

test('resolveLang：auto 跟随浏览器语言；显式选择优先', () => {
  assert.equal(resolveLang('auto', ['zh-CN', 'en']), 'zh');
  assert.equal(resolveLang('auto', ['zh-Hant-TW']), 'zh');
  assert.equal(resolveLang('auto', ['en-US', 'zh']), 'en');
  assert.equal(resolveLang('auto', ['fr-FR']), 'en');
  assert.equal(resolveLang('auto', []), 'en');
  assert.equal(resolveLang('zh', ['en']), 'zh');
  assert.equal(resolveLang('en', ['zh']), 'en');
});

test('applyDom：textContent 与属性，不解释 HTML', () => {
  const mk = (extra = {}) => ({ dataset: {}, attrs: {}, setAttribute(k, v) { this.attrs[k] = v; }, ...extra });
  const a = mk({ dataset: { i18n: 'lang.label' } });
  const b = mk({ dataset: { i18nAttr: 'aria-label:a11y.skip;title:lang.label' } });
  const root = { documentElement: {}, querySelectorAll: (s) => (s === '[data-i18n]' ? [a] : [b]) };
  applyDom(root, makeT('en'), 'en');
  assert.equal(a.textContent, 'Language');
  assert.deepEqual(b.attrs, { 'aria-label': 'Skip to main content', title: 'Language' });
  assert.equal(root.documentElement.lang, 'en');
  applyDom(root, makeT('zh'), 'zh');
  assert.equal(root.documentElement.lang, 'zh-CN');
});

test('html 页面里没有硬编码的可见中文文案（除 <option> 的语言名、示例字节）', () => {
  for (const f of ['index.html', 'privacy.html']) {
    const h = src(f).replace(/<!--[\s\S]*?-->/g, '');
    // 带 data-i18n 的元素，其内部原文只允许是占位（可以有中文占位文本，但必须被字典覆盖——这里要求：标签内文本要么为空，要么元素带 data-i18n）
    for (const m of h.matchAll(/<([a-z0-9]+)([^>]*)>([^<]*[\u4e00-\u9fff][^<]*)<\/\1>/g)) {
      assert.ok(/data-i18n=/.test(m[2]) || m[1] === 'option', `${f}: 未国际化的中文文本 <${m[1]}>${m[3]}`);
    }
  }
});
