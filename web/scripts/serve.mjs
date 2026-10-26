#!/usr/bin/env node
// 本地预览服务器：用与线上相同的安全头（CSP + Permissions-Policy）在 /box 下提供 dist/。
//   node scripts/serve.mjs [--port 8080] [--dir dist]
// 注意：Web Bluetooth 要求安全上下文；http://localhost 算安全上下文。
import { createServer } from 'node:http';
import { readFileSync, statSync } from 'node:fs';
import { dirname, extname, join, normalize, resolve, sep } from 'node:path';
import { fileURLToPath } from 'node:url';
import { BOX_CSP, BOX_PERMISSIONS_POLICY } from './headers.mjs';

const WEB = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const TYPES = { '.html': 'text/html; charset=utf-8', '.js': 'text/javascript; charset=utf-8', '.css': 'text/css; charset=utf-8', '.json': 'application/json; charset=utf-8' };

export function makeServer(root) {
  return createServer((req, res) => {
    const send = (code, body, type = 'text/plain; charset=utf-8', extra = {}) => {
      res.writeHead(code, {
        'Content-Type': type,
        'Content-Security-Policy': BOX_CSP,
        'Permissions-Policy': BOX_PERMISSIONS_POLICY,
        'X-Content-Type-Options': 'nosniff',
        'Referrer-Policy': 'no-referrer',
        ...extra,
      });
      res.end(req.method === 'HEAD' ? undefined : body);
    };
    if (req.method !== 'GET' && req.method !== 'HEAD') return send(405, 'method not allowed');
    let path;
    try { path = decodeURIComponent(new URL(req.url, 'http://x').pathname); } catch { return send(400, 'bad request'); }
    if (path === '/') { res.writeHead(302, { Location: '/box/' }); return res.end(); }
    if (path !== '/box' && !path.startsWith('/box/')) return send(404, 'not found');
    let rel = path.slice('/box'.length).replace(/^\/+/, '');
    if (rel === '') rel = 'index.html';
    const file = normalize(join(root, rel));
    if (file !== root && !file.startsWith(root + sep)) return send(404, 'not found');
    try {
      if (!statSync(file).isFile()) throw new Error('not a file');
      const hashed = /\.[0-9a-f]{10}\.(js|css)$/.test(file);
      send(200, readFileSync(file), TYPES[extname(file)] ?? 'application/octet-stream', {
        'Cache-Control': hashed ? 'public, max-age=31536000, immutable' : 'no-cache',
      });
    } catch { send(404, 'not found'); }
  });
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const arg = (n, d) => { const i = process.argv.indexOf(n); return i > 0 ? process.argv[i + 1] : d; };
  const port = Number(arg('--port', '8080'));
  const root = resolve(WEB, arg('--dir', 'dist'));
  makeServer(root).listen(port, '127.0.0.1', () => console.log(`serving ${root} at http://127.0.0.1:${port}/box/`));
}
