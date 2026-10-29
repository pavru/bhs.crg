#!/usr/bin/env node
// Генератор темы MD3 (эпик sharedui-md3, [UI-57]): из описания src/theme/md3-theme.json — три файла
// рядом с ним, все в git и все руками не правятся:
//   md3-tokens.css   — переменные --md-sys-* / --md-ext-* обеих тем (тёмная — атрибутом data-theme;
//                      форма и возвышение — одни на обе);
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
 * подменяет светлый primary самим seed (с ним surface-tint и тёмный inverse-primary; остальные роли
 * primary — из тональной палитры seed); свои роли
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
  // Закреплённый primary — вместе с ролями, которые по MD3 и есть он: подсветка поверхности светлой
  // темы и primary светлой темы внутри тёмной. Иначе в теме два «основных» синих.
  if (theme.pinPrimary)
    for (const [mode, role] of [['light', 'primary'], ['light', 'surface-tint'], ['dark', 'inverse-primary']])
      roles[mode][role] = theme.seed.toLowerCase();
  const ext = { light: {}, dark: {} };
  const names = new Set();
  for (const { name, value, harmonize } of theme.ext ?? []) {
    if (!/^[a-z][a-z0-9]*(?:-[a-z0-9]+)*$/.test(name ?? '')) throw new Error(`md3-theme.json: имя своей роли «${name}» — не kebab-case`);
    if (names.has(name) || SCHEME_ROLES.includes(name)) throw new Error(`md3-theme.json: своя роль «${name}» повторяет ${names.has(name) ? 'другую свою роль' : 'роль схемы'}`);
    if (!/^#[0-9a-f]{6}$/i.test(value ?? '')) throw new Error(`md3-theme.json: цвет своей роли «${name}» — «${value}», а не #rrggbb`);
    names.add(name);
    const group = mcu.customColor(seed, { name, value: mcu.argbFromHex(value), blend: !!harmonize });
    for (const mode of ['light', 'dark']) {
      const g = group[mode];
      Object.assign(ext[mode], { [name]: mcu.hexFromArgb(g.color), [`on-${name}`]: mcu.hexFromArgb(g.onColor),
        [`${name}-container`]: mcu.hexFromArgb(g.colorContainer), [`on-${name}-container`]: mcu.hexFromArgb(g.onColorContainer) });
    }
  }
  return { ...theme, mode: 'values', roles, ext };
}

/**
 * Слой состояния и движение ([UI-64] [UI-65]) — с темой не переключаются. Проверяются здесь, а не
 * при чтении стилей: кривая «ease» вместо cubic-bezier или «.08» строкой молча дали бы элемент без
 * перехода или без слоя, и заметили бы это только глазом.
 */
function stateMotion(theme) {
  const state = entries(theme.state);
  const easing = entries(theme.motion?.easing);
  const duration = entries(theme.motion?.duration);
  for (const [k, v] of state)
    if (typeof v !== 'number' || v <= 0 || v >= 1) throw new Error(`md3-theme.json: state.${k} — «${v}», а не доля между 0 и 1`);
  for (const [k, v] of easing)
    if (!/^cubic-bezier\((-?[\d.]+, ){3}-?[\d.]+\)$/.test(v)) throw new Error(`md3-theme.json: motion.easing.${k} — «${v}», а не cubic-bezier(…)`);
  for (const [k, v] of duration)
    if (!/^\d+ms$/.test(v)) throw new Error(`md3-theme.json: motion.duration.${k} — «${v}», а не миллисекунды`);
  for (const [part, list] of [['state', state], ['motion.easing', easing]])
    if (!list.length) throw new Error(`md3-theme.json: раздел ${part} пуст — слою состояния и переходам нечего брать`);
  return {
    tokens: [
      ...state.map(([k, v]) => `--md-sys-state-${k}: ${v};`),
      ...easing.map(([k, v]) => `--md-sys-motion-easing-${k}: ${v};`),
      ...duration.map(([k, v]) => `--md-sys-motion-duration-${k}: ${v};`),
    ],
    tailwind: easing.map(([k]) => `--ease-${k}: var(--md-sys-motion-easing-${k});`),
  };
}

/** Ступень шкалы формы → класс Tailwind (`rounded-sm`). `none` и `full` у Tailwind свои. */
export const SHAPE_CLASSES = { xs: 'extra-small', sm: 'small', md: 'medium', lg: 'large', xl: 'extra-large' };
/** Уровни возвышения с классом `shadow-elevation-N`. Уровень 0 — отсутствие тени: класс у него `shadow-none`. */
export const ELEVATION_CLASSES = [1, 2, 3, 4, 5];
const LAYER = /^(-?\d+(?:px)?) (-?\d+(?:px)?) (\d+(?:px)?) (-?\d+(?:px)?) (0?\.\d+|1)$/;

/**
 * Форма и возвышение ([UI-66]) — с темой не переключаются. Слой тени — геометрия и доля роли `shadow`:
 * цвет тени — роль, а не число, как и у любого цвета клиента. Ступень без px или слой без доли
 * отказывают здесь: в CSS они молча дали бы элемент без скругления или без тени.
 */
function shapeElevation(theme) {
  const shape = entries(theme.shape);
  const elevation = entries(theme.elevation);
  for (const [k, v] of shape)
    if (!/^\d+px$/.test(v)) throw new Error(`md3-theme.json: shape.${k} — «${v}», а не пиксели`);
  for (const [cls, step] of Object.entries(SHAPE_CLASSES))
    if (!shape.some(([k]) => k === step)) throw new Error(`md3-theme.json: в shape нет ступени ${step} (класс rounded-${cls})`);
  for (const n of [0, ...ELEVATION_CLASSES])
    if (!Array.isArray(theme.elevation?.[`level${n}`])) throw new Error(`md3-theme.json: в elevation нет уровня level${n} — списка слоёв`);
  const layer = (k, l) => {
    const m = LAYER.exec(l);
    if (!m) throw new Error(`md3-theme.json: elevation.${k} — слой «${l}», а не «x y размытие растяжение доля»`);
    const px = v => (v === '0' || v.endsWith('px') ? v : `${v}px`);
    return `${m.slice(1, 5).map(px).join(' ')} color-mix(in srgb, var(--md-sys-color-shadow) ${Math.round(Number(m[5]) * 100)}%, transparent)`;
  };
  return {
    tokens: [
      ...shape.map(([k, v]) => `--md-sys-shape-corner-${k}: ${v};`),
      ...elevation.map(([k, layers]) => `--md-sys-elevation-${k}: ${layers.length ? layers.map(l => layer(k, l)).join(', ') : 'none'};`),
    ],
    tailwind: [
      ...Object.entries(SHAPE_CLASSES).map(([cls, step]) => `--radius-${cls}: var(--md-sys-shape-corner-${step});`),
      ...ELEVATION_CLASSES.map(n => `--shadow-elevation-${n}: var(--md-sys-elevation-level${n});`),
    ],
  };
}

/** Три файла темы текстом: { 'md3-tokens.css': '…', … }. Чистая функция — её же зовёт тест дрейфа. */
export function generate(source) {
  const theme = resolve(source);
  const motion = stateMotion(theme);
  const form = shapeElevation(theme);
  // Темы — одним набором имён в обе стороны: роль только тёмной темы ушла бы в переменные без
  // класса (слой Tailwind строится по светлой) и без значения светлой темы.
  for (const part of ['roles', 'ext']) {
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
  ];
  const tokens = [
    HEADER,
    block(':root', [
      `--md-ref-typeface-plain: ${theme.typeface.plain};`,
      ...scheme('light'),
      // Половины превью значка вкладки: с темой не переключаются, в тёмной не переопределяются.
      ...statics.map(([k, v]) => `--md-ext-color-${k}: ${v};`),
      ...motion.tokens,
      ...form.tokens,
    ]),
    block(':root[data-theme="dark"]', scheme('dark')),
  ].join('\n');

  const tailwind = [
    HEADER,
    block('@theme inline', [
      ...entries(theme.roles.light).filter(([k]) => !NO_CLASS.includes(k))
        .map(([k]) => `--color-${k}: var(--md-sys-color-${k});`),
      ...[...entries(theme.ext.light), ...statics].map(([k]) => `--color-${k}: var(--md-ext-color-${k});`),
      ...motion.tailwind,
      ...form.tailwind,
    ]),
  ].join('\n');

  // Своих значений у старых имён больше нет (sharedui-md3-05 снял fg-disabled, brand-hover,
  // brand-pressed вместе с --legacy-*): псевдоним — только имя роли.
  const aliases = entries(theme.aliases);
  for (const [name, to] of aliases)
    if (typeof to !== 'string') throw new Error(`md3-theme.json: псевдоним «${name}» — не имя роли MD3`);
  const legacy = [
    HEADER + '/* Старые имена классов — переходные псевдонимы ролей MD3 ([UI-58]): рисуют ровно то же, что до\n'
      + '   перехода, и расти им нельзя (храповик legacy-class). Файл удаляет финал эпика целиком. */\n',
    block('@theme inline', aliases.map(([k, to]) => `--color-${k}: var(${roleVar(theme, to)});`)),
  ].join('\n');

  return { 'md3-tokens.css': tokens, 'md3-tailwind.css': tailwind, 'md3-legacy.css': legacy };
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  const files = generate(readTheme());
  for (const [name, text] of Object.entries(files)) writeFileSync(join(themeDir, name), text, 'utf8');
  console.log(`Тема MD3 записана: ${Object.keys(files).map(f => `src/theme/${f}`).join(', ')}.`);
}
