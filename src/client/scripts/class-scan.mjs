// Разбор цветовых классов в тексте файла — без Tailwind: им пользуются и сторож цветовых классов
// (color-classes-lib.mjs, там же — почему сверка идёт движком Tailwind), и храповик интерфейса,
// которому движок не нужен.

export const COLOR_UTILITIES = [
  'bg', 'text', 'border', 'border-[xytblrse]', 'ring', 'ring-offset', 'inset-ring', 'outline', 'divide',
  'fill', 'stroke', 'from', 'via', 'to', 'shadow', 'inset-shadow', 'drop-shadow', 'text-shadow', 'accent',
  'caret', 'decoration',
].join('|');

// Утилита с именованным значением: bg-surface2, border-danger/30, -text-fg!. Произвольные значения
// (bg-[var(--x)], text-(--x)) не проверяются: их и пишут, когда токена нет. Перед классом — начало
// строки, пробел, кавычка или двоеточие варианта (hover:, has-[:disabled]:).
const CANDIDATE = new RegExp(
  `(?<=^|[\\s'"\`:{(])!?-?((?:${COLOR_UTILITIES})-[a-z][\\w-]*(?:\\/[\\w.]+)?)!?(?=$|[\\s'"\`}),;])`, 'gm');

/**
 * Комментарии — пробелами, с сохранением переводов строк (номер строки в отчёте должен совпасть).
 * Класс в комментарии не рисуется, а проза о классах пишется сокращённо (`text-xs/fg2`,
 * «тонкие бордеры (outline-variant)») — сторож тонул бы в ней. Строки и шаблоны пропускаются
 * целиком: `https://…` в строке — не комментарий.
 */
export function stripComments(text) {
  const out = [...text];
  const blank = (from, to) => { for (let k = from; k < to; k++) if (out[k] !== '\n') out[k] = ' '; };
  for (let i = 0; i < text.length; i++) {
    const c = text[i];
    if (c === '"' || c === "'" || c === '`') {
      for (i++; i < text.length && text[i] !== c; i++) if (text[i] === '\\') i++;
    } else if (c === '/' && text[i + 1] === '/') {
      const end = text.indexOf('\n', i);
      blank(i, end < 0 ? text.length : end);
      i = end < 0 ? text.length : end;
    } else if (c === '/' && text[i + 1] === '*') {
      const end = text.indexOf('*/', i + 2);
      blank(i, end < 0 ? text.length : end + 2);
      i = end < 0 ? text.length : end + 1;
    }
  }
  return out.join('');
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
