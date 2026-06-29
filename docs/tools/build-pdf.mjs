// Сборка PDF из Markdown-инструкций.
// Использование:  node build-pdf.mjs            — собрать все документы из ../
//                 node build-pdf.mjs FILE.md     — собрать один документ
//
// Зависимости: marked + playwright (см. package.json). Перед первым запуском:
//   npm install
//   npx playwright install chromium
//
// Необязательные переменные окружения (для CI/нестандартных установок):
//   BHS_PLAYWRIGHT  — путь/URL к модулю playwright (если не установлен локально)
//   BHS_CHROMIUM    — путь к исполняемому файлу Chromium (executablePath)

import { readFileSync, writeFileSync, mkdirSync, rmSync, existsSync } from 'node:fs';
import { dirname, resolve, basename } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { marked } from 'marked';

const HERE = dirname(fileURLToPath(import.meta.url));
const DOCS = resolve(HERE, '..');
const OUT = resolve(DOCS, 'pdf');

// Документы по умолчанию (в порядке важности).
const DEFAULT_DOCS = [
  ['DEPLOYMENT.md', 'Развёртывание системы BHS.CRG'],
  ['USER_GUIDE.md', 'Инструкция пользователя BHS.CRG'],
  ['ADMIN_GUIDE.md', 'Инструкция администратора BHS.CRG'],
];

async function loadChromium() {
  const candidates = [process.env.BHS_PLAYWRIGHT, 'playwright', 'playwright-core'].filter(Boolean);
  for (const c of candidates) {
    try { const m = await import(c); if (m.chromium) return m.chromium; } catch { /* следующий */ }
  }
  throw new Error('Playwright не найден. Выполните: npm install && npx playwright install chromium');
}

function htmlTemplate(title, bodyHtml) {
  return `<!doctype html><html lang="ru"><head><meta charset="utf-8">
<title>${title}</title>
<style>
  :root { --fg:#1a1a1a; --muted:#5c6470; --line:#d7dbe0; --brand:#1668c1; --code-bg:#f4f6f8; }
  * { box-sizing: border-box; }
  body { font-family: "Segoe UI", Roboto, "Helvetica Neue", Arial, sans-serif;
         color: var(--fg); font-size: 10.5pt; line-height: 1.5; margin: 0; }
  h1, h2, h3, h4 { line-height: 1.25; font-weight: 600; color: #11151a; }
  h1 { font-size: 20pt; margin: 0 0 .3em; padding-bottom: .25em; border-bottom: 2px solid var(--brand); page-break-before: always; }
  h1:first-of-type { page-break-before: avoid; }
  h2 { font-size: 14.5pt; margin: 1.4em 0 .4em; padding-bottom: .15em; border-bottom: 1px solid var(--line); }
  h3 { font-size: 12pt; margin: 1.1em 0 .3em; }
  h4 { font-size: 10.5pt; margin: 1em 0 .3em; color: var(--muted); }
  p, li { orphans: 3; widows: 3; }
  a { color: var(--brand); text-decoration: none; }
  code { font-family: "Cascadia Code", Consolas, "Courier New", monospace; font-size: 9.2pt;
         background: var(--code-bg); padding: 1px 4px; border-radius: 3px; }
  pre { background: var(--code-bg); border: 1px solid var(--line); border-radius: 6px;
        padding: 10px 12px; overflow-x: auto; page-break-inside: avoid; }
  pre code { background: none; padding: 0; font-size: 8.8pt; line-height: 1.45; }
  table { border-collapse: collapse; width: 100%; margin: .6em 0; font-size: 9.6pt; page-break-inside: avoid; }
  th, td { border: 1px solid var(--line); padding: 5px 8px; text-align: left; vertical-align: top; }
  th { background: #eef2f6; font-weight: 600; }
  img { max-width: 100%; border: 1px solid var(--line); border-radius: 6px; margin: .5em 0; page-break-inside: avoid; }
  blockquote { margin: .6em 0; padding: .4em .9em; border-left: 3px solid var(--brand);
               background: #f3f7fc; color: #2a2f36; border-radius: 0 6px 6px 0; }
  blockquote p { margin: .2em 0; }
  ul, ol { padding-left: 1.4em; }
  hr { border: none; border-top: 1px solid var(--line); margin: 1.2em 0; }
  .doc-title { page-break-after: always; text-align: center; padding-top: 28vh; }
  .doc-title .t { font-size: 26pt; font-weight: 700; color: var(--brand); border: none; padding: 0; }
  .doc-title .s { font-size: 12pt; color: var(--muted); margin-top: .6em; }
  .doc-title .d { font-size: 10pt; color: var(--muted); margin-top: 2em; }
</style></head><body>${bodyHtml}</body></html>`;
}

function footer(title) {
  return `<div style="font-size:7pt; color:#8a929c; width:100%; padding:0 14mm; display:flex; justify-content:space-between;">
    <span>${title}</span>
    <span>стр. <span class="pageNumber"></span> / <span class="totalPages"></span></span>
  </div>`;
}

const chromium = await loadChromium();
mkdirSync(OUT, { recursive: true });

const arg = process.argv[2];
const jobs = arg
  ? [[arg, basename(arg).replace(/\.md$/i, '')]]
  : DEFAULT_DOCS.filter(([f]) => existsSync(resolve(DOCS, f)));

const browser = await chromium.launch(
  process.env.BHS_CHROMIUM ? { executablePath: process.env.BHS_CHROMIUM } : {});

for (const [file, title] of jobs) {
  const src = resolve(DOCS, file);
  if (!existsSync(src)) { console.warn('пропуск (нет файла):', file); continue; }

  const md = readFileSync(src, 'utf8');
  const cover = `<div class="doc-title"><div class="t">${title}</div>
    <div class="s">Система исполнительной документации BHS.CRG</div>
    <div class="d">Версия документа: ${new Date().toISOString().slice(0, 10)}</div></div>`;
  const html = htmlTemplate(title, cover + marked.parse(md));

  // Временный HTML рядом с документом — чтобы относительные пути к images/ работали.
  const tmp = resolve(DOCS, `.tmp-${basename(file)}.html`);
  writeFileSync(tmp, html, 'utf8');

  const page = await browser.newPage();
  await page.goto(pathToFileURL(tmp).href, { waitUntil: 'networkidle' });
  const outPdf = resolve(OUT, basename(file).replace(/\.md$/i, '.pdf'));
  await page.pdf({
    path: outPdf,
    format: 'A4',
    printBackground: true,
    displayHeaderFooter: true,
    headerTemplate: '<span></span>',
    footerTemplate: footer(title),
    margin: { top: '16mm', bottom: '16mm', left: '16mm', right: '14mm' },
  });
  await page.close();
  rmSync(tmp, { force: true });
  console.log('собрано:', outPdf);
}

await browser.close();
