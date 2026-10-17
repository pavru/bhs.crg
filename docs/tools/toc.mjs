// Содержание инструкций (issue #1374).
// Использование:  node toc.mjs            — перестроить содержание во всех инструкциях
//                 node toc.mjs --check    — только сверить; расхождение — код 1 и перечень
//
// Содержание не пишут руками: инструкции дописывает почти каждый PR, и рукописное отстало бы с первой
// же новой главой. Скрипт пересобирает блок между метками START и END и не трогает остальной текст —
// повторный запуск даёт тот же блок.
//
// ⚠️ Якоря строятся ПО ПРАВИЛАМ GITHUB (github-slugger), и этим же модулем — в PDF (build-pdf.mjs):
// одна ссылка обязана работать в обоих местах. Разойдись правило хоть в одном знаке — ссылка молча
// вела бы в никуда там, где правило другое. Поэтому реализация одна, и сторож
// (src/client/scripts/docs-toc.test.mjs) зовёт её же, а не свою копию.
//
// Зависимостей нет нарочно: модуль импортирует сторож из проверки фронтенда, где docs/tools/node_modules
// не установлены.

import { readFileSync, writeFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const HERE = dirname(fileURLToPath(import.meta.url));
export const DOCS_DIR = resolve(HERE, '..');

/** Инструкции с содержанием. Документы разработки (DEV_NOTES и др.) — нет: их читают иначе. */
export const TOC_DOCS = ['USER_GUIDE.md', 'ADMIN_GUIDE.md', 'DEPLOYMENT.md'];

export const START = '<!-- toc -->';
export const END = '<!-- /toc -->';

/** Команда, которая чинит расхождение, — её называет и сторож. */
export const FIX_COMMAND = 'cd docs/tools && npm run toc';

/**
 * Якорь заголовка, как его ставит GitHub (github-slugger): нижний регистр; всё, что не буква, не
 * цифра, не диакритика и не «_», выбрасывается — кроме пробела и дефиса; каждый пробел — дефис
 * (два пробела вокруг выброшенного тире дают «--», и это правильно). Повторный якорь — с «-1», «-2».
 *
 * <p>⚠️ Счёт повторов идёт по ВСЕМ заголовкам документа по порядку, включая «#» и «####»: так
 * считает GitHub. Поэтому один счётчик на документ.</p>
 */
export function slugger() {
  const seen = new Map();
  return text => {
    const base = text.toLowerCase().replace(/[^\p{L}\p{M}\p{N}\p{Pc} -]/gu, '').replace(/ /g, '-');
    let slug = base;
    while (seen.has(slug)) {
      const next = seen.get(base) + 1;
      seen.set(base, next);
      slug = `${base}-${next}`;
    }
    seen.set(slug, 0);
    return slug;
  };
}

/**
 * Текст заголовка, как его видит читатель, — из него GitHub строит якорь: код без кавычек-обратных
 * апострофов, ссылка — своим текстом, без звёздочек выделения.
 */
export function plain(markdown) {
  return markdown
    .replace(/!\[([^\]]*)\]\([^)]*\)/g, '$1')
    .replace(/\[([^\]]*)\]\([^)]*\)/g, '$1')
    .replace(/`([^`]*)`/g, '$1')
    .replace(/\*+/g, '')
    .replace(/<[^>]+>/g, '')
    .trim();
}

/**
 * Заголовки документа по порядку: уровень, текст как в Markdown и якорь.
 *
 * <p>⚠️ Строки «#» внутри блоков кода — не заголовки: в DEPLOYMENT блоки shell полны комментариев
 * вида «# 1. Взять со страницы выпуска…», и наивный разбор принял бы их за разделы. Ограда блока
 * бывает с отступом — у блока внутри пункта списка.</p>
 *
 * <p>Блок содержания пропускается: заголовков в нём нет, но и считать его частью документа нельзя.</p>
 */
export function headings(md) {
  const slug = slugger();
  const found = [];
  let fence = null;
  let inToc = false;
  for (const line of md.split(/\r?\n/)) {
    if (line.trim() === START) { inToc = true; continue; }
    if (line.trim() === END) { inToc = false; continue; }
    if (inToc) continue;

    const mark = /^\s*(`{3,}|~{3,})/.exec(line);
    if (mark) {
      if (fence === null) fence = mark[1];
      else if (mark[1][0] === fence[0] && mark[1].length >= fence.length && line.trim() === mark[1]) fence = null;
      continue;
    }
    if (fence !== null) continue;

    const heading = /^ {0,3}(#{1,6})[ \t]+(.+?)(?:[ \t]+#+)?[ \t]*$/.exec(line);
    if (!heading) continue;
    const text = heading[2];
    found.push({ level: heading[1].length, text, anchor: slug(plain(text)) });
  }
  return found;
}

/** Блок содержания: разделы «##» и подразделы «###», каждая строка — ссылка на свой заголовок. */
export function tocBlock(md) {
  const lines = headings(md)
    .filter(h => h.level === 2 || h.level === 3)
    .map(h => `${h.level === 3 ? '  ' : ''}- [${h.text}](#${h.anchor})`);
  // Пустая строка перед закрывающей меткой — чтобы она не прилипла к последнему пункту списка.
  return [START, '**Содержание**', '', ...lines, '', END].join('\n');
}

/**
 * Документ с перестроенным содержанием. Меток нет — ошибка, а не молчаливая вставка наугад: где стоит
 * содержание (после вводного абзаца, до §1), решает человек один раз.
 */
export function withToc(md, file = 'документ') {
  const from = md.indexOf(START);
  const to = md.indexOf(END);
  if (from < 0 || to < from) throw new Error(`${file}: нет меток ${START} … ${END} — поставьте их перед §1.`);
  const eol = md.includes('\r\n') ? '\r\n' : '\n';
  return md.slice(0, from) + tocBlock(md).replace(/\n/g, eol) + md.slice(to + END.length);
}

/**
 * Чем содержание расходится с заголовками — человеческим языком, по строке на расхождение: новая
 * глава без строки, строка без главы, переименование. Пусто — сходится.
 */
export function tocProblems(md, file) {
  const from = md.indexOf(START);
  const to = md.indexOf(END);
  if (from < 0 || to < from) return [`${file}: нет меток ${START} … ${END} перед §1.`];

  const written = md.slice(from, to).split(/\r?\n/).filter(l => /^\s*- \[/.test(l)).map(l => l.trim());
  const expected = tocBlock(md).split('\n').filter(l => /^\s*- \[/.test(l)).map(l => l.trim());
  const problems = [];
  for (const line of expected) if (!written.includes(line)) problems.push(`${file}: в содержании нет строки ${line}`);
  for (const line of written) if (!expected.includes(line)) problems.push(`${file}: в содержании лишняя строка ${line}`);
  if (problems.length === 0 && written.join('\n') !== expected.join('\n'))
    problems.push(`${file}: строки содержания стоят не в порядке заголовков`);
  if (problems.length === 0 && md.slice(from, to + END.length).replace(/\r\n/g, '\n') !== tocBlock(md))
    problems.push(`${file}: блок содержания отличается от построенного (отступы, подпись)`);
  return problems;
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const check = process.argv.includes('--check');
  let failed = false;
  for (const file of TOC_DOCS) {
    const path = resolve(DOCS_DIR, file);
    const md = readFileSync(path, 'utf8');
    if (check) {
      const problems = tocProblems(md, file);
      problems.forEach(p => console.error(p));
      failed ||= problems.length > 0;
      continue;
    }
    const next = withToc(md, file);
    if (next !== md) writeFileSync(path, next, 'utf8');
    console.log(`${next === md ? 'без изменений' : 'перестроено'}: ${file}`);
  }
  if (failed) {
    console.error(`Содержание разошлось с заголовками. Перестроить: ${FIX_COMMAND}`);
    process.exit(1);
  }
}
