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

import { readFileSync, realpathSync, writeFileSync } from 'node:fs';
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
 * Текст заголовка, как его видит читатель (textContent отрисованного заголовка), — из него GitHub
 * строит якорь.
 *
 * <p>⚠️ Код разбирается ПЕРВЫМ и остаётся как написан: `<путь>` внутри кода — текст, а не тег, и
 * выброшенный тегом он дал бы якорь без слова (ревью PR #1447). Вне кода: ссылка — своим текстом, теги
 * выброшены, звёздочки и подчёркивания выделения сняты (подчёркивание внутри слова — `BACKUP_DIR` —
 * не выделение и остаётся), сущности вроде «&amp;» — своим знаком.</p>
 */
export function plain(markdown) {
  return markdown
    .split(/(`[^`]*`)/)
    .map((part, i) => (i % 2 === 1 ? part.slice(1, -1) : prose(part)))
    .join('')
    .trim();
}

const ENTITIES = { amp: '&', lt: '<', gt: '>', quot: '"', apos: "'", nbsp: ' ' };

function prose(text) {
  return text
    .replace(/!\[([^\]]*)\]\([^)]*\)/g, '$1')
    .replace(/\[([^\]]*)\]\([^)]*\)/g, '$1')
    .replace(/<[^>]+>/g, '')
    .replace(/\*+/g, '')
    .replace(/(^|[^\p{L}\p{N}])_+(?=\S)|(?<=\S)_+(?=[^\p{L}\p{N}]|$)/gu, '$1')
    .replace(/&(#x?[0-9a-f]+|[a-z]+);/gi, (all, name) => {
      if (name[0] !== '#') return ENTITIES[name.toLowerCase()] ?? all;
      const code = name[1] === 'x' || name[1] === 'X' ? parseInt(name.slice(2), 16) : parseInt(name.slice(1), 10);
      return Number.isFinite(code) ? String.fromCodePoint(code) : all;
    });
}

const ATX = /^ {0,3}(#{1,6})[ \t]+(.+?)(?:[ \t]+#+)?[ \t]*$/;

/**
 * Документ одним проходом по строкам: заголовки, где стоит блок содержания, ссылки на якоря из текста
 * и заголовки в форме, которую построитель не понимает.
 *
 * <p>⚠️ Строки «#» внутри блоков кода — не заголовки: в DEPLOYMENT блоки shell полны комментариев
 * вида «# 1. Взять со страницы выпуска…», и наивный разбор принял бы их за разделы. Ограда блока
 * бывает с отступом — у блока внутри пункта списка.</p>
 *
 * <p>⚠️ Метка содержания — только ЦЕЛОЙ строкой и вне блоков кода (ревью PR #1447). Поиск подстроки
 * находил бы и упоминание метки в тексте («метками `&lt;!-- toc --&gt;`…»), и перестройка переписала
 * бы прозу вместо блока.</p>
 *
 * <p>⚠️ Заголовки в цитате, в пункте списка и подчёркнутые («===», «---») GitHub считает, а этот
 * разбор — нет: счёт повторов («-1», «-2») и содержание съехали бы молча. Разбирать их всерьёз —
 * значит писать свой Markdown; дешевле запретить: такие строки попадают в `unsupported`, и сторож
 * отказывает, называя строку.</p>
 */
export function scan(md) {
  const lines = md.split(/\r?\n/);
  const slug = slugger();
  const doc = { lines, headings: [], links: [], unsupported: [], start: -1, end: -1 };
  let fence = null;
  let inToc = false;
  let paragraph = false;
  lines.forEach((line, index) => {
    const number = index + 1;
    const mark = /^\s*(`{3,}|~{3,})/.exec(line);
    if (mark) {
      if (fence === null) fence = mark[1];
      else if (mark[1][0] === fence[0] && mark[1].length >= fence.length && line.trim() === mark[1]) fence = null;
      paragraph = false;
      return;
    }
    if (fence !== null) return;

    const bare = line.trim();
    if (bare === START && doc.start < 0) { doc.start = index; inToc = true; paragraph = false; return; }
    if (bare === END && inToc) { doc.end = index; inToc = false; paragraph = false; return; }
    if (inToc) return;

    const heading = ATX.exec(line);
    if (heading) {
      doc.headings.push({ level: heading[1].length, text: heading[2], anchor: slug(plain(heading[2])), line: number });
      paragraph = false;
    } else if (/^\s*>\s*#{1,6}[ \t]/.test(line) || /^\s*(?:[-*+]|\d+[.)])[ \t]+#{1,6}[ \t]/.test(line)) {
      doc.unsupported.push(`строка ${number}: заголовок внутри цитаты или пункта списка — «${bare}»`);
    } else if (paragraph && /^ {0,3}(=+|-+)[ \t]*$/.test(line)) {
      doc.unsupported.push(`строка ${number}: заголовок, подчёркнутый «${bare}», — пишите его через «##»`);
    }

    for (const link of line.replace(/`[^`]*`/g, '').matchAll(/\]\(#([^)\s]+)\)/g))
      doc.links.push({ anchor: decodeURIComponent(link[1]), line: number });

    // Подчёркивание делает заголовком только строку абзаца, начатую с края: продолжение пункта списка
    // (с отступом), строка таблицы, цитаты или разметки под чертой «---» остаются чертой.
    paragraph = !heading && /^[^\s>|<\-*+]/.test(line) && !/^\d+[.)][ \t]/.test(line);
  });
  return doc;
}

/** Заголовки документа по порядку: уровень, текст как в Markdown, якорь и строка. */
export function headings(md) {
  return scan(md).headings;
}

/**
 * Текст строки содержания. Ссылка в заголовке — своим текстом, скобки экранированы: иначе вышла бы
 * ссылка в ссылке, а её не отрисует ни GitHub, ни marked (ревью PR #1447).
 */
function tocText(text) {
  return text
    .replace(/!\[([^\]]*)\]\([^)]*\)/g, '$1')
    .replace(/\[([^\]]*)\]\([^)]*\)/g, '$1')
    .replace(/[[\]]/g, '\\$&');
}

/** Блок содержания: разделы «##» и подразделы «###», каждая строка — ссылка на свой заголовок. */
export function tocBlock(md) {
  const lines = headings(md)
    .filter(h => h.level === 2 || h.level === 3)
    .map(h => `${h.level === 3 ? '  ' : ''}- [${tocText(h.text)}](#${h.anchor})`);
  // Пустая строка перед закрывающей меткой — чтобы она не прилипла к последнему пункту списка.
  return [START, '**Содержание**', '', ...lines, '', END].join('\n');
}

/**
 * Документ с перестроенным содержанием. Меток нет — ошибка, а не молчаливая вставка наугад: где стоит
 * содержание (после вводного абзаца, до §1), решает человек один раз.
 */
export function withToc(md, file = 'документ') {
  const { lines, start, end } = scan(md);
  if (start < 0 || end < 0) throw new Error(`${file}: нет меток ${START} … ${END} отдельными строками — поставьте их перед §1.`);
  const eol = md.includes('\r\n') ? '\r\n' : '\n';
  return [...lines.slice(0, start), ...tocBlock(md).split('\n'), ...lines.slice(end + 1)].join(eol);
}

/**
 * Чем документ расходится с заголовками — человеческим языком, по строке на расхождение: новая глава
 * без строки содержания, строка без главы, переименование, ссылка из текста на якорь, которого нет, и
 * заголовок, который построитель не понимает. Пусто — сходится.
 */
export function tocProblems(md, file) {
  const doc = scan(md);
  if (doc.start < 0 || doc.end < 0) return [`${file}: нет меток ${START} … ${END} отдельными строками перед §1.`];

  const problems = doc.unsupported.map(u => `${file}, ${u}`);
  const item = l => /^\s*- \[/.test(l);
  const written = doc.lines.slice(doc.start, doc.end).filter(item).map(l => l.trim());
  const expected = tocBlock(md).split('\n').filter(item).map(l => l.trim());
  for (const line of expected) if (!written.includes(line)) problems.push(`${file}: в содержании нет строки ${line}`);
  for (const line of written) if (!expected.includes(line)) problems.push(`${file}: в содержании лишняя строка ${line}`);
  if (problems.length === 0 && written.join('\n') !== expected.join('\n'))
    problems.push(`${file}: строки содержания стоят не в порядке заголовков`);
  if (problems.length === 0 && doc.lines.slice(doc.start, doc.end + 1).join('\n') !== tocBlock(md))
    problems.push(`${file}: блок содержания отличается от построенного (отступы, подпись)`);

  // Ссылки на разделы из текста (README советует их ставить): переименование главы перестройка
  // содержания чинит, а эти — нет, и без сверки они вели бы в никуда молча (ревью PR #1447).
  const anchors = new Set(doc.headings.map(h => h.anchor));
  for (const link of doc.links)
    if (!anchors.has(link.anchor))
      problems.push(`${file}, строка ${link.line}: ссылка «#${link.anchor}» ведёт в никуда — заголовка с таким якорем нет`);
  return problems;
}

/**
 * Запущен ли модуль сам, а не импортирован. Сравниваются настоящие пути: через ссылку на каталог
 * (junction, symlink) Node называет модуль его настоящим путём, а argv[1] — путём ссылки, и `--check`
 * молча не проверял бы ничего (ревью PR #1447).
 */
function launchedDirectly() {
  if (!process.argv[1]) return false;
  try {
    return realpathSync(resolve(process.argv[1])) === realpathSync(fileURLToPath(import.meta.url));
  } catch {
    return false;
  }
}

if (launchedDirectly()) {
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
  // Успех говорится вслух: молчание выглядело бы так же, как проверка, которая не запускалась.
  if (check) console.log(`Содержание сходится с заголовками: ${TOC_DOCS.join(', ')}.`);
}
