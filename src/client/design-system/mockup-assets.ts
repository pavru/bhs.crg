import { readFileSync } from 'node:fs';
import { inflateRawSync } from 'node:zlib';
import { resolve } from 'node:path';
import type { Plugin } from 'vite';

/** Read only central-directory ZIP entries; paths are looked up, never resolved on disk. */
export function readMockupArchive(path: string) {
  const zip = readFileSync(path);
  let end = zip.length - 22;
  while (end >= 0 && zip.readUInt32LE(end) !== 0x06054b50) end--;
  if (end < 0) throw Error('Invalid mockup source archive');
  let offset = zip.readUInt32LE(end + 16);
  const files = new Map<string, Buffer>();
  for (let entry = 0; entry < zip.readUInt16LE(end + 10); entry++) {
    if (zip.readUInt32LE(offset) !== 0x02014b50) throw Error('Invalid ZIP directory');
    const method = zip.readUInt16LE(offset + 10);
    const length = zip.readUInt32LE(offset + 20);
    const nameLength = zip.readUInt16LE(offset + 28);
    const name = zip.toString('utf8', offset + 46, offset + 46 + nameLength);
    const local = zip.readUInt32LE(offset + 42);
    const start = local + 30 + zip.readUInt16LE(local + 26) + zip.readUInt16LE(local + 28);
    const compressed = zip.subarray(start, start + length);
    if (method !== 0 && method !== 8) throw Error('Unsupported ZIP compression');
    const content = method === 8 ? inflateRawSync(compressed) : compressed;
    // Exported font files were often absent. The host supplies the same fonts locally.
    files.set(name, name.endsWith('.css') ? Buffer.from(content.toString('utf8').replace(/@font-face\s*\{[^}]*\}/gi, '').replace(/@import\s+(?:url\()?['"]?https?:[^;]+;/gi, '')) : content);
    offset += 46 + nameLength + zip.readUInt16LE(offset + 30) + zip.readUInt16LE(offset + 32);
  }
  return files;
}

const types: Record<string, string> = { html: 'text/html; charset=utf-8', css: 'text/css; charset=utf-8', js: 'text/javascript; charset=utf-8', json: 'application/json', jpg: 'image/jpeg', png: 'image/png', svg: 'image/svg+xml', woff2: 'font/woff2' };
export function mockupAssets(root: string): Plugin {
  const archive = resolve(root, 'mockups/sources.zip');
  let files: Map<string, Buffer>;
  const load = () => files ??= readMockupArchive(archive);
  return {
    name: 'restored-mockup-assets',
    configureServer(server) {
      server.watcher.add(archive);
      server.watcher.on('change', path => { if (path === archive) files = undefined!; });
      server.middlewares.use('/mockups', (request, response, next) => {
        let path: string;
        try { path = decodeURIComponent((request.url ?? '').split('?')[0]).replace(/^\//, ''); }
        catch { response.statusCode = 400; response.end(); return; }
        if (!/^[0-9a-f-]{36}\//.test(path)) { next(); return; }
        const file = load().get(path);
        if (!file) { response.statusCode = 404; response.end('Not in restored archive'); return; }
        response.setHeader('Content-Type', types[path.split('.').pop()!] ?? 'application/octet-stream');
        response.end(file);
      });
    },
    generateBundle() {
      for (const [name, source] of load()) this.emitFile({ type: 'asset', fileName: `mockups/${name}`, source });
    },
  };
}
