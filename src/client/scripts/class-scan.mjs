// Разбор цветовых классов в тексте файла — без Tailwind: им пользуются и сторож цветовых классов
// (color-classes-lib.mjs, там же — почему сверка идёт движком Tailwind), и храповик интерфейса,
// которому движок не нужен.

export const COLOR_UTILITIES = [
  'bg', 'text', 'border', 'border-[xytblrse]', 'ring', 'ring-offset', 'inset-ring', 'outline', 'divide',
  'fill', 'stroke', 'from', 'via', 'to', 'shadow', 'inset-shadow', 'drop-shadow', 'text-shadow', 'accent',
  'caret', 'decoration',
].join('|');

// Граница класса — одна на сторожей и кодмод md3:migrate: что кодмод переводит, то храповик и видит.
// Перед классом — начало строки, пробел, кавычка, скобка, двоеточие варианта (hover:, has-[:disabled]:)
// или точка селектора (span.bg-primary). После — конец, пробел, кавычка, скобка, запятая, `\`
// (кавычка в строке внутри строки: \'bg-primary\'), псевдокласс селектора (.bg-primary:hover) или точка
// следующего класса селектора — но не расширение файла: 'bg-brand.svg' — путь, а не класс.
export const CLASS_BEFORE = `(?<=^|[\\s'"\`:{(.])`;
export const CLASS_AFTER = `(?=$|[\\s'"\`}),;\\\\]|:(?=[a-z])`
  + `|\\.(?!(?:svg|png|jpe?g|gif|webp|ico|css|[cm]?[jt]sx?|json|md|pdf|typ)(?![\\w-])))`;

// Утилита с именованным значением: bg-surface2, border-danger/30, -text-fg!. Произвольные значения
// (bg-[var(--x)], text-(--x)) не проверяются: их и пишут, когда токена нет.
const CANDIDATE = new RegExp(
  `${CLASS_BEFORE}!?-?((?:${COLOR_UTILITIES})-[a-z][\\w-]*(?:\\/[\\w.]+)?)!?${CLASS_AFTER}`, 'gm');

// Знак перед `/`, после которого тот открывает регулярное выражение, а не делит: `(`, `=`, `,`, `!`…
const REGEX_AFTER = /^$|[(,=:[!&|?{};+\-*%~^]/;

/**
 * Разбор кода на комментарии и строки — один на сторожей и кодмод: иначе они разошлись бы в том, что
 * считать комментарием. `comments` — отрезки [начало, конец); `strings` — строки, каждая списком
 * отрезков содержимого без кавычек. У шаблона `…${…}…` отрезки — его постоянные части, а строки внутри
 * `${…}` (`${on ? 'bg-x text-y' : ''}`) — свои, отдельные. Регулярное выражение (`/["']/`) — не строка.
 * `"` обрывается на переводе строки, кроме значения атрибута (`className="…`): прямая кавычка в тексте
 * JSX иначе перевернула бы разбор до конца файла. `'` — всегда на переводе строки.
 */
export function scanCode(text) {
  const comments = [];
  const strings = [];
  const before = i => { let k = i - 1; while (k >= 0 && /\s/.test(text[k])) k--; return k < 0 ? '' : text[k]; };
  const regexEnd = i => {
    if (!REGEX_AFTER.test(before(i))) return -1;
    for (let k = i + 1, inClass = false; k < text.length; k++) {
      const c = text[k];
      if (c === '\n') return -1;
      if (c === '\\') k++;
      else if (c === '[') inClass = true;
      else if (c === ']') inClass = false;
      else if (c === '/' && !inClass) return k;
    }
    return -1;
  };
  // Код от `i` до конца текста или (inner) до `}`, закрывающего `${`; возвращает, где остановился.
  const code = (i, inner) => {
    for (let depth = 0; i < text.length; i++) {
      const c = text[i];
      if (c === '/' && (text[i + 1] === '/' || text[i + 1] === '*')) {
        const end = text[i + 1] === '/' ? text.indexOf('\n', i) : text.indexOf('*/', i + 2);
        const to = end < 0 ? text.length : text[i + 1] === '/' ? end : end + 2;
        comments.push([i, to]);
        i = to - 1;
      } else if (c === '/') {
        const end = regexEnd(i);
        if (end > 0) i = end;
      } else if (c === '"' || c === "'") {
        const multiline = c === '"' && before(i) === '=';
        const from = i + 1;
        for (i++; i < text.length && text[i] !== c; i++) {
          if (text[i] === '\\') i++;
          else if (text[i] === '\n' && !multiline) break;
        }
        strings.push([[from, Math.min(i, text.length)]]);
      } else if (c === '`') {
        i = template(i);
      } else if (inner && c === '{') {
        depth++;
      } else if (inner && c === '}') {
        if (depth === 0) return i;
        depth--;
      }
    }
    return i;
  };
  const template = start => {
    const span = [];
    let from = start + 1;
    let i = start + 1;
    for (; i < text.length && text[i] !== '`'; i++) {
      if (text[i] === '\\') i++;
      else if (text[i] === '$' && text[i + 1] === '{') {
        span.push([from, i]);
        i = code(i + 2, true);
        from = i + 1;
      }
    }
    span.push([from, Math.min(i, text.length)]);
    strings.push(span);
    return i;
  };
  code(0, false);
  return { comments, strings };
}

/**
 * Комментарии — пробелами, с сохранением переводов строк (номер строки в отчёте должен совпасть).
 * Класс в комментарии не рисуется, а проза о классах пишется сокращённо (`text-xs/fg2`,
 * «тонкие бордеры (outline-variant)») — сторож тонул бы в ней. Разбор — `scanCode`: `https://…` в
 * строке — не комментарий.
 */
export function stripComments(text) {
  const out = text.split('');
  for (const [from, to] of scanCode(text).comments)
    for (let k = from; k < to; k++) if (out[k] !== '\n') out[k] = ' ';
  return out.join('');
}

/** Роли с контейнером: текст на `bg-X-container` — парной ролью `on-X-container` ([UI-62]). */
const CONTAINER_ROLES = ['error', 'success', 'warning', 'primary', 'secondary', 'tertiary'];
const LIST_TOKEN = /(?<=^|\s)([\w:[\]&=>*-]*:)?(bg|text)-([a-z]+(?:-container)?)((?:\/[\w.]+)?!?)(?=\s|$)/g;

/**
 * Текст на контейнере роли цветом самой роли — в ОДНОМ списке классов (`bg-warning-container
 * text-warning`): `{ at, from, to }` для каждого такого `text-…`, по порядку в тексте. Цвет роли — для
 * текста на поверхности, на своём контейнере у него парная роль. Список — строка кода (`scanCode`):
 * постоянные части шаблона вместе, строка внутри `${…}` — своим списком, иначе условный контейнер
 * (`${on ? 'bg-warning-container' : ''}`) перекрасил бы текст и там, где его нет. Вариант обязан
 * совпасть: `hover:bg-warning-container text-warning` — контейнер только под наведением. Разнесённое по
 * элементам (плашка снаружи, текст внутри) не видно — это правится руками. Им переводит кодмод
 * md3:migrate и считает храповик (`container-text`).
 */
export function containerTextHits(text) {
  if (!text.includes('-container')) return [];
  const hits = [];
  for (const span of scanCode(text).strings) {
    const tokens = span.flatMap(([from, to]) =>
      [...text.slice(from, to).matchAll(LIST_TOKEN)].map(m => ({ m, at: from + m.index })));
    const containers = new Set(tokens.filter(({ m }) => m[2] === 'bg' && m[3].endsWith('-container'))
      .map(({ m }) => `${m[1] ?? ''}|${m[3].slice(0, -'-container'.length)}`));
    for (const { m, at } of tokens)
      if (m[2] === 'text' && CONTAINER_ROLES.includes(m[3]) && containers.has(`${m[1] ?? ''}|${m[3]}`))
        hits.push({ at, from: m[0], to: `${m[1] ?? ''}text-on-${m[3]}-container${m[4]}`, role: m[3] });
  }
  return hits.sort((a, b) => a.at - b.at);
}

/**
 * Классы в строках кода — не в комментариях и не в регулярных выражениях (`scanCode`): `{ cls,
 * variants, at }`, где `cls` — утилита без вариантов и без `!`, `variants` — варианты по порядку
 * (`dark:hover:bg-white/10` → `['dark', 'hover']`). Двоеточие внутри скобок варианта
 * (`[@media(pointer:coarse)]:`, `data-[state=open]:`) вариант не делит. Регулярка со строкой `dark:`
 * (фильтр классов в Tooltip) — не строка, её классы не видны нарочно. Храповик зовёт её подряд для
 * каждого правила по одному и тому же файлу — последний разбор помнится, результат только читают.
 */
let lastTokens = { text: null, out: [] };
export function classTokens(text) {
  if (lastTokens.text === text) return lastTokens.out;
  const out = [];
  for (const span of scanCode(text).strings)
    for (const [from, to] of span)
      for (const m of text.slice(from, to).matchAll(/\S+/g)) {
        const { cls, variants } = splitVariants(m[0]);
        if (cls) out.push({ cls, variants, at: from + m.index });
      }
  lastTokens = { text, out };
  return out;
}

/** Класс → `{ cls, variants }` (см. `classTokens`): `dark:hover:bg-white/10` → `bg-white/10`, `['dark', 'hover']`. */
export function splitVariants(raw) {
  const variants = [];
  let depth = 0;
  let start = 0;
  for (let k = 0; k < raw.length; k++) {
    if (raw[k] === '[' || raw[k] === '(') depth++;
    else if (raw[k] === ']' || raw[k] === ')') depth--;
    else if (raw[k] === ':' && depth === 0) { variants.push(raw.slice(start, k)); start = k + 1; }
  }
  return { cls: raw.slice(start).replace(/^!|!$/g, ''), variants };
}

/** Цветовые утилиты в тексте, где комментарии уже вырезаны (`stripComments`). */
export function scanCandidates(text) {
  const found = [];
  for (const m of text.matchAll(CANDIDATE))
    found.push({ cls: m[1], line: text.slice(0, m.index).split('\n').length });
  return found;
}

/** Цветовые утилиты в коде файла (не в комментариях): класс без вариантов и номер строки. */
export const colorCandidates = source => scanCandidates(stripComments(source));
