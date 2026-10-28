#!/usr/bin/env node
// Генератор темы MD3 (эпик sharedui-md3, [UI-57]): из описания src/theme/md3-theme.json — три файла
// рядом с ним, все в git и все руками не правятся:
//   md3-tokens.css   — переменные --md-sys-* / --md-ext-* обеих тем (тёмная — атрибутом data-theme);
//   md3-tailwind.css — слой Tailwind: класс — роль без префикса (bg-primary, text-on-surface-variant);
//   md3-legacy.css   — старые имена классов (text-fg1, bg-brand…) псевдонимами ролей. Финал эпика
//                      удаляет файл целиком, а не правит строки.
//
// Почему файлы, а не переменные прямо в index.css: тогда дрейф генерации сверялся бы по куску
// рукописного файла. Дрейф сторожит md3-theme.test.mjs — тем же generate(), что и здесь.
//
//   npm run md3:theme

import { readFileSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

export const themeDir = join(dirname(fileURLToPath(import.meta.url)), '..', 'src', 'theme');

const HEADER = '/* Сгенерировано scripts/md3-theme.mjs из src/theme/md3-theme.json — руками не править.\n'
  + '   Правка — в md3-theme.json, затем: npm run md3:theme */\n';

/** Пары «имя — значение» без ключей-комментариев («//»). */
export const entries = obj => Object.entries(obj ?? {}).filter(([k]) => !k.startsWith('//'));

/** Переменная роли: своя роль (`ext`) — `--md-ext-color-*`, роль схемы MD3 — `--md-sys-color-*`. */
export function roleVar(theme, role) {
  if (role in theme.roles.light) return `--md-sys-color-${role}`;
  if (role in theme.ext.light) return `--md-ext-color-${role}`;
  throw new Error(`md3-theme.json: роли «${role}» нет ни в roles, ни в ext`);
}

/**
 * Роли без класса Tailwind. `shadow` рисуют уровни тени (`--md-sys-elevation-*`), а её класс
 * перехватил бы имена утилит тени: `text-shadow` красил бы текст в цвет тени, и сторож цветовых
 * классов принимал бы его как известный.
 */
export const NO_CLASS = ['shadow'];

/**
 * Значение половины превью значка: ссылка «тема.роль» (`light.surface-container-low`). Числом её не
 * пишут — превью обязано совпадать с фоном страницы и после смены палитры.
 */
function staticValue(theme, name, ref) {
  const [mode, role] = String(ref).split('.');
  const value = theme.roles[mode]?.[role] ?? theme.ext[mode]?.[role];
  if (!value) throw new Error(`md3-theme.json: static.${name} — «${ref}» не ссылка «light|dark.<роль>»`);
  return value;
}

/** Описание темы с диска. */
export const readTheme = () => JSON.parse(readFileSync(join(themeDir, 'md3-theme.json'), 'utf8'));

/** Три файла темы текстом: { 'md3-tokens.css': '…', … }. Чистая функция — её же зовёт тест дрейфа. */
export function generate(theme) {
  if (theme.mode !== 'values')
    throw new Error(`md3-theme.json: режим «${theme.mode}» генератор пока не знает — только values`);
  // Темы — одним набором имён в обе стороны: роль только тёмной темы ушла бы в переменные без
  // класса (слой Tailwind строится по светлой) и без значения светлой темы.
  for (const part of ['roles', 'ext', 'elevation']) {
    const [light, dark] = ['light', 'dark'].map(mode => entries(theme[part][mode]).map(([k]) => k));
    for (const name of light.filter(k => !dark.includes(k))) throw new Error(`md3-theme.json: у ${part}.${name} нет значения тёмной темы`);
    for (const name of dark.filter(k => !light.includes(k))) throw new Error(`md3-theme.json: у ${part}.${name} нет значения светлой темы`);
  }
  // Псевдоним с именем роли пишет свой --color-* после слоя ролей и молча перекрывает класс роли.
  const classes = [...entries(theme.roles.light), ...entries(theme.ext.light), ...entries(theme.static)].map(([k]) => k);
  for (const [name] of entries(theme.aliases))
    if (classes.includes(name)) throw new Error(`md3-theme.json: псевдоним «${name}» совпадает с именем роли — класс роли он перекрыл бы`);
  const statics = entries(theme.static).map(([k, ref]) => [k, staticValue(theme, k, ref)]);

  const block = (selector, lines) => `${selector} {\n${lines.map(l => `  ${l}`).join('\n')}\n}\n`;
  const scheme = mode => [
    ...entries(theme.roles[mode]).map(([k, v]) => `--md-sys-color-${k}: ${v};`),
    ...entries(theme.ext[mode]).map(([k, v]) => `--md-ext-color-${k}: ${v};`),
    ...entries(theme.elevation[mode]).map(([k, v]) => `--md-sys-elevation-${k}: ${v};`),
  ];
  const tokens = [
    HEADER,
    block(':root', [
      `--md-ref-typeface-plain: ${theme.typeface.plain};`,
      ...scheme('light'),
      // Половины превью значка вкладки: с темой не переключаются, в тёмной не переопределяются.
      ...statics.map(([k, v]) => `--md-ext-color-${k}: ${v};`),
    ]),
    block(':root[data-theme="dark"]', scheme('dark')),
  ].join('\n');

  const tailwind = [
    HEADER,
    block('@theme inline', [
      ...entries(theme.roles.light).filter(([k]) => !NO_CLASS.includes(k))
        .map(([k]) => `--color-${k}: var(--md-sys-color-${k});`),
      ...[...entries(theme.ext.light), ...statics].map(([k]) => `--color-${k}: var(--md-ext-color-${k});`),
    ]),
  ].join('\n');

  const aliases = entries(theme.aliases);
  const own = aliases.filter(([, to]) => typeof to !== 'string');
  for (const [name, to] of own)
    if (!to.legacy?.light || !to.legacy?.dark) throw new Error(`md3-theme.json: псевдоним «${name}» без значений legacy обеих тем`);
  const legacy = [
    HEADER + '/* Старые имена классов — переходные псевдонимы ролей MD3 ([UI-58]): рисуют ровно то же, что до\n'
      + '   перехода, и расти им нельзя (храповик legacy-class). Имя без роли того же значения держит своё\n'
      + '   значение переменной --legacy-*. Файл удаляет финал эпика целиком. */\n',
    block(':root', own.map(([k, to]) => `--legacy-${k}: ${to.legacy.light};`)),
    block(':root[data-theme="dark"]', own.map(([k, to]) => `--legacy-${k}: ${to.legacy.dark};`)),
    block('@theme inline', aliases.map(([k, to]) =>
      `--color-${k}: var(${typeof to === 'string' ? roleVar(theme, to) : `--legacy-${k}`});`)),
  ].join('\n');

  return { 'md3-tokens.css': tokens, 'md3-tailwind.css': tailwind, 'md3-legacy.css': legacy };
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  const files = generate(readTheme());
  for (const [name, text] of Object.entries(files)) writeFileSync(join(themeDir, name), text, 'utf8');
  console.log(`Тема MD3 записана: ${Object.keys(files).map(f => `src/theme/${f}`).join(', ')}.`);
}
