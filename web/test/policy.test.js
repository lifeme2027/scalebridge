import test from 'node:test';
import assert from 'node:assert/strict';
import { classifyWeight, isAbnormal, shouldAutoSave, dedupKey, isDeviceClockSuspect, decideIngest } from '../src/core/policy.js';
import { processValue } from '../src/core/pipeline.js';
import { parseHex } from '../src/core/bytes.js';

const rec = (o = {}) => ({
  deviceTime: '2026-10-26T08:30:15', receivedTime: '2026-10-26T08:30:16+08:00', weightKg: '70.000', rawWeight: 14000,
  bmi: null, heightM: null, bodyFatPercent: null, musclePercent: null, muscleMassKg: null, fatFreeMassKg: null,
  softLeanMassKg: null, bodyWaterMassKg: null, basalMetabolismKj: null, impedanceOhm: null, userId: 1,
  deviceId: 'web:x', source: 'weight-scale', ...o,
});

test('体重分类：边界与桌面 Q16 一致（2.000 / 200.000 恰好正常，0 不入库）', () => {
  const c = classifyWeight;
  assert.equal(c(null), 'notApplicable');
  assert.equal(c('0'), 'zero');
  assert.equal(c('0.000'), 'zero');
  assert.equal(c('0.005'), 'tooLight');
  assert.equal(c('1.999'), 'tooLight');
  assert.equal(c('2.000'), 'normal');
  assert.equal(c('2'), 'normal');
  assert.equal(c('70.000'), 'normal');
  assert.equal(c('200.000'), 'normal');
  assert.equal(c('200.001'), 'tooHeavy');
  assert.equal(shouldAutoSave(rec({ weightKg: '0.000' })), false);
  assert.equal(shouldAutoSave(rec({ weightKg: '0.005' })), true);
  assert.equal(shouldAutoSave(rec({ weightKg: null })), true, '没有体重（仅体脂）照常入库');
  assert.equal(isAbnormal(rec({ weightKg: '1.5' })), true);
  assert.equal(isAbnormal(rec({ weightKg: '250' })), true);
  assert.equal(isAbnormal(rec()), false);
  assert.equal(isAbnormal(rec({ weightKg: null })), false);
});

test('去重键：没有设备时间戳 → null；同时间戳不同数值 → 不同键；十进制写法不同（70.0 / 70.000）→ 同键', () => {
  assert.equal(dedupKey(rec({ deviceTime: null })), null);
  assert.notEqual(dedupKey(rec()), dedupKey(rec({ rawWeight: 14001, weightKg: '70.005' })));
  assert.equal(dedupKey(rec({ bmi: '22.5' })), dedupKey(rec({ bmi: '22.50' })));
  assert.notEqual(dedupKey(rec()), dedupKey(rec({ userId: 2 })));
  assert.notEqual(dedupKey(rec()), dedupKey(rec({ deviceId: 'web:y' })));
  assert.notEqual(dedupKey(rec()), dedupKey(rec({ source: 'body-composition' })));
  assert.equal(dedupKey(rec()), dedupKey(rec({ deviceTime: '2026-10-26T08:30:15.999' })), '到秒');
  assert.notEqual(dedupKey(rec()), dedupKey(rec({ deviceTime: '2026-10-26T08:30:16' })));
});

test('设备时钟可疑：晚 > 24h 或早 > 366 天；恰好边界不算；无时间戳不算', () => {
  const at = (devTime) => isDeviceClockSuspect(rec({ deviceTime: devTime, receivedTime: '2026-10-26T08:30:00+08:00' }));
  assert.equal(at('2026-10-27T08:30:00'), false);
  assert.equal(at('2026-10-27T08:30:01'), true);
  assert.equal(at('2025-10-25T08:30:00'), false); // 恰好 366 天前
  assert.equal(at('2025-10-25T08:29:59'), true);
  assert.equal(at('2000-01-01T00:00:00'), true);
  assert.equal(isDeviceClockSuspect(rec({ deviceTime: null })), false);
});

test('decideIngest：0 → zero；重复 → duplicate；无时间戳的相同读数各存一条', () => {
  assert.equal(decideIngest(rec({ weightKg: '0.000' }), []), 'zero');
  assert.equal(decideIngest(rec(), []), 'save');
  assert.equal(decideIngest(rec(), [rec()]), 'duplicate');
  assert.equal(decideIngest(rec({ rawWeight: 14001, weightKg: '70.005' }), [rec()]), 'save');
  assert.equal(decideIngest(rec({ deviceTime: null }), [rec({ deviceTime: null })]), 'save');
});

test('端到端：真实字节经解析管线 → 记录 → 策略（0 kg 的 0x2A9D 包不入库；带时间戳的重复包只存一次）', () => {
  const dev = { deviceId: 'web:x', deviceName: null, isDemo: false };
  const at = new Date(2026, 9, 26, 8, 30, 20);
  const mk = (hex) => processValue(0x181d, 0x2a9d, parseHex(hex).bytes, dev, at);
  const zero = mk('02 00 00 EA 07 0A 1A 08 1E 0F'); // 0 kg + 时间戳
  assert.equal(zero.kind, 'record');
  assert.equal(decideIngest(zero.record, []), 'zero');
  const w = mk('02 80 3E EA 07 0A 1A 08 1E 0F'); // 80.00 kg + 时间戳 2026-10-26 08:30:15
  assert.equal(w.kind, 'record');
  assert.equal(decideIngest(w.record, []), 'save');
  assert.equal(decideIngest(mk('02 80 3E EA 07 0A 1A 08 1E 0F').record, [w.record]), 'duplicate');
});
