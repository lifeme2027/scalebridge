// 读取仓库根目录 shared-vectors/*.json（与桌面 Core 的 SharedVectorsTests 是同一批文件），逐条验证 JS 解析器：
// reject / errorCode / warnings（集合比较）/ expect（全部键、类型与十进制数值）。
import test from 'node:test';
import assert from 'node:assert/strict';
import { loadVectorFiles } from '../testlib/helpers.js';
import { parseHex } from '../src/core/bytes.js';
import { parseCharacteristic } from '../src/core/protocol.js';
import { DEC_RE, equals } from '../src/core/decimal.js';

/** 深度比较：整数/布尔/null 必须严格相等；十进制字符串按数值比较（"70.005" 等于 "70.0050"）；对象键集合必须完全一致。 */
export function diff(expected, actual, path = '$') {
  if (expected === null || actual === null) return expected === actual ? null : `${path}: expected ${JSON.stringify(expected)}, got ${JSON.stringify(actual)}`;
  if (typeof expected !== typeof actual) return `${path}: type ${typeof expected} vs ${typeof actual} (${JSON.stringify(expected)} vs ${JSON.stringify(actual)})`;
  if (typeof expected === 'string') {
    if (expected === actual) return null;
    if (DEC_RE.test(expected) && DEC_RE.test(actual) && equals(expected, actual)) return null;
    return `${path}: expected ${JSON.stringify(expected)}, got ${JSON.stringify(actual)}`;
  }
  if (typeof expected === 'object') {
    const ek = Object.keys(expected).sort(), ak = Object.keys(actual).sort();
    if (JSON.stringify(ek) !== JSON.stringify(ak)) return `${path}: keys ${ek} vs ${ak}`;
    for (const k of ek) { const d = diff(expected[k], actual[k], `${path}.${k}`); if (d) return d; }
    return null;
  }
  return expected === actual ? null : `${path}: expected ${expected}, got ${actual}`;
}

const files = loadVectorFiles();
const all = files.flatMap(({ file, doc }) => doc.vectors.map((v) => ({ file, doc, v })));

test('向量文件自描述、名字唯一、数量不少于 235', () => {
  assert.ok(files.length >= 2);
  const names = new Set();
  for (const { file, doc } of files) {
    assert.equal(doc.format, 'scalebridge-vectors/1', file);
    assert.ok(doc.suite, file);
    for (const v of doc.vectors) {
      for (const k of ['name', 'service', 'characteristic', 'hex', 'reject']) assert.ok(k in v, `${file}: ${v.name} missing ${k}`);
      assert.ok(!names.has(v.name), `duplicate name ${v.name}`);
      names.add(v.name);
      if (v.reject) assert.ok('errorCode' in v); else assert.ok('expect' in v && 'warnings' in v);
    }
  }
  assert.ok(names.size >= 235, `only ${names.size} vectors`);
});

for (const { file, v } of all) {
  test(`${file}: ${v.name}`, () => {
    const h = parseHex(v.hex);
    assert.ok(h.ok, `bad hex in vector: ${v.hex}`);
    const r = parseCharacteristic(parseInt(v.service, 16), parseInt(v.characteristic, 16), h.bytes);
    if (v.reject) {
      assert.equal(r.ok, false, 'expected rejection');
      assert.equal(r.errorCode, v.errorCode);
      return;
    }
    assert.equal(r.ok, true, `expected success but got ${r.errorCode}: ${r.message}`);
    assert.deepEqual([...r.warnings].sort(), [...v.warnings].sort(), 'warnings');
    const d = diff(v.expect, r.value);
    assert.equal(d, null, d ?? '');
  });
}
