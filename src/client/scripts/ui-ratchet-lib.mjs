// Счёт и сравнение для храповика интерфейса (issue #1349). Чистые функции: файловая система и
// процесс — в ui-ratchet.mjs, здесь только то, что проверяет тест.

import ts from 'typescript';

const COLORS = 'slate|gray|zinc|neutral|stone|red|orange|amber|yellow|lime|green|emerald|teal|cyan|sky|blue|indigo|violet|purple|fuchsia|pink|rose';
const UTILITIES = 'bg|text|border|ring|ring-offset|outline|divide|fill|stroke|from|via|to|shadow|accent|caret|decoration|placeholder';

/**
 * Классы нарушений. У каждого — куда идти вместо: сообщение отказа обязано называть выход, иначе
 * его обходят первым попавшимся способом.
 */
export const RULES = [
  {
    id: 'table',
    pattern: /<table\b/g,
    instead: 'данные — DataGrid (shared/ui/DataGrid: cell, width, wrap, density="compact"); черновик строк — EditGrid (shared/ui/EditGrid: cell, add, onRemove, total); «поле — значение» и форма — раскладка (dl, grid), а не таблица (docs/UI.md, «Таблицы», #1354)',
  },
  {
    id: 'select',
    pattern: /<select\b/g,
    instead: 'выбор из списка — Select (shared/ui/Select): компактный size="sm" для строк таблиц, emptyItem для «не выбрано» (#1353)',
  },
  {
    id: 'h1',
    // Свой заголовок страницы. Законные — карточки входа (features/auth, LoginPage): это карточка
    // посреди пустого экрана, а не страница с шапкой, и в уровне они стоят навсегда.
    pattern: /<h1\b/g,
    instead: 'заголовок страницы — PageHeader (shared/ui/PageHeader), в list-detail — ListDetailShell: docs/UI.md, «Заголовки»',
  },
  {
    id: 'text-size',
    // Произвольный размер текста: text-[11px], text-[0.7rem]. Цвет вида text-[#fff] ловит palette.
    pattern: /\btext-\[\d+(?:\.\d+)?(?:px|rem|em)\]/g,
    instead: 'размер текста — ступенью шкалы (text-caption, text-xs, text-note, text-sm): docs/UI.md, «Текст»',
  },
  {
    id: 'palette',
    // Сырая палитра Tailwind (bg-indigo-50, hover:text-purple-700/80) и hex в произвольном значении.
    pattern: new RegExp(`(?<![\\w-])(?:${UTILITIES})-(?:${COLORS})-\\d{2,3}\\b|-\\[#[0-9a-fA-F]{3,8}\\]`, 'g'),
    instead: 'цвет — только токеном (text-brand, bg-warning-subtle, text-tertiary…): docs/UI.md, «Цвет»',
  },
  {
    id: 'fixed-width',
    // Своя ширина (issue #1382): w-[…], min-w-[…], max-w-[…] и числовая ширина в style. Обёртка тела
    // с min-w-[520px] подпирает окно изнутри, и размер выходит не тем, что назван у Modal. По всему
    // features/, а не только в файлах с <Modal: тело окна обычно живёт в своём файле (форма,
    // редактор сопоставления), и сторож по файлу с окном его не видел бы. Различить обёртку и
    // колонку по тексту нельзя — законные (колонка сопоставления, поле поиска) стоят в уровне.
    // Доля (max-w-[45%]) — обрезка подписи в строке: ничего не подпирает и не считается.
    pattern: /(?<![\w-])(?:min-|max-)?w-\[[^\]\s%]+\]|\b(?:minWidth|maxWidth|width)\s*:\s*['"]?\d/g,
    instead: 'размер окна — по содержимому: обычное, wide или fullScreen (docs/UI.md, «Окна»); ширину колонки — width у DataGrid / EditGrid классом шкалы (w-36); раскладку экрана задают каркасы (docs/UI.md, «Узкий экран»)',
  },
  {
    id: 'native-title',
    // Браузерная подсказка у элемента разметки (issue #1394): серая, с задержкой в секунду, к фокусу
    // глуха. Текстом её не поймать — `title` носят и наши компоненты (окно, заголовок страницы), и
    // атрибут бывает в любой строке многострочного тега; поэтому — по синтаксическому дереву.
    // Законны две: у iframe `title` — его имя для скринридера, а у обрезанной строки (truncate,
    // line-clamp) — её же полный текст, когда подсказка совпадает с содержимым.
    count: countNativeTitles,
    instead: 'подсказка — Tooltip (shared/ui/Tooltip) или проп title у Button / IconButton: при наведении и фокусе, в нашем виде; у кнопки из одной иконки — <Tooltip name>. Нативный title — только полный текст обрезанной строки (docs/UI.md, «Подсказка»)',
  },
  {
    id: 'chip',
    // Свой чип (#1416): нажимаемая пилюля — rounded-full с полями по бокам (px-…) или aria-pressed у
    // нативного элемента. Компоненты (Button, Chip) рисуют пилюлю сами и не считаются: правило
    // смотрит только элементы разметки.
    count: text => countElements(text, isChip),
    instead: 'чип-переключатель и отбор — Chip (shared/ui/Chip: selected, count, tone, onRemove); режим внутри панели — Tabs variant="segmented" (docs/UI.md, «Язык формы», #1416)',
  },
  {
    id: 'badge',
    // Своя пометка состояния (#1416): пилюля без нажатия — span или div с rounded-full и фоном или рамкой.
    // Круги заданного размера (аватар, точка, спиннер, полоса загрузки) — не пометки: у них w-/h-/size-.
    count: text => countElements(text, isBadge),
    instead: 'пометка состояния — StatusBadge (shared/ui/StatusBadge): tone по смыслу — neutral, brand, success, warning, danger, quiet; bordered на заливке (docs/UI.md, «Язык формы»)',
  },
  {
    id: 'icon-button',
    // Своя кнопка из одной иконки (#1417): нативный <button>, единственный ребёнок которого — компонент
    // (значок lucide). IconButton даёт ей размер не меньше 32 px, имя для скринридера и подсказку.
    count: text => countElements(text, isIconButton),
    instead: 'кнопка из одной иконки — IconButton (shared/ui/Button: значок ребёнком, label — имя для скринридера, size sm|md|lg — 32/36/40 px, danger); с подписью — Button variant="text" (docs/UI.md, «Узкий экран»: кнопка-иконка не меньше 32 px)',
  },
  {
    id: 'tabs',
    // Свои вкладки по ARIA (#1416): role="tab" / "tablist" / "tabpanel" у элемента разметки. Их клавиатуру
    // (стрелки, Home/End, ручная активация) Tabs уже делает — второй раз её пишут с ошибками.
    count: text => countElements(text, isTabRole),
    instead: 'вкладки — Tabs + TabPanel (shared/ui/Tabs): underline для разделов, segmented для режима; стрелки и ручную активацию компонент делает сам (docs/UI.md, «Язык формы»)',
  },
];

// Классы оформления пишутся строкой, шаблоном или выражением — правило читает текст значения
// className целиком. Модификаторы (hover:bg-…) — не основной вид: пилюля без своего фона только
// подсвечивается при наведении и пометкой не выглядит.
const PILL = /(?<![\w-])rounded-full\b/;
const SIZED = /(?<![\w-])(?:(?:w|h|size)-(?:\d|\[|px\b)|inset-)/;
const SIDE_PADDING = /(?<![\w:-])px-/;
const SURFACE = /(?<![\w:/-])(?:bg-(?!transparent\b)|border(?![\w-]*(?:-0|-none|-transparent)\b))/;
const INTERACTIVE = new Set(['button', 'a', 'label']);
const PLAIN = new Set(['span', 'div', 'p', 'small', 'em', 'strong', 'mark', 'li']);

function isChip({ tag, cls, attrs }) {
  if (!INTERACTIVE.has(tag)) return false;
  return attrs.has('aria-pressed') || (PILL.test(cls) && SIDE_PADDING.test(cls) && !SIZED.test(cls));
}

function isBadge({ tag, cls }) {
  return PLAIN.has(tag) && PILL.test(cls) && SURFACE.test(cls) && !SIZED.test(cls);
}

function isIconButton({ tag, element, sf }) {
  if (tag !== 'button' || !ts.isJsxElement(element)) return false;
  const kids = element.children.filter(c => !(ts.isJsxText(c) && !c.text.trim()));
  return kids.length === 1 && ts.isJsxSelfClosingElement(kids[0]) && /^[A-Z]/.test(kids[0].tagName.getText(sf));
}

function isTabRole({ attrs }) {
  return /^["'{`]*(?:tab|tablist|tabpanel)["'}`]*$/.test(attrs.get('role') ?? '');
}

// Четыре правила подряд читают один и тот же файл — дерево разбирается однажды.
let parsed = { text: null, sf: null };
function parse(text) {
  if (parsed.text !== text) parsed = { text, sf: ts.createSourceFile('x.tsx', text, ts.ScriptTarget.Latest, true, ts.ScriptKind.TSX) };
  return parsed.sf;
}

/** Сколько элементов разметки (тег со строчной буквы) подходят под `match`. */
function countElements(text, match) {
  if (!text.includes('<')) return 0;
  const sf = parse(text);
  let n = 0;
  const visit = node => {
    if (ts.isJsxOpeningElement(node) || ts.isJsxSelfClosingElement(node)) {
      const tag = node.tagName.getText(sf);
      if (/^[a-z]/.test(tag)) {
        const attrs = new Map(node.attributes.properties.filter(ts.isJsxAttribute)
          .map(p => [p.name.getText(sf), p.initializer?.getText(sf) ?? '']));
        const element = ts.isJsxSelfClosingElement(node) ? node : node.parent;
        if (match({ tag, cls: attrs.get('className') ?? '', attrs, element, sf })) n++;
      }
    }
    ts.forEachChild(node, visit);
  };
  visit(sf);
  return n;
}

/** Подсказка — полный текст самой строки: `title` совпадает с её содержимым. */
function repeatsContent(init, element) {
  if (!init || !ts.isJsxElement(element)) return false;
  const kids = element.children.flatMap(c => ts.isJsxElement(c) ? [c, ...c.children] : [c]);
  const norm = s => s.replace(/\s+/g, ' ');
  if (ts.isStringLiteral(init)) return kids.some(c => ts.isJsxText(c) && c.text.trim() === init.text);
  const want = init.expression && norm(init.expression.getText());
  return kids.some(c => ts.isJsxExpression(c) && c.expression && norm(c.expression.getText()) === want);
}

/** Нативные `title` у элементов разметки — кроме законных (см. правило `native-title`). */
function countNativeTitles(text) {
  if (!text.includes('title')) return 0;
  const sf = parse(text);
  let n = 0;
  const visit = node => {
    if (ts.isJsxAttribute(node) && node.name.getText(sf) === 'title') {
      const opening = node.parent.parent;
      const tag = opening.tagName.getText(sf);
      const element = ts.isJsxSelfClosingElement(opening) ? opening : opening.parent;
      const cls = opening.attributes.properties
        .find(p => ts.isJsxAttribute(p) && p.name.getText(sf) === 'className')?.initializer?.getText(sf) ?? '';
      const truncated = /\b(?:truncate|line-clamp-\d+)\b/.test(cls) && repeatsContent(node.initializer, element);
      if (/^[a-z]/.test(tag) && tag !== 'iframe' && !truncated) n++;
    }
    ts.forEachChild(node, visit);
  };
  visit(sf);
  return n;
}

/** Сколько нарушений каждого класса в тексте одного файла. Нулевые классы не возвращаются. */
export function countViolations(text) {
  const counts = {};
  for (const rule of RULES) {
    const n = rule.count ? rule.count(text) : text.match(rule.pattern)?.length ?? 0;
    if (n > 0) counts[rule.id] = n;
  }
  return counts;
}

/**
 * Сравнение по ФАЙЛАМ, а не по общему числу: иначе «убрал таблицу здесь, завёл в новом экране»
 * прошло бы молча, а новый экран как раз и есть то, что храповик обязан остановить.
 *
 * @param baseline { [ruleId]: { [file]: count } }
 * @param actual   { [ruleId]: { [file]: count } }
 */
export function compare(baseline, actual) {
  const worse = [];
  const better = [];
  for (const rule of RULES) {
    const was = baseline[rule.id] ?? {};
    const now = actual[rule.id] ?? {};
    for (const [file, n] of Object.entries(now))
      if (n > (was[file] ?? 0)) worse.push({ rule: rule.id, file, was: was[file] ?? 0, now: n });
    for (const [file, n] of Object.entries(was))
      if ((now[file] ?? 0) < n) better.push({ rule: rule.id, file, was: n, now: now[file] ?? 0 });
  }
  return { worse, better };
}

/** Устойчивый порядок ключей — чтобы базовый уровень менялся в диффе только по делу. */
export function normalize(actual) {
  const out = {};
  for (const rule of RULES) {
    const files = actual[rule.id] ?? {};
    out[rule.id] = Object.fromEntries(Object.keys(files).sort().map(f => [f, files[f]]));
  }
  return out;
}
