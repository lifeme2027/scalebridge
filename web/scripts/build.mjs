#!/usr/bin/env node
// 构建：src/ → dist/。零依赖。
//   * JS/CSS 文件名带内容哈希（name.<10位>.js），模块之间的 import 说明符按依赖拓扑顺序改写成哈希名；
//   * HTML 里的 /box/xxx.js|css 改写成哈希名（用绝对路径：CSP 的 base-uri 'none' 禁止 <base>，且 /box 与 /box/ 都要能打开）；
//   * 生成 build-info.js（版本 + 提交号）与 build-info.json（文件清单，供运维核对）；
//   * 构建后自检：无内联脚本/样式/事件属性，import 全部能解析。
import { createHash } from 'node:crypto';
import { cpSync, existsSync, mkdirSync, readFileSync, readdirSync, rmSync, writeFileSync } from 'node:fs';
import { dirname, join, posix, relative, resolve, sep } from 'node:path';
import { fileURLToPath } from 'node:url';
import { execFileSync } from 'node:child_process';

const WEB = resolve(dirname(fileURLToPath(import.meta.url)), '..');

const sha = (buf) => createHash('sha256').update(buf).digest('hex');
const walk = (dir) => readdirSync(dir, { withFileTypes: true }).flatMap((d) => (d.isDirectory() ? walk(join(dir, d.name)) : [join(dir, d.name)]));

export function build({ srcDir = join(WEB, 'src'), outDir = join(WEB, 'dist'), commit } = {}) {
  const pkg = JSON.parse(readFileSync(join(WEB, 'package.json'), 'utf8'));
  if (commit === undefined) {
    commit = process.env.BUILD_COMMIT;
    if (!commit) {
      try { commit = execFileSync('git', ['rev-parse', '--short=12', 'HEAD'], { cwd: WEB, encoding: 'utf8', stdio: ['ignore', 'pipe', 'ignore'] }).trim(); } catch { commit = 'unknown'; }
    }
  }
  rmSync(outDir, { recursive: true, force: true });
  mkdirSync(outDir, { recursive: true });

  const files = walk(srcDir).map((f) => relative(srcDir, f).split(sep).join('/')).sort();
  const text = new Map(files.map((f) => [f, readFileSync(join(srcDir, f), 'utf8')]));
  // build-info.js：在 dist 里替换成真实值
  text.set('build-info.js', `export const BUILD = Object.freeze({ version: ${JSON.stringify(pkg.version)}, commit: ${JSON.stringify(commit)} });\n`);

  const jsFiles = files.filter((f) => f.endsWith('.js'));
  const IMPORT_RE = /(\bfrom\s*|\bimport\s*)(['"])(\.{1,2}\/[^'"]+)\2/g;
  for (const f of jsFiles) {
    if (/\bimport\s*\(/.test(text.get(f))) throw new Error(`${f}: 动态 import() 不受支持（哈希改写只处理静态 import）`);
  }
  const deps = new Map(jsFiles.map((f) => [f, [...text.get(f).matchAll(IMPORT_RE)].map((m) => posix.normalize(posix.join(posix.dirname(f), m[3])))]));
  for (const [f, ds] of deps) for (const d of ds) if (!text.has(d)) throw new Error(`${f}: import 的 ${d} 不存在`);

  const hashed = new Map(); // 源路径 → 输出路径
  const outText = new Map();
  const visiting = new Set();
  const withHash = (f, h) => f.replace(/\.(js|css)$/, `.${h}.$1`);
  function visit(f) {
    if (hashed.has(f)) return;
    if (visiting.has(f)) throw new Error(`循环 import：${f}`);
    visiting.add(f);
    for (const d of deps.get(f)) visit(d);
    visiting.delete(f);
    const body = text.get(f).replace(IMPORT_RE, (m, kw, q, spec) => {
      const target = posix.normalize(posix.join(posix.dirname(f), spec));
      const out = hashed.get(target);
      let rel = posix.relative(posix.dirname(f), out);
      if (!rel.startsWith('.')) rel = './' + rel;
      return `${kw}${q}${rel}${q}`;
    });
    const out = withHash(f, sha(body).slice(0, 10));
    hashed.set(f, out);
    outText.set(out, body);
  }
  for (const f of jsFiles) visit(f);

  for (const f of files.filter((x) => x.endsWith('.css'))) {
    const out = withHash(f, sha(text.get(f)).slice(0, 10));
    hashed.set(f, out);
    outText.set(out, text.get(f));
  }
  for (const f of files.filter((x) => x.endsWith('.html'))) {
    const body = text.get(f).replace(/(["'])\/box\/([\w./-]+\.(?:js|css))\1/g, (m, q, p) => {
      if (!hashed.has(p)) throw new Error(`${f}: 引用了不存在的 /box/${p}`);
      return `${q}/box/${hashed.get(p)}${q}`;
    });
    outText.set(f, body);
  }
  // 其它文件原样复制
  for (const f of files) if (!hashed.has(f) && !outText.has(f)) outText.set(f, text.get(f));

  const manifest = [];
  for (const [out, body] of [...outText].sort(([a], [b]) => (a < b ? -1 : 1))) {
    const p = join(outDir, out);
    mkdirSync(dirname(p), { recursive: true });
    writeFileSync(p, body);
    manifest.push({ path: out, sha256: sha(body), bytes: Buffer.byteLength(body) });
  }
  const info = { name: pkg.name, version: pkg.version, commit, source: 'https://github.com/composehk/scalebridge/tree/main/web', files: manifest };
  writeFileSync(join(outDir, 'build-info.json'), JSON.stringify(info, null, 2) + '\n');
  verify(outDir);
  return { outDir, files: manifest.length + 1, commit };
}

/** 构建产物自检（失败抛异常）。 */
export function verify(dir) {
  const all = walk(dir).map((f) => relative(dir, f).split(sep).join('/'));
  for (const f of all.filter((x) => x.endsWith('.html'))) {
    const h = readFileSync(join(dir, f), 'utf8');
    if (/<script(?![^>]*\bsrc=)/i.test(h)) throw new Error(`${f}: 内联 <script>`);
    if (/<style\b/i.test(h)) throw new Error(`${f}: 内联 <style>`);
    if (/\son[a-z]+\s*=/i.test(h)) throw new Error(`${f}: 内联事件处理属性`);
    if (/\sstyle\s*=/i.test(h)) throw new Error(`${f}: 内联 style 属性`);
    if (/<base\b/i.test(h)) throw new Error(`${f}: <base> 被 CSP base-uri 'none' 禁止`);
    for (const m of h.matchAll(/(?:src|href)="(\/box\/[^"#?]*)"/g)) {
      const rel = m[1].slice('/box/'.length);
      if (rel !== '' && !existsSync(join(dir, rel))) throw new Error(`${f}: 引用的 ${m[1]} 不存在`);
    }
  }
  for (const f of all.filter((x) => x.endsWith('.js'))) {
    const body = readFileSync(join(dir, f), 'utf8');
    for (const m of body.matchAll(/(?:from\s*|import\s*)(['"])(\.{1,2}\/[^'"]+)\1/g)) {
      if (!existsSync(join(dir, dirname(f), m[2]))) throw new Error(`${f}: import ${m[2]} 不存在`);
    }
  }
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const r = build();
  console.log(`built ${r.files} files into ${relative(process.cwd(), r.outDir) || '.'} (commit ${r.commit})`);
}
