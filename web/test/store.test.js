import test from 'node:test';
import assert from 'node:assert/strict';
import { IDBFactory } from 'fake-indexeddb';
import { IdbStore, MemoryStore, openStore, DEFAULT_SETTINGS } from '../src/core/store.js';

const rec = (over = {}) => ({
  deviceTime: null, receivedTime: '2026-10-26T08:30:09+08:00', weightKg: '70.000', deviceId: 'd', deviceName: null,
  source: 'weight-scale', experimental: true, demo: false, ...over });

for (const [name, make] of [
  ['IdbStore（fake-indexeddb）', async () => new IdbStore(new IDBFactory()).open()],
  ['MemoryStore', async () => new MemoryStore().open()],
]) {
  test(`${name}：增删查、自增 id、演示数据清理`, async () => {
    const s = await make();
    assert.equal(await s.count(), 0);
    const added = await s.add([rec(), rec({ demo: true, deviceId: 'DEMO:1' }), rec({ weightKg: '71.5' })]);
    assert.deepEqual(added.map((r) => r.id), [1, 2, 3]);
    assert.equal(await s.count(), 3);
    const all = await s.all();
    assert.equal(all.length, 3);
    assert.equal(all[0].weightKg, '70.000'); // 十进制字符串原样存取
    assert.equal(await s.removeDemo(), 1);
    assert.equal((await s.all()).some((r) => r.demo), false);
    await s.remove(1);
    assert.deepEqual((await s.all()).map((r) => r.id), [3]);
    await s.clearMeasurements();
    assert.equal(await s.count(), 0);
  });

  test(`${name}：设置读写与默认值`, async () => {
    const s = await make();
    assert.deepEqual(await s.getSettings(), DEFAULT_SETTINGS);
    await s.setSetting('displayUnit', 'lb');
    await s.setSetting('userNotes', { 1: '爸爸' });
    const st = await s.getSettings();
    assert.equal(st.displayUnit, 'lb'); assert.deepEqual(st.userNotes, { 1: '爸爸' }); assert.equal(st.lang, 'auto');
    await assert.rejects(() => s.setSetting('evil', 1));
  });
}

test('IdbStore：关闭后重新打开，数据仍在（模拟刷新页面）', async () => {
  const factory = new IDBFactory();
  const a = await new IdbStore(factory).open();
  await a.add([rec({ weightKg: '65.5' })]);
  await a.setSetting('lang', 'en');
  a.close();
  const b = await new IdbStore(factory).open();
  assert.equal((await b.all())[0].weightKg, '65.5');
  assert.equal((await b.getSettings()).lang, 'en');
});

test('IdbStore.add 是原子的：事务失败时一条都不写入', async () => {
  const s = await new IdbStore(new IDBFactory()).open();
  await s.add([rec()]);
  // 第二条含函数，不可结构化克隆 → 整个事务回滚
  await assert.rejects(() => s.add([rec({ weightKg: '1' }), { ...rec(), bad: () => 1 }]));
  assert.equal(await s.count(), 1);
});

test('openStore：IndexedDB 不可用时降级为内存存储并标记 persistent=false', async () => {
  const s = await openStore(undefined);
  assert.equal(s.persistent, false);
  const ok = await openStore(new IDBFactory());
  assert.equal(ok.persistent, true);
  const broken = await openStore({ open() { throw new Error('denied'); } });
  assert.equal(broken.persistent, false);
});
