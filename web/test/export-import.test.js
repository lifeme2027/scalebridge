import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { WEB_DIR } from '../testlib/helpers.js';
import { toCsv, toJson, guardText, unguardText, escapeCsv, CSV_HEADER, exportFileName } from '../src/core/export.js';
import { importCsv, importJson, importAuto, parseCsvRows, parseJsonExact } from '../src/core/import.js';
import { fromWeightScale, fromBodyComposition, formatReceivedTime, recordSignature } from '../src/core/measurement.js';
import { parseWeightMeasurement, parseBodyCompositionMeasurement } from '../src/core/protocol.js';
import { parseHex } from '../src/core/bytes.js';

const fx = (n) => readFileSync(join(WEB_DIR, 'test/fixtures', n), 'utf8');
const dev = { deviceId: 'AA:BB', deviceName: 'Scale', isDemo: false };
const at = new Date(2026, 9, 26, 8, 30, 9);
const wsm = (hex) => fromWeightScale(parseWeightMeasurement(parseHex(hex).bytes).value, dev, at);
const bcm = (hex) => fromBodyComposition(parseBodyCompositionMeasurement(parseHex(hex).bytes).value, dev, at);

test('GuardText 与桌面 CsvExporter.GuardText 同样的用例', () => {
  const cases = [['=1+1', "'=1+1"], ['+cmd', "'+cmd"], ['-2', "'-2"], ['@SUM(A1)', "'@SUM(A1)"], ['\tx', "'\tx"], ['\rx', "'\rx"],
    ["'=x", "''=x"], ['Scale', 'Scale'], ["it's", "it's"], ['', ''], ["'", "'"], ["'abc", "'abc"]];
  for (const [i, o] of cases) { assert.equal(guardText(i), o); assert.equal(unguardText(o), i, `unguard ${JSON.stringify(o)}`); }
  assert.equal(unguardText("'plain"), "'plain"); // 非防护产生的前导 ' 不动
});

test('escapeCsv', () => {
  assert.equal(escapeCsv('a'), 'a');
  assert.equal(escapeCsv('a,b'), '"a,b"');
  assert.equal(escapeCsv('say "x"'), '"say ""x"""');
  assert.equal(escapeCsv('l1\nl2'), '"l1\nl2"');
});

test('toCsv：BOM、CRLF、表头 22 列、数值列不加引号前缀、设备列做防护', () => {
  const rec = { ...wsm('0E 2E 36 EA 07 0A 1A 0C 1E 05 01 FA 00 A1 02'), deviceId: '=evil', deviceName: '@x', demo: true };
  const csv = toCsv([rec]);
  assert.ok(csv.startsWith('\uFEFF' + CSV_HEADER.join(',') + '\r\n'));
  assert.equal(CSV_HEADER.length, 22);
  assert.ok(csv.endsWith('\r\n'));
  assert.ok(!csv.replace(/\r\n/g, '').includes('\n'), '只能出现 CRLF 换行');
  assert.equal(csv.split('\r\n').length, 3);
  assert.ok(csv.includes(",'=evil,'@x,weight-scale,true,true\r\n"));
  assert.ok(!csv.includes(",'69"), '数值列不应加引号前缀');
});

test('桌面（C# CsvExporter / JsonExporter）生成的夹具：Web 能导入，再导出字节级一致', () => {
  const csvText = fx('desktop-export.csv'), jsonText = fx('desktop-export.json');
  const a = importCsv(csvText), b = importJson(jsonText);
  assert.equal(a.ok, true); assert.equal(b.ok, true);
  assert.equal(a.records.length, 4); assert.deepEqual(a.rejected, []); assert.deepEqual(b.rejected, []);
  assert.deepEqual(a.records, b.records, 'CSV 与 JSON 导入结果相同');
  assert.equal(toCsv(a.records), csvText);
  assert.equal(toJson(b.records, new Date('2026-10-26T02:00:00Z')).replace(/"exportedAt": "[^"]+"/, '"exportedAt": "X"'),
    jsonText.replace(/"exportedAt": "[^"]+"/, '"exportedAt": "X"'));
  // 精确十进制保留（不被浮点吃掉末尾的 0）
  assert.equal(b.records[0].weightKg, '70.3066435010');
  assert.equal(b.records[1].weightKg, '69.350');
  // 防护字符被还原
  assert.deepEqual(b.records.map((r) => r.deviceId), ["=cmd|' /C calc'!A0", 'AA:BB:CC:DD:EE:FF', "'@odd", 'line\nbreak,名称']);
});

test('Web 导出 → Web 导入：无损往返；再次导入全部判重（幂等）', () => {
  const recs = [
    wsm('0E 2E 36 EA 07 0A 1A 0C 1E 05 01 FA 00 A1 02'),
    wsm('01 78 3C'),
    bcm('00 04 BB 00 1E 36'), // 体成分：体脂 18.7 %，体重 69.270 kg
  ];
  recs.push({ ...recs[0], deviceId: "'=x", deviceName: null, demo: true });
  for (const [name, text] of [['csv', toCsv(recs)], ['json', toJson(recs)]]) {
    const r = importAuto(text);
    assert.equal(r.ok, true, name);
    assert.equal(r.records.length, recs.length, name);
    assert.deepEqual(r.rejected, []);
    assert.deepEqual(r.records.map(recordSignature).sort(), recs.map(recordSignature).sort(), name);
    const again = importAuto(text, r.records);
    assert.equal(again.records.length, 0); assert.equal(again.duplicates, recs.length);
  }
});

const goodJson = (over = {}) => JSON.stringify({ format: 'scalebridge-export', version: 1, measurements: [{
  receivedTime: '2026-10-26T08:30:09+08:00', weightKg: 70, deviceId: 'd', source: 'weight-scale', experimental: true, demo: false, ...over }] });

test('JSON：version/format 严格校验', () => {
  assert.equal(importJson(goodJson()).ok, true);
  const bad = (doc) => importJson(JSON.stringify(doc));
  assert.equal(bad({ format: 'scalebridge-export', measurements: [] }).error, 'missingVersion');
  assert.equal(bad({ format: 'scalebridge-export', version: '1', measurements: [] }).error, 'missingVersion');
  assert.equal(bad({ format: 'scalebridge-export', version: 1.5, measurements: [] }).error, 'badVersion');
  assert.equal(bad({ format: 'scalebridge-export', version: 0, measurements: [] }).error, 'badVersion');
  assert.equal(bad({ format: 'scalebridge-export', version: 2, measurements: [] }).error, 'newerVersion');
  assert.equal(bad({ format: 'other', version: 1, measurements: [] }).error, 'wrongFormat');
  assert.equal(bad({ format: 'scalebridge-export', version: 1 }).error, 'noMeasurements');
  assert.equal(importJson('[]').error, 'notExport');
  assert.equal(importJson('not json').error, 'notJson');
  assert.equal(importJson('').error, 'empty');
  assert.equal(importJson('{"a":1,"a":2}').error, 'notJson'); // 重复键
});

test('JSON：逐行校验，坏行跳过并报告，好行仍导入', () => {
  const doc = JSON.parse(goodJson());
  const rows = [
    {},                                       // ok
    { receivedTime: '2026-02-30T00:00:00+08:00' }, // 不存在的日期
    { receivedTime: '2026-10-26 08:30:09' },  // 格式
    { weightKg: '70' },                        // 数字字段给了字符串
    { weightKg: -1 },                          // 负数
    { weightKg: 1e3 },                         // 科学计数法令牌（1000 被 JSON.stringify 写成 1000，换成文本形式见下）
    { userId: 256 }, { userId: 1.5 }, { rawWeight: 70000 },
    { source: 'x' }, { demo: 'yes' }, { deviceId: '' }, { deviceId: 'a\u0000b' },
    { originalUnit: 'stone' }, { weightOriginal: 70 },        // 有原值无单位
    { deviceTime: '2026-13-01T00:00:00' },
    { weightKg: null },                         // 没有任何测量值
    'not an object',
  ];
  doc.measurements = rows.map((o) => (typeof o === 'string' ? o : { ...doc.measurements[0], ...o }));
  const r = importJson(JSON.stringify(doc).replace('"weightKg":1000', '"weightKg":1e3'));
  assert.equal(r.ok, true);
  assert.equal(r.total, rows.length);
  assert.equal(r.records.length + r.rejected.length + r.duplicates, rows.length);
  const rejectedRows = r.rejected.map((x) => x.row);
  assert.ok(!rejectedRows.includes(1));
  for (const n of [2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18]) assert.ok(rejectedRows.includes(n), `row ${n} should be rejected`);
  assert.equal(r.records.length, 1);
  assert.deepEqual(r.rejected.find((x) => x.row === 2), { row: 2, error: 'badReceivedTime', field: 'receivedTime' });
  assert.equal(r.rejected.find((x) => x.row === 17).error, 'noValues');
});

test('parseJsonExact：数字保留原文、转义、嵌套深度、尾随内容', () => {
  const v = parseJsonExact('{"a":70.0050,"b":"\\u00e9\\n\\ud83d\\ude00","c":[1,{"d":null}]}');
  assert.equal(v.a.token, '70.0050');
  assert.equal(v.b, 'é\n😀');
  assert.throws(() => parseJsonExact('{"a":1} x'));
  assert.throws(() => parseJsonExact('[' .repeat(40) + ']'.repeat(40)));
  assert.throws(() => parseJsonExact('{"a":01}'));
  assert.throws(() => parseJsonExact('"\n"'));
  assert.throws(() => parseJsonExact("{'a':1}"));
  assert.throws(() => parseJsonExact('{"__proto__":1,"__proto__":2}'));
  assert.equal(Object.getPrototypeOf(parseJsonExact('{"__proto__":{"x":1}}')), null); // 无原型污染
});

test('CSV 解析：引号、转义、CRLF/LF、BOM、坏格式', () => {
  assert.deepEqual(parseCsvRows('a,b\r\n"x,1","y""z"\r\n'), [['a', 'b'], ['x,1', 'y"z']]);
  assert.deepEqual(parseCsvRows('a,b\nc,d'), [['a', 'b'], ['c', 'd']]);
  assert.deepEqual(parseCsvRows('a,"l1\r\nl2"\r\n'), [['a', 'l1\r\nl2']]);
  assert.deepEqual(parseCsvRows('a,,\n'), [['a', '', '']]);
  assert.equal(parseCsvRows('a,"unterminated'), null);
  assert.equal(parseCsvRows('a,b"c"'), null);
  assert.equal(parseCsvRows('"a"b'), null);
});

test('CSV 导入：表头/列数/内容校验与 BOM、LF 容忍', () => {
  const good = importCsv(fx('desktop-export.csv'));
  assert.equal(good.ok, true);
  const lf = importCsv(fx('desktop-export.csv').replace(/\r\n/g, '\n').slice(1));
  assert.equal(lf.records.length, 4);
  assert.equal(importCsv('a,b\n1,2\n').error, 'badHeader');
  assert.equal(importCsv('').error, 'empty');
  const header = CSV_HEADER.join(',');
  const row = (o = {}) => {
    const base = { device_time: '', received_time: '2026-10-26T08:30:09+08:00', weight_kg: '70.000', device_id: 'd', source: 'weight-scale', experimental: 'true', demo: 'false' };
    return CSV_HEADER.map((h) => ({ ...base, ...o })[h] ?? '').join(',');
  };
  const r = importCsv([header, row(), row({ weight_kg: 'abc' }), 'only,three,cols', row({ demo: 'maybe' }), row({ weight_kg: '=1+1' }), row({ weight_kg: '', received_time: 'x' })].join('\r\n'));
  assert.equal(r.records.length, 1);
  assert.deepEqual(r.rejected.map((x) => [x.row, x.error]), [[2, 'badNumber'], [3, 'columnCount'], [4, 'badBoolean'], [5, 'badNumber'], [6, 'badReceivedTime']]);
  // 重复列名
  assert.equal(importCsv(header + ',weight_kg\r\n').error, 'badHeader');
  // 额外未知列被忽略并计数
  const extra = importCsv(header + ',future_col\r\n' + row() + ',zzz\r\n');
  assert.equal(extra.unknownColumns, 1); assert.equal(extra.records.length, 1);
});

test('导入：同文件内重复判重；上限；receivedTime 规范化', () => {
  const doc = JSON.parse(goodJson());
  doc.measurements.push({ ...doc.measurements[0], weightKg: 70 });
  const r = importJson(JSON.stringify(doc).replace('"weightKg":70}', '"weightKg":70.000}'));
  assert.equal(r.records.length, 1); assert.equal(r.duplicates, 1);
  const z = importJson(goodJson({ receivedTime: '2026-10-26T00:30:09Z' }));
  assert.equal(z.records[0].receivedTime, '2026-10-26T00:30:09+00:00');
  assert.equal(importJson(goodJson({ receivedTime: '2026-10-26T00:30:09+15:00' })).records.length, 0);
  assert.equal(importJson('x'.repeat(6_000_000)).error, 'tooLarge');
});

test('formatReceivedTime / exportFileName', () => {
  assert.match(formatReceivedTime(new Date()), /^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d[+-]\d\d:\d\d$/);
  assert.equal(exportFileName('csv', new Date(2026, 0, 2, 3, 4, 5), true), 'scalebridge-20260102-030405-demo.csv');
  assert.equal(exportFileName('json', new Date(2026, 0, 2, 3, 4, 5)), 'scalebridge-20260102-030405.json');
});

test('工厂：测量不成功返回 null；英制质量换算 kg', () => {
  assert.equal(fromWeightScale(parseWeightMeasurement(parseHex('00 FF FF').bytes).value, dev, at), null);
  const r = wsm('01 78 3C'); // 0x3C78 = 15480 → 154.80 lb
  assert.equal(r.originalUnit, 'lb'); assert.equal(r.weightOriginal, '154.80');
  assert.equal(r.weightKg, '70.2160988760');
  assert.equal(r.experimental, true);
});
