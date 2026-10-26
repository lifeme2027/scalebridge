import { readFileSync, readdirSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

export const WEB_DIR = join(dirname(fileURLToPath(import.meta.url)), '..');
export const REPO_DIR = join(WEB_DIR, '..');
/** 与桌面 Core 共用的同一份向量目录（不复制到 web/）。 */
export const VECTORS_DIR = join(REPO_DIR, 'shared-vectors');

export function loadVectorFiles() {
  return readdirSync(VECTORS_DIR).filter((f) => f.endsWith('.json')).sort()
    .map((f) => ({ file: f, doc: JSON.parse(readFileSync(join(VECTORS_DIR, f), 'utf8')) }));
}
