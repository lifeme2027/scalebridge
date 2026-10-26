import test from 'node:test';
import assert from 'node:assert/strict';
import { ConnectionController, mapError, requestOptions, OPTIONAL_SERVICES } from '../src/core/ble.js';
import { FakeBluetooth, FakeDevice, FakeService, FakeCharacteristic, domErr } from '../testlib/fake-gatt.js';

const NOW = new Date(2026, 9, 26, 8, 30, 15);
const mkScale = (opts = {}) => {
  const meas = new FakeCharacteristic();
  const feat = new FakeCharacteristic([0x00, 0x00, 0x00, 0x00]);
  const man = new FakeCharacteristic([...Buffer.from('ACME')]);
  const dev = new FakeDevice({
    ...opts,
    services: { 0x181d: new FakeService({ 0x2a9e: feat, 0x2a9d: meas }), 0x180a: new FakeService({ 0x2a29: man }) },
  });
  return { dev, meas, feat };
};
const collect = (c, ...events) => {
  const log = [];
  for (const e of events) c.on(e, (p) => log.push([e, p]));
  return log;
};
const ctl = (bt, extra = {}) => new ConnectionController({ bluetooth: bt, now: () => NOW, wait: async () => {}, ...extra });

test('requestDevice 选项：默认按两个标准服务过滤；optionalServices 含 181D/181B/180A', () => {
  const o = requestOptions(false);
  assert.deepEqual(o.filters, [{ services: [0x181d] }, { services: [0x181b] }]);
  assert.deepEqual(o.optionalServices, [0x181d, 0x181b, 0x180a]);
  assert.deepEqual(OPTIONAL_SERVICES, [0x181d, 0x181b, 0x180a]);
  const all = requestOptions(true);
  assert.equal(all.acceptAllDevices, true);
  assert.equal(all.filters, undefined);
  assert.deepEqual(all.optionalServices, [0x181d, 0x181b, 0x180a]);
});

test('连接：读 Feature、订阅 0x2A9D、读设备信息，通知经解析管线变成记录', async () => {
  const { dev, meas } = mkScale();
  const bt = new FakeBluetooth(dev);
  const c = ctl(bt);
  const log = collect(c, 'state', 'feature', 'info', 'record', 'device');
  assert.equal(await c.requestAndConnect(), true);
  assert.deepEqual(log.filter(([e]) => e === 'state').map(([, p]) => p.state), ['requesting', 'connecting', 'connected']);
  assert.ok(meas.notifying);
  assert.equal(log.find(([e]) => e === 'info')[1].manufacturer, 'ACME');
  assert.equal(log.find(([e]) => e === 'feature')[1].service, 0x181d);
  meas.push([0x00, 0xb0, 0x36]);
  const rec = log.find(([e]) => e === 'record')[1].record;
  assert.equal(rec.weightKg, '70.000');
  assert.equal(rec.deviceId, 'web:dev1');
  assert.equal(rec.deviceName, 'Fake Scale');
  assert.equal(rec.demo, false);
  assert.equal(rec.experimental, true);
  assert.equal(rec.receivedTime.slice(0, 19), '2026-10-26T08:30:15');
});

test('测量不成功与解析错误分别给出事件，不产生记录', async () => {
  const { dev, meas } = mkScale();
  const c = ctl(new FakeBluetooth(dev));
  const log = collect(c, 'record', 'unsuccessful', 'parseError');
  await c.requestAndConnect();
  meas.push([0x00, 0xff, 0xff]);
  meas.push([0x00, 0x59]);
  assert.deepEqual(log.map(([e]) => e), ['unsuccessful', 'parseError']);
  assert.equal(log[1][1].errorCode, 'TooShort');
  assert.equal(log[1][1].hex, '00 59');
});

test('用户取消选择器 → cancelled，状态回到 idle', async () => {
  const c = ctl(new FakeBluetooth(domErr('NotFoundError', 'User cancelled the requestDevice() chooser.')));
  const log = collect(c, 'error', 'state');
  assert.equal(await c.requestAndConnect(), false);
  assert.equal(log.find(([e]) => e === 'error')[1].code, 'cancelled');
  assert.equal(c.state, 'idle');
});

test('设备没有受支持的服务 → noSupportedService，并断开', async () => {
  const dev = new FakeDevice({ services: {} });
  const c = ctl(new FakeBluetooth(dev));
  const log = collect(c, 'error');
  assert.equal(await c.requestAndConnect(), false);
  assert.equal(log[0][1].code, 'noSupportedService');
  assert.equal(c.state, 'disconnected');
  assert.equal(dev.connected, false);
});

test('GATT 连接失败 → network 错误；之后可 reconnect()', async () => {
  const { dev } = mkScale({ connectFailures: 1 });
  const c = ctl(new FakeBluetooth(dev));
  const log = collect(c, 'error');
  assert.equal(await c.requestAndConnect(), false);
  assert.equal(log[0][1].code, 'network');
  assert.equal(c.state, 'disconnected');
  assert.equal(await c.reconnect(), true);
  assert.equal(c.state, 'connected');
});

test('意外断开：自动重连（最多 3 次），成功后继续收数据', async () => {
  const { dev, meas } = mkScale();
  const c = ctl(new FakeBluetooth(dev));
  const log = collect(c, 'state', 'record');
  await c.requestAndConnect();
  dev.connectFailures = 1;
  dev.drop();
  await new Promise((r) => setTimeout(r, 20));
  const states = log.filter(([e]) => e === 'state').map(([, p]) => p.state + (p.attempt ? p.attempt : ''));
  assert.deepEqual(states.slice(-3), ['reconnecting1', 'reconnecting2', 'connected']);
  meas.push([0x00, 0xb0, 0x36]);
  assert.equal(log.filter(([e]) => e === 'record').length, 1);
  assert.equal(meas.listenerCount('characteristicvaluechanged'), 1, '重连后不能重复挂监听');
});

test('重连 3 次都失败 → connectionLost，状态 disconnected', async () => {
  const { dev } = mkScale();
  const c = ctl(new FakeBluetooth(dev));
  const log = collect(c, 'error', 'state');
  await c.requestAndConnect();
  dev.connectFailures = 99;
  dev.drop();
  await new Promise((r) => setTimeout(r, 20));
  const codes = log.filter(([e]) => e === 'error').map(([, p]) => p.code);
  assert.deepEqual(codes, ['reconnectFailed', 'reconnectFailed', 'reconnectFailed', 'connectionLost']);
  assert.equal(c.state, 'disconnected');
  assert.equal(dev.connectCalls, 4);
});

test('用户主动断开：不重连，取消订阅', async () => {
  const { dev, meas } = mkScale();
  const c = ctl(new FakeBluetooth(dev));
  const log = collect(c, 'state');
  await c.requestAndConnect();
  await c.disconnect();
  await new Promise((r) => setTimeout(r, 10));
  assert.equal(c.state, 'disconnected');
  assert.equal(dev.connectCalls, 1);
  assert.equal(meas.listenerCount('characteristicvaluechanged'), 0);
  assert.ok(!log.some(([, p]) => p.state === 'reconnecting'));
});

test('没有 navigator.bluetooth → notSupported 错误', async () => {
  const c = ctl(undefined);
  const log = collect(c, 'error');
  assert.equal(await c.requestAndConnect(), false);
  assert.equal(log[0][1].code, 'notSupported');
});

test('mapError 覆盖常见 DOMException', () => {
  const m = (name, message) => mapError({ name, message });
  assert.equal(m('NotFoundError', 'No Bluetooth devices found'), 'noDevice');
  assert.equal(m('SecurityError'), 'security');
  assert.equal(m('NotAllowedError'), 'notAllowed');
  assert.equal(m('NetworkError'), 'network');
  assert.equal(m('InvalidStateError'), 'invalidState');
  assert.equal(m('Whatever'), 'unknown');
  assert.equal(mapError(null), 'unknown');
});
