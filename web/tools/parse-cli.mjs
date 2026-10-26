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
// 标准输入（与桌面 ParseCli 同一约定；每行一条，空行与 # 开头的行忽略，输出 JSON Lines）：
//   echo "181D/2A9D 00 59 36" | node tools/parse-cli.mjs --stdin
//   echo "00 59 36"           | node tools/parse-cli.mjs --stdin --service 181D --char 2A9D
// 别名（与桌面对齐）：--vectors = --file（可重复），--compare = --check，--compact = 紧凑输出。
// 单条/stdin 的输出另带 service、characteristic、hex（规整成大写、空格分隔）三个字段，其余同上。
// 退出码：0 正常；1 --check 有不一致；2 参数错误/读文件失败/stdin 里有坏行。

import { readFileSync, readdirSync, statSync } from 'node:fs';
import { join } from 'node:path';
import { parseHex } from '../src/core/bytes.js';
import { parseCharacteristic } from '../src/core/protocol.js';
import { DEC_RE, equals } from '../src/core/decimal.js';

function usage(msg) {
  if (msg) process.stderr.write(`parse-cli: ${msg}\n`);
  process.stderr.write('usage:\n  parse-cli.mjs --service 181D --char 2A9D --hex "00 59 36"\n  parse-cli.mjs --stdin [--service 181D --char 2A9D]   (lines: \"181D/2A9D 00 59 36\")\n  parse-cli.mjs (--file <vectors.json> | --dir <dir>) [--check]\n');
  process.exit(2);
}

function parseArgs(argv) {
  const o = { files: [] };
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    if (a === '--check' || a === '--compare') { o.check = true; continue; }
    if (a === '--stdin') { o.stdin = true; continue; }
    if (a === '--compact') { o.compact = true; continue; }
    if (!['--service', '--char', '--hex', '--file', '--vectors', '--dir'].includes(a)) usage(`unknown argument ${a}`);
    if (i + 1 >= argv.length) usage(`${a} needs a value`);
    const v = argv[++i];
    if (a === '--file' || a === '--vectors') o.files.push(v); else o[a.slice(2)] = v;
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

const hex4 = (n) => n.toString(16).toUpperCase().padStart(4, '0');
const fmtHex = (bytes) => [...bytes].map((b) => b.toString(16).toUpperCase().padStart(2, '0')).join(' ');

/** 单条/stdin 的完整输出：service/characteristic/hex + run() 的结果（与桌面 ParseCli 同形的超集）。 */
function describe(service, characteristic, hexText) {
  const h = parseHex(hexText);
  return { service: hex4(service), characteristic: hex4(characteristic), hex: h.ok ? fmtHex(h.bytes) : String(hexText), ...run(service, characteristic, hexText) };
}

const SUPPORTED = new Set(['181D/2A9E', '181D/2A9D', '181B/2A9B', '181B/2A9C']);

function runStdin(text, defService, defChar, compact) {
  let status = 0;
  const lines = text.split(/\r?\n/);
  lines.forEach((raw, idx) => {
    const t = raw.trim();
    if (t === '' || t.startsWith('#')) return;
    let service = defService, characteristic = defChar, hexPart = t;
    const sp = t.search(/\s/);
    const head = sp < 0 ? t : t.slice(0, sp);
    if (head.includes('/')) {
      const [a, b] = head.split('/');
      service = uuid16(a); characteristic = uuid16(b);
      hexPart = sp < 0 ? '' : t.slice(sp + 1);
    }
    const fail = (msg) => {
      process.stdout.write(JSON.stringify({ line: idx + 1, cliError: msg }) + '\n');
      process.stderr.write(`line ${idx + 1}: ${msg}\n`);
      status = 2;
    };
    if (service === null || service === undefined || characteristic === null || characteristic === undefined) return fail("no service/characteristic (use --service/--char or 'SERVICE/CHAR hex' per line)");
    if (!SUPPORTED.has(`${hex4(service)}/${hex4(characteristic)}`)) return fail(`unsupported service/characteristic ${hex4(service)}/${hex4(characteristic)}`);
    const out = describe(service, characteristic, hexPart);
    if (out.errorCode === 'BadHex') return fail(`bad hex: ${out.message}`);
    process.stdout.write(JSON.stringify(out, null, compact ? 0 : undefined) + '\n');
  });
  return status;
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
if (o.stdin) {
  if (o.hex !== undefined || o.files.length || o.dir) usage('--stdin cannot be combined with --hex/--file/--dir');
  const text = readFileSync(0, 'utf8');
  process.exit(runStdin(text, uuid16(o.service), uuid16(o.char), true));
} else if (o.hex !== undefined || o.service !== undefined || o.char !== undefined) {
  if (o.files.length || o.dir) usage('use either single-vector flags or --file/--dir');
  const s = uuid16(o.service), c = uuid16(o.char);
  if (s === null || c === null || o.hex === undefined) usage('--service, --char and --hex are all required');
  process.stdout.write(JSON.stringify(describe(s, c, o.hex), null, o.compact ? 0 : 2) + '\n');
} else if (o.files.length || o.dir) {
  let files;
  if (o.files.length) files = o.files;
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
