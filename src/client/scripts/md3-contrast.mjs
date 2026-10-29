// Контраст ролей темы MD3 ([UI-62]): пары, которые обязан выдержать каждый вариант палитры. Значения ролей
// считает генератор (md3-theme.mjs), и ручной правкой контраст не чинят — тест называет пару, тему и
// число, а лечится это вариантом схемы, уровнем контраста или своим цветом в описании темы.

/** Относительная яркость WCAG 2.x цвета #rrggbb. */
function luminance(hex) {
  const [r, g, b] = [1, 3, 5].map(i => parseInt(hex.slice(i, i + 2), 16) / 255)
    .map(c => (c <= 0.04045 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4));
  return 0.2126 * r + 0.7152 * g + 0.0722 * b;
}

/** Контраст двух цветов #rrggbb по WCAG 2.x: от 1 до 21. */
export function contrast(a, b) {
  const [hi, lo] = [luminance(a), luminance(b)].sort((x, y) => y - x);
  return (hi + 0.05) / (lo + 0.05);
}

const TEXT = 4.5;
const GRAPHIC = 3;
const ACCENTS = ['primary', 'secondary', 'tertiary', 'error'];
const SURFACES = ['surface', 'surface-dim', 'surface-bright', 'surface-container-lowest', 'surface-container-low',
  'surface-container', 'surface-container-high', 'surface-container-highest'];

/** Пары «передний план — фон — порог» одной темы: роли схемы и свои роли (`ext` — их значения). */
function pairs(ext) {
  const accents = [...ACCENTS, ...Object.keys(ext).filter(k => !k.startsWith('on-') && !k.endsWith('-container'))];
  return [
    ...accents.flatMap(x => [[`on-${x}`, x, TEXT], [`on-${x}-container`, `${x}-container`, TEXT]]),
    ...SURFACES.flatMap(s => [['on-surface', s, TEXT], ['on-surface-variant', s, TEXT], ['outline', s, GRAPHIC]]),
    ['inverse-on-surface', 'inverse-surface', TEXT],
  ];
}

const number = x => x.toFixed(2).replace('.', ',');

/**
 * Пары ниже порога в описании со значениями ролей (`resolve` из md3-theme.mjs). Строка называет тему,
 * пару и контраст: «светлая: on-success-container на success-container — 3,61:1, нужно 4,5:1».
 */
export function contrastFailures(palette) {
  const failures = [];
  for (const [mode, label] of [['light', 'светлая'], ['dark', 'тёмная']]) {
    const roles = { ...palette.roles[mode], ...palette.ext[mode] };
    for (const [fg, bg, need] of pairs(palette.ext[mode])) {
      if (!roles[fg] || !roles[bg]) {
        failures.push(`${label}: нет роли ${roles[fg] ? bg : fg} для пары ${fg} на ${bg}`);
        continue;
      }
      const c = contrast(roles[fg], roles[bg]);
      if (c < need) failures.push(`${label}: ${fg} на ${bg} — ${number(c)}:1, нужно ${String(need).replace('.', ',')}:1`);
    }
  }
  return failures;
}
