import { readFileSync } from 'node:fs';
import path from 'node:path';
import { describe, expect, it } from 'vitest';
import {
  deliveryCsp, deliveryCspOnPreview, missingPdfjsAssets, NGINX_CONF, PDFJS_ASSET_DIRS, pdfjsAssetFiles,
} from './viteSupport';

/**
 * Сторожа поставки просмотрщика PDF (issue #1490, [UI-21], [UI-22]). Обе поломки CI сам по себе не
 * видит: dev-сервер и `vite preview` берут файлы из `node_modules` и работают без политики поставки.
 */

const WORKER = readFileSync(path.resolve(__dirname, 'node_modules/pdfjs-dist/build/pdf.worker.mjs'), 'utf8');
const shipped = new Set(PDFJS_ASSET_DIRS.flatMap(dir => pdfjsAssetFiles(dir)));

describe('сторож упаковки pdf.js', () => {
  it('всё, что worker просит по wasmUrl и iccUrl, едет в поставку', () => {
    expect(missingPdfjsAssets(WORKER, shipped)).toEqual([]);
  });

  it('видит, что просят, — иначе он молча зелёный', () => {
    expect(missingPdfjsAssets(WORKER, new Set())).toEqual(expect.arrayContaining([
      'jbig2.wasm', 'jbig2_nowasm_fallback.js', 'openjpeg.wasm', 'qcms_bg.wasm', 'CGATS001Compat-v2-micro.icc',
    ]));
  });

  it('новый файл в worker — красный с его именем', () => {
    expect(missingPdfjsAssets(`${WORKER}\nconst x = \`\${wasmUrl}brand_new.wasm\`;`, shipped)).toEqual(['brand_new.wasm']);
  });

  it('исполнение скриптов PDF не едет', () => {
    expect([...shipped].filter(f => f.startsWith('quickjs'))).toEqual([]);
  });

  it('шрифты без встраивания: cmaps и стандартные шрифты на месте', () => {
    expect(shipped).toContain('UniJIS-UTF16-H.bcmap');
    expect(shipped).toContain('LiberationSans-Regular.ttf');
  });
});

describe('политика поставки в vite preview', () => {
  const conf = readFileSync(NGINX_CONF, 'utf8');

  it('строка разбирается и та, что ломает просмотр PDF адресом', () => {
    expect(deliveryCsp(conf)).toContain("connect-src 'self'");
  });

  it('preview отдаёт ровно строку nginx.conf', () => {
    const headers: Record<string, string> = {};
    let use: ((req: unknown, res: unknown, next: () => void) => void) | undefined;
    const hook = deliveryCspOnPreview().configurePreviewServer as (s: unknown) => void;
    hook({ middlewares: { use: (fn: typeof use) => { use = fn; } } });
    let passed = false;
    use!({}, { setHeader: (k: string, v: string) => { headers[k] = v; } }, () => { passed = true; });
    expect(headers['Content-Security-Policy']).toBe(deliveryCsp(conf));
    expect(passed).toBe(true);
  });

  it('строки нет — отказ, а не preview без политики', () => {
    expect(() => deliveryCsp('server { listen 8080; }')).toThrow(/Content-Security-Policy/);
  });
});
