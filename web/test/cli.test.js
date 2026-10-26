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
  assert.deepEqual(bad, { service: '181D', characteristic: '2A9D', hex: '00 59', reject: true, errorCode: 'TooShort' });
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

const node2 = (input, ...args) => spawnSync(process.execPath, [CLI, ...args], { encoding: 'utf8', input });

test('CLI --stdin：JSON Lines，行内 SERVICE/CHAR 或默认值，忽略空行与注释；坏行输出 cliError 且退出码 2', () => {
  const ok = node2('181D/2A9D 00 59 36\n# 注释\n\n181B/2A9C 00\r\n', '--stdin');
  assert.equal(ok.status, 0, ok.stderr);
  const rows = ok.stdout.trim().split('\n').map((l) => JSON.parse(l));
  assert.equal(rows.length, 2);
  assert.equal(rows[0].expect.weight.kilograms, '69.565');
  assert.deepEqual(rows[1], { service: '181B', characteristic: '2A9C', hex: '00', reject: true, errorCode: 'TooShort' });
  assert.equal(ok.stdout.trim().split('\n')[0].includes('\n'), false, '紧凑单行');

  const def = node2('00 59 36\n', '--stdin', '--service', '0x181d', '--char', '2a9d');
  assert.equal(JSON.parse(def.stdout).hex, '00 59 36');

  const bad = node2('00 59 36\n181D/2A9D zz\n181D/2A99 00\n', '--stdin');
  assert.equal(bad.status, 2);
  const lines = bad.stdout.trim().split('\n').map((l) => JSON.parse(l));
  assert.deepEqual(lines.map((l) => l.line), [1, 2, 3]);
  assert.ok(lines.every((l) => typeof l.cliError === 'string'));
  assert.equal(node2('x', '--stdin', '--hex', '00').status, 2);
});

test('CLI 别名 --vectors/--compare 与桌面一致', () => {
  const r = node('--vectors', join(VECTORS_DIR, 'weight-scale.json'), '--compare');
  assert.equal(r.status, 0, r.stderr);
  assert.match(r.stderr, /0 mismatches/);
});
