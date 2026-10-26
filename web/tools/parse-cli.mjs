#!/usr/bin/env node
// ScaleBridge JS 解析器命令行：用于与桌面解析器（tools/ParseCli）交叉比对。
// 实验性（仅按规范实现，未真机验证）。
//
// 单条：
//   node tools/parse-cli.mjs --service 181D --char 2A9D --hex "00 59 36"
// 批量（scalebridge-vectors/1 格式的文件或目录，目录只读其中顶层 *.json）：
//   node tools/parse-cli.mjs --file ../shared-vectors/weight-scale.json
//   node tools/parse-cli.mjs --dir ../shared-vectors
// 加 --check：把结果与文件里的 reject/errorCode/warnings(集合)/expect(十进制按数值比较) 对照，不一致则退出码 1。
//
// 输出（stdout，JSON）与向量里的期望同形：
//   成功：{"reject":false,"warnings":[...],"expect":{...}}
//   拒绝：{"reject":true,"errorCode":"TooShort"}
//   批量：[{"name":"...","reject":...,...}, ...]（顺序同文件；每项多一个 name）
// 厂商向量（带 frameType 的 shared-vectors/vendor/*）：Web 版本阶段不实现广播类厂商协议，
// 输出 {"name":...,"unsupported":true,"reason":"vendor protocol not implemented in Web"}，--check 时计为 skipped。
// 退出码：0 正常；1 --check 有不一致；2 参数错误/读文件失败。

import { readFileSync, readdirSync, statSync } from 'node:fs';
import { join } from 'node:path';
import { parseHex } from '../src/core/bytes.js';
import { parseCharacteristic } from '../src/core/protocol.js';
import { DEC_RE, equals } from '../src/core/decimal.js';

function usage(msg) {
  if (msg) process.stderr.write(`parse-cli: ${msg}\n`);
  process.stderr.write('usage:\n  parse-cli.mjs --service 181D --char 2A9D --hex "00 59 36"\n  parse-cli.mjs (--file <vectors.json> | --dir <dir>) [--check]\n');
  process.exit(2);
}

function parseArgs(argv) {
  const o = {};
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    if (a === '--check') { o.check = true; continue; }
    if (!['--service', '--char', '--hex', '--file', '--dir'].includes(a)) usage(`unknown argument ${a}`);
    if (i + 1 >= argv.length) usage(`${a} needs a value`);
    o[a.slice(2)] = argv[++i];
  }
  return o;
}

const uuid16 = (s) => (/^(0x)?[0-9a-fA-F]{1,4}$/.test(s ?? '') ? parseInt(s.replace(/^0x/i, ''), 16) : null);

/** 返回与向量期望同形的对象。 */
export function run(service, characteristic, hexText) {
  const h = parseHex(hexText);
  if (!h.ok) return { reject: true, errorCode: 'BadHex', message: h.error };
  const r = parseCharacteristic(service, characteristic, h.bytes);
  if (!r.ok) return { reject: true, errorCode: r.errorCode };
  return { reject: false, warnings: [...r.warnings].sort(), expect: r.value };
}

function same(e, a) {
  if (e === null || a === null) return e === a;
  if (typeof e !== typeof a) return false;
  if (typeof e === 'string') return e === a || (DEC_RE.test(e) && DEC_RE.test(a) && equals(e, a));
  if (typeof e === 'object') {
    const ek = Object.keys(e).sort(), ak = Object.keys(a).sort();
    return ek.length === ak.length && ek.every((k, i) => k === ak[i] && same(e[k], a[k]));
  }
  return e === a;
}

function matches(v, out) {
  if (v.reject !== out.reject) return false;
  if (v.reject) return v.errorCode === out.errorCode;
  return JSON.stringify([...v.warnings].sort()) === JSON.stringify(out.warnings) && same(v.expect, out.expect);
}

function batch(files, check) {
  const results = [];
  let mismatches = 0, skipped = 0;
  for (const f of files) {
    let doc;
    try { doc = JSON.parse(readFileSync(f, 'utf8')); } catch (e) { usage(`cannot read ${f}: ${e.message}`); }
    if (doc.format !== 'scalebridge-vectors/1' || !Array.isArray(doc.vectors)) usage(`${f}: not a scalebridge-vectors/1 file`);
    for (const v of doc.vectors) {
      const s = uuid16(v.service), c = uuid16(v.characteristic);
      if (v.frameType !== undefined || s === null || c === null) {
        skipped++;
        results.push({ name: v.name, unsupported: true, reason: 'vendor protocol not implemented in Web' });
        continue;
      }
      const out = run(s, c, v.hex);
      if (check && !matches(v, out)) { mismatches++; process.stderr.write(`MISMATCH ${v.name}\n`); }
      results.push({ name: v.name, ...out });
    }
  }
  return { results, mismatches, skipped };
}

const o = parseArgs(process.argv.slice(2));
if (o.hex !== undefined || o.service !== undefined || o.char !== undefined) {
  if (o.file || o.dir) usage('use either single-vector flags or --file/--dir');
  const s = uuid16(o.service), c = uuid16(o.char);
  if (s === null || c === null || o.hex === undefined) usage('--service, --char and --hex are all required');
  process.stdout.write(JSON.stringify(run(s, c, o.hex), null, 2) + '\n');
} else if (o.file || o.dir) {
  let files;
  if (o.file) files = [o.file];
  else {
    try { if (!statSync(o.dir).isDirectory()) throw new Error('not a directory'); } catch (e) { usage(`${o.dir}: ${e.message}`); }
    files = readdirSync(o.dir).filter((n) => n.endsWith('.json')).sort().map((n) => join(o.dir, n));
  }
  const { results, mismatches, skipped } = batch(files, o.check);
  process.stdout.write(JSON.stringify(results, null, 2) + '\n');
  if (o.check) {
    process.stderr.write(`checked ${results.length - skipped} vectors, ${mismatches} mismatches, ${skipped} skipped (vendor)\n`);
    if (mismatches > 0) process.exit(1);
  }
} else usage();
