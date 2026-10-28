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
import { dirname, join } from 'node:path';
import { __unstable__loadDesignSystem } from 'tailwindcss';

const require = createRequire(import.meta.url);

/**
 * Дизайн-система Tailwind по тексту index.css клиента — та же, что собирает vite. `base` — каталог,
 * от которого читаются относительные импорты (файлы темы `./theme/*.css`).
 */
export async function loadDesign(css, base) {
  const design = await __unstable__loadDesignSystem(css, {
    base,
    // Импорты index.css — сам `tailwindcss` и сгенерированные файлы темы рядом. Что-то ещё
    // появилось — пусть скажет.
    loadStylesheet: async (id, from) => {
      const path = id === 'tailwindcss' ? require.resolve('tailwindcss/index.css')
        : id.startsWith('./theme/') ? join(from, id) : null;
      if (!path) throw new Error(`Сторож цветовых классов не умеет импорт «${id}» из index.css`);
      return { path, base: dirname(path), content: await readFile(path, 'utf8') };
    },
  });
  // Функция не из стабильного API: обновление Tailwind может её переименовать. Тогда — отказ с
  // причиной, а не зелёный сторож, который ничего не проверяет.
  if (typeof design.candidatesToCss !== 'function')
    throw new Error('У дизайн-системы Tailwind нет candidatesToCss — сторож цветовых классов надо перевести на новый API');
  return design;
}

/**
 * Дизайн-система по index.css клиента. Возвращает проверку: какие из классов Tailwind НЕ порождает.
 */
export async function loadColorCheck(indexCssPath) {
  const design = await loadDesign(await readFile(indexCssPath, 'utf8'), dirname(indexCssPath));
  const known = new Map();
  return classes => {
    const fresh = [...new Set(classes)].filter(c => !known.has(c));
    design.candidatesToCss(fresh).forEach((out, i) => known.set(fresh[i], out != null));
    return classes.filter(c => !known.get(c));
  };
}

/**
 * Объявления класса без селектора, переменные — значениями темы: `(класс, 'light' | 'dark') → текст`
 * или null, если Tailwind класс не порождает. Так сверяют «рисует то же»: тема MD3 — псевдоним и его
 * роль (md3-theme.test.mjs), кодмод — пару карты (md3-migrate.test.mjs). Переменные — из
 * сгенерированных файлов темы, тёмная поверх светлой.
 */
export async function loadDeclarations(srcRoot) {
  const read = file => readFile(file, 'utf8').then(t => t.replaceAll('\r\n', '\n'));
  const design = await loadDesign(await read(join(srcRoot, 'index.css')), srcRoot);
  const light = new Map();
  const dark = new Map();
  for (const name of ['md3-tokens.css', 'md3-legacy.css']) {
    const css = await read(join(srcRoot, 'theme', name));
    for (const [, selector, body] of css.matchAll(/^(:root(?:\[data-theme="dark"\])?) \{\n([^}]*)\}/gm))
      for (const [, k, v] of body.matchAll(/(--[\w-]+):\s*([^;]+);/g)) (selector === ':root' ? light : dark).set(k, v.trim());
  }
  const vars = { light, dark: new Map([...light, ...dark]) };
  return (cls, mode) => {
    const [css] = design.candidatesToCss([cls]);
    if (css == null) return null;
    const resolve = text => text.replace(/var\((--[\w-]+)\)/g, (m, k) => vars[mode].has(k) ? resolve(vars[mode].get(k)) : m);
    return resolve(css.slice(css.indexOf('{') + 1, css.lastIndexOf('}'))).replace(/\s+/g, ' ').trim();
  };
}
