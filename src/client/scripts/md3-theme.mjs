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
// Генератор схемы MD3 — только здесь, в офлайн-скрипте: в бандл клиента он не попадает ([UI-62]).
import * as mcu from '@material/material-color-utilities';

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

/** Роли схемы MD3 в порядке переменных. Значение каждой — динамический цвет MCU того же имени. */
export const SCHEME_ROLES = [
  'primary', 'surface-tint', 'on-primary', 'primary-container', 'on-primary-container',
  'secondary', 'on-secondary', 'secondary-container', 'on-secondary-container',
  'tertiary', 'on-tertiary', 'tertiary-container', 'on-tertiary-container',
  'error', 'on-error', 'error-container', 'on-error-container',
  'background', 'on-background', 'surface', 'on-surface', 'surface-variant', 'on-surface-variant',
  'outline', 'outline-variant', 'shadow', 'scrim', 'inverse-surface', 'inverse-on-surface', 'inverse-primary',
  'primary-fixed', 'on-primary-fixed', 'primary-fixed-dim', 'on-primary-fixed-variant',
  'secondary-fixed', 'on-secondary-fixed', 'secondary-fixed-dim', 'on-secondary-fixed-variant',
  'tertiary-fixed', 'on-tertiary-fixed', 'tertiary-fixed-dim', 'on-tertiary-fixed-variant',
  'surface-dim', 'surface-bright', 'surface-container-lowest', 'surface-container-low', 'surface-container',
  'surface-container-high', 'surface-container-highest',
];

const SCHEMES = { tonalSpot: mcu.SchemeTonalSpot, fidelity: mcu.SchemeFidelity, content: mcu.SchemeContent,
  neutral: mcu.SchemeNeutral, vibrant: mcu.SchemeVibrant, expressive: mcu.SchemeExpressive };
const camel = name => name.replace(/-([a-z])/g, (_, c) => c.toUpperCase());

/**
 * Описание в режиме `seed` → то же описание со значениями ролей (`roles`, `ext`), как в режиме
 * `values`. Схема обеих тем — из seed по варианту и уровню контраста ([UI-62]); `pinPrimary`
 * подменяет светлый primary самим seed (остальные роли primary — из тональной палитры seed); свои роли
 * — `customColor` MCU: тона 40/100/90/10 и 80/20/30/90 палитры своего цвета, `harmonize` — сдвиг к seed.
 * Режим `values` проходит как есть: им собрано описание до перехода, и откат — его возвращение.
 */
export function resolve(theme) {
  if (theme.mode === 'values') return theme;
  if (theme.mode !== 'seed') throw new Error(`md3-theme.json: режим «${theme.mode}» генератор не знает — только seed или values`);
  const Scheme = SCHEMES[theme.variant];
  if (!Scheme) throw new Error(`md3-theme.json: вариант схемы «${theme.variant}» неизвестен — ${Object.keys(SCHEMES).join(', ')}`);
  if (!/^#[0-9a-f]{6}$/i.test(theme.seed)) throw new Error(`md3-theme.json: seed «${theme.seed}» — не цвет #rrggbb`);
  const seed = mcu.argbFromHex(theme.seed);
  const roles = {};
  for (const [mode, dark] of [['light', false], ['dark', true]]) {
    const scheme = new Scheme(mcu.Hct.fromInt(seed), dark, theme.contrast ?? 0);
    roles[mode] = Object.fromEntries(SCHEME_ROLES.map(role => {
      const color = mcu.MaterialDynamicColors[camel(role)];
      if (!color) throw new Error(`MCU: динамического цвета «${role}» нет`);
      return [role, mcu.hexFromArgb(color.getArgb(scheme))];
    }));
  }
  if (theme.pinPrimary) roles.light.primary = theme.seed.toLowerCase();
  const ext = { light: {}, dark: {} };
  for (const { name, value, harmonize } of theme.ext ?? []) {
    const group = mcu.customColor(seed, { name, value: mcu.argbFromHex(value), blend: !!harmonize });
    for (const mode of ['light', 'dark']) {
      const g = group[mode];
      Object.assign(ext[mode], { [name]: mcu.hexFromArgb(g.color), [`on-${name}`]: mcu.hexFromArgb(g.onColor),
        [`${name}-container`]: mcu.hexFromArgb(g.colorContainer), [`on-${name}-container`]: mcu.hexFromArgb(g.onColorContainer) });
    }
  }
  return { ...theme, mode: 'values', roles, ext };
}

/** Три файла темы текстом: { 'md3-tokens.css': '…', … }. Чистая функция — её же зовёт тест дрейфа. */
export function generate(source) {
  const theme = resolve(source);
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
