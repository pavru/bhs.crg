import { createReadStream, existsSync, readdirSync, readFileSync, statSync } from 'node:fs';
import path from 'node:path';
import type { Plugin } from 'vite';

/**
 * Помощники сборки, которым нужен Node: файлы pdf.js рядом с библиотекой и политика содержимого
 * поставки для `vite preview`. Отдельно от `vite.config.ts`, чтобы их проверял тест, а не только
 * сборка (viteSupport.test.ts).
 */

const PDFJS_ROOT = path.resolve(__dirname, 'node_modules/pdfjs-dist');

/**
 * Каталоги пакета, которые просит движок pdf.js по ходу разбора (issue #1490, D8 зонтика viewer):
 * - `wasm/` — декодеры JBIG2, CCITT, JPEG 2000 и цвета. Без них чёрно-белый скан с МФУ выходит
 *   ПУСТЫМ листом и без ошибки;
 * - `iccs/` — профиль CMYK;
 * - `cmaps/` и `standard_fonts/` — кириллица в PDF без встроенных шрифтов (счета из 1С).
 *
 * С CDN их не берём: у заказчика сети может не быть, а на машине разработчика с сетью всё «зелёное».
 */
export const PDFJS_ASSET_DIRS = ['wasm', 'iccs', 'cmaps', 'standard_fonts'] as const;

/** Исполнение скриптов PDF (QuickJS) не подключается вовсе — его файлы в поставку не едут. */
const SKIPPED = /^quickjs/;

/** Версия пакета — часть адреса: после обновления браузер не возьмёт прежний `wasm` из кэша. */
export function pdfjsVersion(): string {
  return JSON.parse(readFileSync(path.join(PDFJS_ROOT, 'package.json'), 'utf8')).version;
}

/** Файлы каталога, которые едут в поставку. */
export function pdfjsAssetFiles(dir: string): string[] {
  return readdirSync(path.join(PDFJS_ROOT, dir)).filter(f => !SKIPPED.test(f));
}

/**
 * Имена файлов, которые worker просит по адресу `wasmUrl` / `iccUrl`, и которых нет в поставке.
 * Сторож упаковки: обновлённая библиотека попросит новый файл — тест назовёт его до выпуска.
 */
export function missingPdfjsAssets(workerSource: string, shipped: ReadonlySet<string>): string[] {
  const asked = new Set<string>();
  for (const m of workerSource.matchAll(/([\w.-]+\.(?:wasm|icc))\b/g)) asked.add(m[1]);
  for (const m of workerSource.matchAll(/_noWasmFilename\s*=\s*"([^"]+)"/g)) asked.add(m[1]);
  return [...asked].filter(name => !SKIPPED.test(name) && !shipped.has(name)).sort();
}

const TYPES: Record<string, string> = {
  '.wasm': 'application/wasm', '.js': 'text/javascript', '.icc': 'application/vnd.iccprofile',
  '.ttf': 'font/ttf', '.pfb': 'application/octet-stream', '.bcmap': 'application/octet-stream',
};

/**
 * Кладёт каталоги {@link PDFJS_ASSET_DIRS} в сборку под `pdfjs/<версия>/`, а в dev отдаёт их по тем
 * же адресам прямо из `node_modules`. Адрес движок строит сам (`pdfEngine.ts`).
 */
export function pdfjsAssets(): Plugin {
  const version = pdfjsVersion();
  const prefix = `/pdfjs/${version}/`;
  return {
    name: 'bhs-pdfjs-assets',
    configureServer(server) {
      server.middlewares.use((req, res, next) => {
        const url = req.url?.split('?')[0] ?? '';
        if (!url.startsWith(prefix)) return next();
        const [dir, ...rest] = url.slice(prefix.length).split('/');
        const file = rest.join('/');
        const full = path.join(PDFJS_ROOT, dir, file);
        if (!(PDFJS_ASSET_DIRS as readonly string[]).includes(dir) || SKIPPED.test(file)
            || file.includes('..') || !existsSync(full) || !statSync(full).isFile()) return next();
        res.setHeader('Content-Type', TYPES[path.extname(file)] ?? 'application/octet-stream');
        createReadStream(full).pipe(res);
      });
    },
    generateBundle() {
      for (const dir of PDFJS_ASSET_DIRS) {
        for (const file of pdfjsAssetFiles(dir)) {
          this.emitFile({
            type: 'asset',
            fileName: `pdfjs/${version}/${dir}/${file}`,
            source: readFileSync(path.join(PDFJS_ROOT, dir, file)),
          });
        }
      }
    },
  };
}

/**
 * Политика содержимого поставки — строка `add_header Content-Security-Policy` из `deploy/nginx.conf`
 * (issue #1490, [UI-22]). Её отдаёт `vite preview`, на котором идут живые прогоны: иначе CI проверял
 * бы клиент без политики, а ломается под ней именно поставка (`blob:` под `connect-src 'self'`).
 * Dev-сервер её не получает: HMR вставляет инлайновые скрипты.
 */
export function deliveryCsp(nginxConf: string): string {
  const m = nginxConf.match(/^\s*add_header\s+Content-Security-Policy\s+"([^"]+)"/m);
  if (!m) throw new Error('В deploy/nginx.conf не найдена строка add_header Content-Security-Policy');
  return m[1];
}

export const NGINX_CONF = path.resolve(__dirname, '../../deploy/nginx.conf');

/**
 * Ставит {@link deliveryCsp} на все ответы `vite preview`. Файл читается только при запуске preview:
 * образ `web` собирается из одного `src/client`, без `deploy/`, и чтение при загрузке конфига
 * уронило бы сборку образа.
 */
export function deliveryCspOnPreview(): Plugin {
  return {
    name: 'bhs-delivery-csp',
    configurePreviewServer(server) {
      const csp = deliveryCsp(readFileSync(NGINX_CONF, 'utf8'));
      server.middlewares.use((_req, res, next) => {
        res.setHeader('Content-Security-Policy', csp);
        next();
      });
    },
  };
}
