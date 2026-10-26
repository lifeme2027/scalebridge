#!/usr/bin/env node
// 把构建产物同步到站点仓库（consumption-tracker-web）的 wwwroot/box。
//   node scripts/sync-to-site.mjs <站点仓库路径>          构建并同步（先清空 wwwroot/box，再复制）
//   node scripts/sync-to-site.mjs <站点仓库路径> --check  只比较：站点里的副本与当前构建是否逐字节一致（不一致退出码 1）
// 站点仓库里提交的是构建产物（它的 CI 与发布包需要它），源码只在本仓库的 web/。
import { cpSync, existsSync, mkdirSync, readFileSync, readdirSync, rmSync } from 'node:fs';
import { dirname, join, relative, resolve, sep } from 'node:path';
import { fileURLToPath } from 'node:url';
import { build } from './build.mjs';

const WEB = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const args = process.argv.slice(2);
const check = args.includes('--check');
const siteArg = args.find((a) => !a.startsWith('--'));
if (!siteArg) { console.error('usage: sync-to-site.mjs <consumption-tracker-web path> [--check]'); process.exit(2); }
const site = resolve(siteArg);
const wwwroot = join(site, 'src', 'ConsumptionTracker.Web', 'wwwroot');
if (!existsSync(wwwroot)) { console.error(`not a consumption-tracker-web checkout: ${wwwroot} missing`); process.exit(2); }
const target = join(wwwroot, 'box');
const dist = join(WEB, 'dist');
const walk = (d) => (existsSync(d) ? readdirSync(d, { withFileTypes: true }).flatMap((e) => (e.isDirectory() ? walk(join(d, e.name)) : [join(d, e.name)])) : []);
const rel = (base, f) => relative(base, f).split(sep).join('/');

const r = build({ outDir: dist });
if (check) {
  const a = new Map(walk(dist).map((f) => [rel(dist, f), readFileSync(f)]));
  const b = new Map(walk(target).map((f) => [rel(target, f), readFileSync(f)]));
  const diff = [...new Set([...a.keys(), ...b.keys()])].filter((k) => !a.has(k) || !b.has(k) || !a.get(k).equals(b.get(k)));
  if (diff.length) { console.error(`wwwroot/box differs from the build in ${diff.length} file(s):\n  ${diff.join('\n  ')}`); process.exit(1); }
  console.log(`wwwroot/box matches the build (${a.size} files)`);
} else {
  rmSync(target, { recursive: true, force: true });
  mkdirSync(target, { recursive: true });
  cpSync(dist, target, { recursive: true });
  console.log(`synced ${r.files} files (commit ${r.commit}) to ${target}`);
}
