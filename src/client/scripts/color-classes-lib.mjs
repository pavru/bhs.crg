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
