import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { msg, renderMsg, renderParts } from '../src/core/status.js';
import { makeT } from '../src/i18n/index.js';
import zh from '../src/i18n/zh.js';
import en from '../src/i18n/en.js';
import { WEB_DIR } from '../testlib/helpers.js';

const src = (p) => readFileSync(join(WEB_DIR, 'src', p), 'utf8');

test('SBW-L04：状态消息存键+参数，切换语言后按新语言重渲染', () => {
  const m = msg('io.exported', { n: 3, file: 'a.csv' });
  assert.equal(renderMsg(makeT('zh'), m), '已导出 3 条记录：a.csv');
  assert.equal(renderMsg(makeT('en'), m), makeT('en')('io.exported', { n: 3, file: 'a.csv' }));
  assert.notEqual(renderMsg(makeT('zh'), m), renderMsg(makeT('en'), m));
  assert.match(renderMsg(makeT('en'), m), /3/);
  assert.equal(renderMsg(makeT('zh'), null), '');
  assert.equal(renderMsg(makeT('zh'), msg('io.exportNone')), zh['io.exportNone']);
});

test('消息参数可以是另一条消息（嵌套翻译，如十六进制错误原因）', () => {
  const m = msg('debug.badHex', { reason: msg('hex.badChar') });
  for (const [lang, d] of [['zh', zh], ['en', en]]) {
    const out = renderMsg(makeT(lang), m);
    assert.ok(out.includes(d['hex.badChar']), `${lang}: ${out}`);
    assert.ok(!out.includes('{reason}'));
  }
});

test('renderParts：JSON 原文与消息按段拼接，空段跳过', () => {
  const parts = ['{"a":1}', msg('debug.saved')];
  assert.equal(renderParts(makeT('en'), parts), `{"a":1}\n\n${en['debug.saved']}`);
  assert.equal(renderParts(makeT('zh'), parts), `{"a":1}\n\n${zh['debug.saved']}`);
  assert.equal(renderParts(makeT('zh'), []), '');
});

test('main.js 不再把已翻译的字符串存进状态（status/store 错误都是 {key,params}）', () => {
  const main = src('main.js');
  // 这些元素的文字只能由 renderStatus / renderImportResult 写入
  for (const id of ['export-msg', 'note-msg', 'dbg-out']) {
    const writes = [...main.matchAll(new RegExp(`\\$\\('${id}'\\)\\.textContent\\s*(\\+)?=`, 'g'))];
    assert.deepEqual(writes, [], `${id} 不应在事件处理器里直接写入已翻译文本`);
  }
  assert.doesNotMatch(main, /S\.storeError\s*=\s*S\.t\(/);
});

test('SBW-L02/L03/M01：页面结构——conn-info 是 polite live region，「历史记录」标题可接焦点，窄屏样式', () => {
  const html = src('index.html');
  assert.match(html, /<p id="conn-info"[^>]*aria-live="polite"/);
  assert.match(html, /<h2 id="history-h"[^>]*tabindex="-1"/);
  const css = src('style.css');
  assert.match(css, /main\s*\{[^}]*grid-template-columns:\s*minmax\(0,\s*1fr\)/);
  assert.match(css, /input\[type=file\][^{]*\{[^}]*max-width:\s*100%/);
  assert.match(css, /select[^{]*\{[^}]*max-width:\s*100%/);
});

test('隐私页：同源说明与重连说明，中英都有，且与代码里的重连参数一致', async () => {
  const html = src('privacy.html');
  for (const k of ['privacy.origin.h', 'privacy.origin.p', 'privacy.reconnect.h', 'privacy.reconnect.p']) {
    assert.match(html, new RegExp(`data-i18n="${k.replace(/\./g, '\\.')}"`), k);
    assert.ok(zh[k] && en[k], k);
  }
  assert.match(zh['privacy.origin.p'], /同源/);
  assert.match(zh['privacy.origin.p'], /life\.kastop\.com/);
  assert.match(zh['privacy.origin.p'], /scalebridge/);
  assert.match(zh['privacy.origin.p'], /清除站点数据/);
  assert.match(zh['privacy.origin.p'], /配额/);
  assert.match(en['privacy.origin.p'], /same origin/i);
  assert.match(en['privacy.origin.p'], /life\.kastop\.com/);
  assert.match(en['privacy.origin.p'], /scalebridge/);
  assert.match(en['privacy.origin.p'], /clear site data/i);
  assert.match(en['privacy.origin.p'], /quota/i);
  const { RECONNECT_DELAYS_MS } = await import('../src/core/ble.js');
  assert.equal(RECONNECT_DELAYS_MS.length, 3, '文案写的是最多 3 次');
  assert.match(zh['privacy.reconnect.p'], /最多 3 次/);
  assert.match(en['privacy.reconnect.p'], /up to 3 times/);
  assert.match(zh['connect.intro'], /最多 3 次/);
  assert.match(en['connect.intro'], /up to 3 times/);
});
