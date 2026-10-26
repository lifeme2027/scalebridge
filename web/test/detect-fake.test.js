import test from 'node:test';
import assert from 'node:assert/strict';
import { detectSupport, getAvailability, supportLevel, browserFamily } from '../src/core/detect.js';
import { DEMO_SCENARIOS, DEMO_DEVICE, demoScenario } from '../src/core/fake.js';
import { processValue } from '../src/core/pipeline.js';
import { toCsv } from '../src/core/export.js';

test('detectSupport：特性检测，不依赖 UA', () => {
  const win = { isSecureContext: true, indexedDB: {}, BluetoothDevice: { prototype: { watchAdvertisements() {} } } };
  const nav = { bluetooth: { getAvailability: async () => true }, userAgent: 'x' };
  const s = detectSupport(nav, win);
  assert.deepEqual([s.hasBluetooth, s.secureContext, s.hasGetAvailability, s.hasWatchAdvertisements, s.hasIndexedDB], [true, true, true, true, true]);
  const none = detectSupport({ userAgent: 'Mozilla/5.0 Firefox/130.0' }, { isSecureContext: true });
  assert.equal(none.hasBluetooth, false);
  assert.equal(none.hasWatchAdvertisements, false);
  assert.equal(none.family, 'firefox');
  assert.equal(supportLevel(none, null), 'noApi');
  assert.equal(supportLevel({ ...s, secureContext: false }, true), 'insecure');
  assert.equal(supportLevel(s, false), 'noAdapter');
  assert.equal(supportLevel(s, null), 'ok');
});

test('browserFamily', () => {
  assert.equal(browserFamily({ userAgentData: { brands: [{ brand: 'Chromium' }] } }), 'chromium');
  assert.equal(browserFamily({ userAgent: 'Mozilla/5.0 (Macintosh) AppleWebKit Version/17 Safari/605' }), 'safari');
  assert.equal(browserFamily({ userAgent: 'Mozilla/5.0 Chrome/120 Safari/537' }), 'chromium');
  assert.equal(browserFamily(undefined), 'other');
});

test('getAvailability：true/false/null，抛异常也不外泄', async () => {
  assert.equal(await getAvailability({ bluetooth: { getAvailability: async () => true } }), true);
  assert.equal(await getAvailability({ bluetooth: { getAvailability: async () => false } }), false);
  assert.equal(await getAvailability({ bluetooth: { getAvailability: async () => { throw new Error('x'); } } }), null);
  assert.equal(await getAvailability({}), null);
});

const NOW = new Date(2026, 9, 26, 8, 30, 15);
const run = (id) => {
  const s = demoScenario(id);
  return processValue(s.service, s.characteristic, s.build(NOW), DEMO_DEVICE, NOW);
};

test('演示场景：字节走同一条解析管线，数值与构造一致，全部标 demo', () => {
  assert.equal(DEMO_SCENARIOS.length, 4);
  const basic = run('si-basic');
  assert.equal(basic.kind, 'record');
  assert.equal(basic.record.weightKg, '70.000');
  assert.equal(basic.record.demo, true);
  assert.match(basic.record.deviceId, /^DEMO:/);
  const full = run('si-full').record;
  assert.equal(full.deviceTime, '2026-10-26T08:30:15');
  assert.deepEqual([full.weightKg, full.bmi, full.heightM, full.userId], ['69.350', '22.5', '1.755', 1]);
  const lb = run('imperial').record;
  assert.equal(lb.originalUnit, 'lb');
  assert.equal(lb.weightOriginal, '154.32');
  assert.equal(lb.weightKg, '69.9983745384', 'lb→kg 精确换算（独立 Python Decimal 核对）');
});
