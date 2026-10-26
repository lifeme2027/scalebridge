import test from 'node:test';
import assert from 'node:assert/strict';
import * as D from '../src/core/decimal.js';
import { parseHex, toHex, ByteReader } from '../src/core/bytes.js';

test('parse/format 往返；保留末尾 0；拒绝非十进制串', () => {
  for (const s of ['0', '70', '70.000', '-1.5', '0.0050', '123456789012345678901234567890.123']) assert.equal(D.format(D.parse(s)), s);
  for (const bad of ['', '1e3', '.5', '5.', '+1', '01', 'NaN', ' 1', 1, null, '1.2.3', '0x10']) assert.equal(D.isDecimalString(bad), false, String(bad));
  assert.throws(() => D.parse('1e3'));
});

test('fromScaled / mul / compare', () => {
  assert.equal(D.fromScaled(14001, 3), '14.001');
  assert.equal(D.fromScaled(5, 3), '0.005');
  assert.equal(D.fromScaled(0, 2), '0.00');
  assert.equal(D.mul('154.80', D.KG_PER_LB), '70.2160988760'); // 小数位相加：2 + 8
  assert.equal(D.mul('12.3', D.M_PER_IN), '0.31242');
  assert.equal(D.compare('70.0', '70.000'), 0);
  assert.equal(D.compare('0.1', '0.09'), 1);
  assert.equal(D.compare('-1', '0.5'), -1);
});

test('round：四舍五入远离零；div', () => {
  assert.equal(D.round('1.005', 2), '1.01');
  assert.equal(D.round('1.0049', 2), '1.00');
  assert.equal(D.round('2.5', 0), '3');
  assert.equal(D.round('1.5', 3), '1.500');
  assert.equal(D.round('-1.005', 2), '-1.01');
  assert.equal(D.div('1', '3', 4), '0.3333');
  assert.equal(D.div('2', '3', 4), '0.6667');
  assert.throws(() => D.div('1', '0', 2));
});

test('显示格式：kg 0.0##、lb 固定 2 位（不出现浮点误差）', () => {
  assert.equal(D.formatKgForDisplay('70.000'), '70.0');
  assert.equal(D.formatKgForDisplay('69.350'), '69.35');
  assert.equal(D.formatKgForDisplay('70.2160988760'), '70.216');
  assert.equal(D.kgToLbForDisplay('70.000'), '154.32'); // 70 / 0.45359237 = 154.3235...
  assert.equal(D.kgToLbForDisplay(D.mul('154.80', D.KG_PER_LB)), '154.80');
  assert.equal(D.formatForUnit('70.000', 'lb'), '154.32');
  assert.equal(D.trimFraction('1.500', 1), '1.5');
  assert.equal(D.trimFraction('2', 1), '2.0');
});

test('hex 解析', () => {
  assert.deepEqual([...parseHex('0E 2e,0xFF').bytes], [0x0e, 0x2e, 0xff]);
  assert.deepEqual([...parseHex('0e2eff').bytes], [0x0e, 0x2e, 0xff]);
  assert.deepEqual([...parseHex('  ').bytes], []);
  assert.equal(parseHex('0G').error, 'badChar');
  assert.equal(parseHex('ABC').error, 'oddLength');
  assert.equal(parseHex('00 '.repeat(600)).error, 'tooLong');
  assert.equal(parseHex(42).ok, false);
  assert.equal(toHex(Uint8Array.of(1, 171)), '01 AB');
});

test('ByteReader 越界返回 null，不抛异常', () => {
  const r = new ByteReader(Uint8Array.of(1, 2, 3));
  assert.equal(r.u16(), 0x0201); assert.equal(r.u16(), null); assert.equal(r.u8(), 3); assert.equal(r.u8(), null);
  assert.equal(new ByteReader(Uint8Array.of(0xff, 0xff, 0xff, 0xff)).u32(), 4294967295);
  assert.equal(r.take(1), null);
});
