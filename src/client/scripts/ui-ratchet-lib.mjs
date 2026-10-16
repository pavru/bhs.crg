// Счёт и сравнение для храповика интерфейса (issue #1349). Чистые функции: файловая система и
// процесс — в ui-ratchet.mjs, здесь только то, что проверяет тест.

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
];

/** Сколько нарушений каждого класса в тексте одного файла. Нулевые классы не возвращаются. */
export function countViolations(text) {
  const counts = {};
  for (const rule of RULES) {
    const n = text.match(rule.pattern)?.length ?? 0;
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
