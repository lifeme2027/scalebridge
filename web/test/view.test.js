import test from 'node:test';
import assert from 'node:assert/strict';
import { weightText, weightDetail, detailRows, tagFor, timeText } from '../src/core/view.js';
import { makeT } from '../src/i18n/index.js';
import { DEMO_SCENARIOS, DEMO_DEVICE } from '../src/core/fake.js';
import { processValue } from '../src/core/pipeline.js';

const NOW = new Date(2026, 9, 26, 8, 30, 15);
const rec = (id) => { const s = DEMO_SCENARIOS.find((x) => x.id === id); return processValue(s.service, s.characteristic, s.build(NOW), DEMO_DEVICE, NOW).record; };
const t = makeT('zh');

test('显示：kg 与 lb 显示单位；原单位 lb 时给出原值 + kg 换算', () => {
  const lb = rec('imperial');
  assert.equal(weightText(lb, 'kg', t), '69.998 kg');
  assert.equal(weightText(lb, 'lb', t), '154.32 lb');
  assert.equal(weightDetail(lb, t), '154.32 lb (= 69.998 kg)');
  const si = rec('si-basic');
  assert.equal(weightText(si, 'lb', t), '154.32 lb');
  assert.equal(weightDetail(si, t), '70.0 kg');
});

test('详情行只列有值的字段；体成分场景覆盖全部字段；用户备注显示', () => {
  const rows = detailRows(rec('body-composition'), 'kg', t, { 2: '妈妈' });
  const keys = rows.map(([k]) => k);
  for (const k of ['f.weight', 'f.height', 'f.bodyFat', 'f.muscle', 'f.muscleMass', 'f.fatFree', 'f.softLean', 'f.water', 'f.basal', 'f.impedance', 'f.user', 'f.deviceTime', 'f.device']) assert.ok(keys.includes(k), k);
  assert.ok(!keys.includes('f.bmi'));
  assert.equal(rows.find(([k]) => k === 'f.user')[1], '2（妈妈）');
  assert.equal(rows.find(([k]) => k === 'f.bodyFat')[1], '18.7 %');
  assert.equal(rows.find(([k]) => k === 'f.impedance')[1], '500.5 Ω');
});

test('演示记录始终标 tag.demo；时间文本', () => {
  assert.equal(tagFor(rec('si-basic')), 'tag.demo');
  assert.equal(t('tag.demo'), '演示数据');
  assert.equal(timeText({ deviceTime: '2026-10-26T08:30:15', receivedTime: '2026-10-26T08:30:15+08:00' }), '2026-10-26 08:30:15');
});
