// Сторож словаря темы ([UI-82], sharedui-md3-20; до него — сторож цветовых классов, issue #1362).
//
// Tailwind v4 для класса с именем, которого нет в теме, не порождает НИЧЕГО и не предупреждает:
// `bg-surface2` оставляет место без фона, `text-fg` — с унаследованным цветом, `border-primary` —
// с рамкой цвета текста. Экран выглядит «почти правильно», поэтому такие классы копились
// незамеченными: к 16.10.2026 их было 91 в 33 файлах.
//
// С финала перехода на MD3 пространства имён цвета, радиуса, текста и тени в `@theme` сброшены
// (md3-tailwind.css): классы этих утилит порождает только словарь MD3 — роли цвета, ступени формы,
// роли текста, уровни возвышения. Поэтому тот же вопрос «порождает ли Tailwind класс» ловит сразу всё:
// старое имя (text-fg1), сырую палитру (bg-gray-500), ступени Tailwind (rounded-2xl, text-xs, shadow-lg).
// Произвольное значение там, где у словаря есть ступень (text-[11px], rounded-[28px], shadow-[…]),
// Tailwind порождает — оно отказ само по себе, кроме исключений, названных в theme-classes.test.mjs.
//
// Сверка — тем же движком Tailwind и тем же index.css, что и в сборке, а не рукописным перечнем
// токенов: перечень разошёлся бы с темой ровно так же, как разошлись эти классы. Чем разбирать
// классы — вопрос самого Tailwind: border-2, text-left, rounded-full, bg-transparent он знает, а мы бы
// перечисляли и ошибались.
//
// Под присмотром только утилиты словаря — цвет, rounded, text, shadow: у них опечатка в имени молчит.
// Прочие (p-foo, font-size в комментарии) отсюда не видны нарочно — по всем классам подряд сторож
// тонул бы в словах из комментариев.

import { readFile } from 'node:fs/promises';
import { createRequire } from 'node:module';
import { dirname, join } from 'node:path';
import { __unstable__loadDesignSystem } from 'tailwindcss';
import classMap from './md3-class-map.json' with { type: 'json' };

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
      if (!path) throw new Error(`Сторож словаря темы не умеет импорт «${id}» из index.css`);
      return { path, base: dirname(path), content: await readFile(path, 'utf8') };
    },
  });
  // Функция не из стабильного API: обновление Tailwind может её переименовать. Тогда — отказ с
  // причиной, а не зелёный сторож, который ничего не проверяет.
  if (typeof design.candidatesToCss !== 'function')
    throw new Error('У дизайн-системы Tailwind нет candidatesToCss — сторож словаря темы надо перевести на новый API');
  return design;
}

/**
 * Класс в словаре: Tailwind его порождает, и значение не произвольное. Произвольное у радиуса и тени —
 * отказ всегда; у `text-` — только когда это размер: `text-[var(--x)]` Tailwind читает цветом, а
 * `text-[calc(…)]` и `text-(length:--x)` — размером, и различает их сам движок, а не перечень функций CSS.
 */
const inDictionary = (cls, css) => css != null
  && (!/[[(]/.test(cls) || (/^text-[[(]/.test(cls) && !/\bfont-size:/.test(css)));

/**
 * Дизайн-система по index.css клиента. Возвращает проверку: какие из классов вне словаря темы — Tailwind
 * их не порождает или они с произвольным значением там, где у словаря есть ступень (`inDictionary`).
 */
export async function loadThemeCheck(indexCssPath) {
  const design = await loadDesign(await readFile(indexCssPath, 'utf8'), dirname(indexCssPath));
  const known = new Map();
  return classes => {
    const fresh = [...new Set(classes)].filter(c => !known.has(c));
    design.candidatesToCss(fresh).forEach((out, i) => known.set(fresh[i], inDictionary(fresh[i], out)));
    return classes.filter(c => !known.get(c));
  };
}

const own = obj => Object.entries(obj ?? {}).filter(([k]) => !k.startsWith('//'));
// Старые имена цвета карты md3:migrate — длинные первыми: `bg-on-brand-subtle` — это `on-brand-subtle`.
const NAMES = own(classMap.names).sort(([a], [b]) => b.length - a.length);
const RESTRICTED = { lines: /^(?:border(?:-[xytblrse])?|ring|ring-offset|inset-ring|outline|divide)$/, text: /^(?:text|decoration|caret)$/ };

/**
 * Ближайшая роль словаря для класса вне него — по карте md3:migrate (`scripts/md3-class-map.json`): она
 * знает пары «старое → новое». Без пары — куда смотреть: шкала или раздел docs/UI.md. Отказ без выхода
 * обходят первым похожим классом, и он снова не рисуется.
 */
export function nearestRole(cls) {
  const [body, alpha] = cls.split(/(?=\/)/);
  const tail = alpha ?? '';
  if (classMap.classes[body]?.to) return `${classMap.classes[body].to}${tail}`;
  if (classMap.states.to[cls]) return classMap.states.to[cls];
  for (const [name, pair] of NAMES) {
    if (!body.endsWith(`-${name}`)) continue;
    const utility = body.slice(0, -name.length - 1);
    const restricted = Object.entries(RESTRICTED).find(([key, re]) => pair[key] && re.test(utility));
    if (restricted) return pair[restricted[0]];
    // Своя прозрачность у пары (`fg-disabled → on-surface/38`) — главнее прозрачности класса.
    return `${utility}-${pair.to}${pair.to.includes('/') ? '' : tail}`;
  }
  if (/^rounded/.test(body)) return 'ступень формы: rounded-xs, -sm, -md, -lg, -xl, rounded-full, rounded-none (docs/UI.md, «Форма и возвышение»)';
  if (/^(?:drop-|inset-|text-)?shadow/.test(body)) return 'уровень возвышения: shadow-elevation-1…5 (docs/UI.md, «Форма и возвышение»)';
  if (/^text-(?:\[|\(length:|(?:xs|sm|base|lg|\d?xl)$)/.test(body)) return 'роль текста: text-body-medium, text-body-small, text-label-small, text-title-small… (docs/UI.md, «Текст»)';
  return 'роль цвета MD3: text-on-surface, bg-surface-container, border-outline-variant, text-primary… (src/theme/md3-theme.json; docs/UI.md, «Цвет»)';
}

/**
 * Объявления класса без селектора, переменные — значениями темы: `(класс, 'light' | 'dark') → текст`
 * или null, если Tailwind класс не порождает. Так сверяют значения классов: тема MD3 — шкалы и роли
 * (md3-theme.test.mjs), кодмод — что пара карты ведёт в словарь (md3-migrate.test.mjs). Переменные — из
 * сгенерированного файла темы, тёмная поверх светлой. `.prime(классы)` — разобрать их заранее одним
 * вызовом движка; без него класс разбирается при первом обращении.
 */
export async function loadDeclarations(srcRoot) {
  const read = file => readFile(file, 'utf8').then(t => t.replaceAll('\r\n', '\n'));
  const design = await loadDesign(await read(join(srcRoot, 'index.css')), srcRoot);
  const light = new Map();
  const dark = new Map();
  const css = await read(join(srcRoot, 'theme', 'md3-tokens.css'));
  for (const [, selector, body] of css.matchAll(/^(:root(?:\[data-theme="dark"\])?) \{\n([^}]*)\}/gm))
    for (const [, k, v] of body.matchAll(/(--[\w-]+):\s*([^;]+);/g)) (selector === ':root' ? light : dark).set(k, v.trim());
  const vars = { light, dark: new Map([...light, ...dark]) };
  // Движок зовётся пачкой: `prime` отдаёт ему все классы теста одним вызовом, а не по одному.
  const raw = new Map();
  const prime = classes => {
    const fresh = [...new Set(classes)].filter(c => !raw.has(c));
    if (fresh.length) design.candidatesToCss(fresh).forEach((css, i) => raw.set(fresh[i], css));
  };
  return Object.assign((cls, mode) => {
    prime([cls]);
    const css = raw.get(cls);
    if (css == null) return null;
    const resolve = text => text.replace(/var\((--[\w-]+)\)/g, (m, k) => vars[mode].has(k) ? resolve(vars[mode].get(k)) : m);
    return resolve(css.slice(css.indexOf('{') + 1, css.lastIndexOf('}'))).replace(/\s+/g, ' ').trim();
  }, { prime });
}
