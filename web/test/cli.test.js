import test from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { join } from 'node:path';
import { WEB_DIR, VECTORS_DIR } from '../testlib/helpers.js';

const CLI = join(WEB_DIR, 'tools/parse-cli.mjs');
const node = (...args) => spawnSync(process.execPath, [CLI, ...args], { encoding: 'utf8' });

test('CLI 单条：成功与拒绝，输出与向量期望同形', () => {
  const ok = node('--service', '181D', '--char', '2A9D', '--hex', '00 59 36');
  assert.equal(ok.status, 0);
  const o = JSON.parse(ok.stdout);
  assert.equal(o.reject, false); assert.deepEqual(o.warnings, []);
  assert.deepEqual(o.expect.weight, { raw: 13913, original: '69.565', kilograms: '69.565' });
  const bad = JSON.parse(node('--service', '181D', '--char', '2A9D', '--hex', '00 59').stdout);
  assert.deepEqual(bad, { reject: true, errorCode: 'TooShort' });
  const mp = JSON.parse(node('--service', '181B', '--char', '2A9C', '--hex', '00 10 00 00').stdout);
  assert.equal(mp.errorCode, 'MultiPacketNotSupported');
});

test('CLI 批量 --check：标准向量全部一致，退出码 0', () => {
  const r = node('--dir', VECTORS_DIR, '--check');
  assert.equal(r.status, 0, r.stderr);
  assert.match(r.stderr, /checked \d+ vectors, 0 mismatches/);
  const arr = JSON.parse(r.stdout);
  assert.ok(Array.isArray(arr) && arr.length >= 235 && arr.every((x) => typeof x.name === 'string'));
});

test('CLI 参数错误退出码 2；坏 hex 给出 BadHex', () => {
  assert.equal(node('--service', '181D').status, 2);
  assert.equal(node('--bogus').status, 2);
  assert.equal(node('--file', '/nonexistent.json').status, 2);
  assert.equal(JSON.parse(node('--service', '181D', '--char', '2A9D', '--hex', 'ZZ').stdout).errorCode, 'BadHex');
});
