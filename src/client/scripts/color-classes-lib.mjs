// Сторож цветовых классов без токена (issue #1362, эпик #1347).
//
// Tailwind v4 для класса с несуществующим цветом не порождает НИЧЕГО и не предупреждает:
// `bg-surface2` оставляет место без фона, `text-fg` — с унаследованным цветом, `border-primary` —
// с рамкой цвета текста. Экран выглядит «почти правильно», поэтому такие классы копились
// незамеченными: к 16.10.2026 их было 91 в 33 файлах.
//
// Сверка — тем же движком Tailwind и тем же index.css, что и в сборке, а не рукописным перечнем
// токенов: перечень разошёлся бы с темой ровно так же, как разошлись эти классы. Чем разбирать
// классы — вопрос самого Tailwind: text-xs, border-2, text-left, bg-black/40 он знает, а мы бы
// перечисляли и ошибались.
//
// Под присмотром только утилиты, которые берут ЦВЕТ: у них опечатка в имени токена молчит. Прочие
// (p-foo, font-size в комментарии) отсюда не видны нарочно — по всем классам подряд сторож тонул бы
// в словах из комментариев.

import { readFile } from 'node:fs/promises';
import { createRequire } from 'node:module';
import { dirname } from 'node:path';
import { __unstable__loadDesignSystem } from 'tailwindcss';

const require = createRequire(import.meta.url);

const COLOR_UTILITIES = [
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

/** Цветовые утилиты в коде файла (не в комментариях): класс без вариантов и номер строки. */
export function colorCandidates(source) {
  const text = stripComments(source);
  const found = [];
  for (const m of text.matchAll(CANDIDATE))
    found.push({ cls: m[1], line: text.slice(0, m.index).split('\n').length });
  return found;
}

/**
 * Дизайн-система Tailwind по index.css клиента — та же, что собирает vite. Возвращает проверку:
 * какие из классов Tailwind НЕ порождает.
 */
export async function loadColorCheck(indexCssPath) {
  const css = await readFile(indexCssPath, 'utf8');
  const design = await __unstable__loadDesignSystem(css, {
    base: dirname(indexCssPath),
    // Единственный импорт index.css — сам `tailwindcss`. Что-то ещё появилось — пусть скажет.
    loadStylesheet: async id => {
      if (id !== 'tailwindcss') throw new Error(`Сторож цветовых классов не умеет импорт «${id}» из index.css`);
      const path = require.resolve('tailwindcss/index.css');
      return { path, base: dirname(path), content: await readFile(path, 'utf8') };
    },
  });
  // Функция не из стабильного API: обновление Tailwind может её переименовать. Тогда — отказ с
  // причиной, а не зелёный сторож, который ничего не проверяет.
  if (typeof design.candidatesToCss !== 'function')
    throw new Error('У дизайн-системы Tailwind нет candidatesToCss — сторож цветовых классов надо перевести на новый API');

  const known = new Map();
  return classes => {
    const fresh = [...new Set(classes)].filter(c => !known.has(c));
    design.candidatesToCss(fresh).forEach((out, i) => known.set(fresh[i], out != null));
    return classes.filter(c => !known.get(c));
  };
}
