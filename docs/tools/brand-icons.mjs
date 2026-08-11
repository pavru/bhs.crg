// Сборка PNG-иконок приложения из знака BimHouse (issue #728).
//
// Мастер — docs/brand/BimHouseLogoNoName.svg; в public/favicon.svg лежит его почищенный вариант
// (квадратный viewBox + переключение тёмной половины по prefers-color-scheme). PNG собираем из
// САМОГО мастера, а не из favicon.svg: у того цвета живут в CSS-переменных, и рендер по ошибке в
// тёмной схеме дал бы светлый знак на белом фоне.
//
// Playwright, а не sharp/resvg: он уже стоит здесь ради сборки PDF, и браузерный рендер SVG
// заведомо совпадает с тем, что увидит пользователь. Запуск: npm run icons (в docs/tools).
import { chromium } from 'playwright';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, resolve } from 'node:path';

const here = dirname(fileURLToPath(import.meta.url));
const master = resolve(here, '../brand/BimHouseLogoNoName.svg');
const outDir = resolve(here, '../../src/client/public');

/**
 * `scale` — доля стороны, которую занимает знак.
 *
 * Для maskable это не вкусовщина: маска гарантирует только центральный круг диаметром 80% стороны,
 * а знак квадратный (окружности сидят по его углам) — вписанный в этот круг квадрат имеет сторону
 * 0.8/√2 ≈ 0.566. Отсюда 56%: при любой маске (круг, squircle, капля) лучи «вертушки» целы.
 *
 * `radius` — скругление подложки в долях стороны. Рисуем его сами только там, где систему никто не
 * просит маскировать (ярлык desktop-Chrome); iOS и Android скругляют сами, и второй радиус поверх
 * системного дал бы обгрызенные углы.
 */
const ICONS = [
  { file: 'icon-192.png', size: 192, scale: 0.68, radius: 0.22 },
  { file: 'icon-512.png', size: 512, scale: 0.68, radius: 0.22 },
  { file: 'icon-maskable-512.png', size: 512, scale: 0.56, radius: 0 },
  // Прозрачность iOS заливает чёрным, поэтому фон обязателен и здесь — но радиус её собственный.
  { file: 'apple-touch-icon.png', size: 180, scale: 0.62, radius: 0 },
];

const BG = '#ffffff';

const svg = readFileSync(master, 'utf8');
const dataUri = `data:image/svg+xml;base64,${Buffer.from(svg).toString('base64')}`;

const browser = await chromium.launch();
// colorScheme светлая явно: мастер тёмной темы не знает, но подложка белая — рендер в dark у
// будущего браузера не должен внезапно её перекрасить.
const page = await browser.newPage({ colorScheme: 'light', deviceScaleFactor: 1 });

for (const { file, size, scale, radius } of ICONS) {
  const inner = Math.round(size * scale);
  await page.setViewportSize({ width: size, height: size });
  await page.setContent(`<!doctype html><meta charset="utf-8">
    <style>
      html,body{margin:0;padding:0}
      #i{width:${size}px;height:${size}px;background:${BG};border-radius:${Math.round(size * radius)}px;
         display:flex;align-items:center;justify-content:center}
      img{width:${inner}px;height:${inner}px;display:block}
    </style>
    <div id="i"><img src="${dataUri}" alt=""></div>`);
  await page.locator('#i').screenshot({ path: resolve(outDir, file), omitBackground: true });
  console.log(`${file} — ${size}×${size}, знак ${inner}px`);
}

await browser.close();
console.log('Готово. Имена файлов зашиты в src/client/index.html и public/manifest.webmanifest.');
