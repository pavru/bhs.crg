import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { beforeAll, describe, expect, it } from 'vitest';
import { loadDeclarations } from './color-classes-lib.mjs';
import { generate, readTheme, resolve, themeDir } from './md3-theme.mjs';
import { contrast, contrastFailures } from './md3-contrast.mjs';

// Сторож темы MD3 (эпик sharedui-md3, [UI-57] [UI-58] [UI-59]): почему файлы генерируются — в
// md3-theme.mjs.
const srcRoot = join(themeDir, '..');
const serverRoot = join(srcRoot, '..', '..', 'server');
const theme = readTheme();
/** Описание со значениями ролей — то, что генератор раскладывает по файлам. */
const palette = resolve(theme);
const read = file => readFileSync(file, 'utf8').replaceAll('\r\n', '\n');

describe('тема MD3: файлы равны генерации', () => {
  it.each(Object.entries(generate(theme)))('%s', (name, text) => {
    // Сообщение называет команду: иначе поправят руками второй файл, и дрейф станет двойным.
    expect(read(join(themeDir, name)) === text,
      `src/theme/${name} не равен генерации из src/theme/md3-theme.json. Файл руками не правят:\n`
      + 'правка — в md3-theme.json, затем npm run md3:theme, и оба файла — в коммит.').toBe(true);
  });
});

/**
 * Старое имя → роль MD3 того же значения (design sharedui-md3-01, «Псевдонимы»). Таблица — договор,
 * и держится здесь отдельно от описания темы: перепутанный в md3-theme.json псевдоним
 * (`fg1 → on-surface-variant`) сменил бы вид, а сверка «псевдоним ≡ его же роль» этого не заметила бы.
 */
const ALIAS_ROLE = {
  'fg1': 'on-surface', 'fg2': 'on-surface-variant',
  // В1 (29.10.2026): у текста две роли — третий и четвёртый серый стали подписью ([UI-63]).
  'fg3': 'on-surface-variant', 'fg4': 'on-surface-variant',
  'base': 'surface-container-low', 'muted': 'surface-container', 'muted-hover': 'surface-container-high',
  'stroke': 'outline-variant', 'stroke-strong': 'outline',
  'brand': 'primary', 'brand-subtle': 'primary-container', 'on-brand': 'on-primary', 'on-brand-subtle': 'on-primary-container',
  'tonal': 'secondary-container', 'on-tonal': 'on-secondary-container',
  'tertiary-subtle': 'tertiary-container', 'on-tertiary-subtle': 'on-tertiary-container',
  'success-subtle': 'success-container', 'warning-subtle': 'warning-container',
  'danger': 'error', 'danger-subtle': 'error-container', 'on-danger': 'on-error',
};
const UTILITIES = ['bg', 'text', 'border', 'border-t', 'ring', 'outline', 'divide', 'fill', 'stroke', 'from',
  'accent', 'caret', 'decoration'];

let declarations;
beforeAll(async () => {
  declarations = await loadDeclarations(srcRoot);
  declarations.prime(UTILITIES.flatMap(u => Object.entries(ALIAS_ROLE).flatMap(([old, role]) => [`${u}-${old}`, `${u}-${role}`])));
});

describe('тема MD3: старые имена рисуют прежнее', () => {
  it('перечень старых имён в описании темы — тот же, что в договоре', () => {
    expect(Object.keys(theme.aliases).filter(k => !k.startsWith('//')).sort())
      .toEqual(Object.keys(ALIAS_ROLE).sort());
  });

  it.each(Object.entries(ALIAS_ROLE))('%s ≡ %s в обеих темах', (old, role) => {
    for (const mode of ['light', 'dark'])
      for (const u of UTILITIES) {
        const was = declarations(`${u}-${old}`, mode);
        const now = declarations(`${u}-${role}`, mode);
        expect(was, `${u}-${old} Tailwind не порождает`).not.toBeNull();
        expect(was, `${u}-${old} рисует не то же, что ${u}-${role} (${mode === 'light' ? 'светлая' : 'тёмная'} тема): `
          + `псевдоним «${old}» в src/theme/md3-theme.json обязан указывать на роль «${role}»`).toBe(now);
      }
  });

  it('своих значений у старых имён нет: --legacy-* снят (sharedui-md3-05)', () => {
    expect(generate(readTheme())['md3-legacy.css']).not.toContain('--legacy-');
  });
});

describe('тема MD3: сервер согласован с палитрой', () => {
  // Сервер повторяет два цвета темы ([UI-59]); прочитанные текстом константы — те, что едут в сборку.
  const cases = [
    { file: 'BHS.CRG.Application/Branding/BrandIcons.cs', name: 'BrandManifest.BackgroundColor', role: 'light.surface-container-low',
      value: text => text.match(/BackgroundColor\s*=\s*"(#[0-9a-fA-F]{6})"/)?.[1] },
    { file: 'BHS.CRG.Infrastructure/Branding/BrandIconComposer.cs', name: 'BrandIconComposer.DarkTile', role: 'dark.surface',
      value: text => {
        const m = text.match(/DarkTile\s*=\s*new\(0x(\w{2}),\s*0x(\w{2}),\s*0x(\w{2})\)/);
        return m && `#${m[1]}${m[2]}${m[3]}`;
      } },
  ];

  it.each(cases)('$name = $role', ({ file, name, role, value }) => {
    const [mode, key] = role.split('.');
    const want = palette.roles[mode][key];
    const got = value(read(join(serverRoot, file)));
    expect(got, `Не найдено значение ${name} в src/server/${file}`).toBeTruthy();
    expect(got.toLowerCase(), `src/server/${file}: ${name} = ${got}, а роль ${role} в src/theme/md3-theme.json — ${want}.\n`
      + 'Сервер повторяет цвет темы — поправьте константу вместе с палитрой.').toBe(want.toLowerCase());
  });
});

describe('тема MD3: генератор отказывает кривому описанию', () => {
  // Описание со значениями (режим values): в режиме seed роли и свои роли считает генератор.
  const variant = change => { const t = structuredClone(palette); change(t); return t; };

  it('роль только одной темы', () => {
    expect(() => generate(variant(t => { t.roles.dark['surface-bright-2'] = '#000000'; })))
      .toThrow('у roles.surface-bright-2 нет значения светлой темы');
    expect(() => generate(variant(t => { delete t.ext.dark.success; })))
      .toThrow('у ext.success нет значения тёмной темы');
  });

  it('псевдоним с именем роли', () => {
    expect(() => generate(variant(t => { t.aliases.surface = 'primary'; })))
      .toThrow('псевдоним «surface» совпадает с именем роли');
  });

  it('половина превью значка числом, а не ссылкой на роль', () => {
    expect(() => generate(variant(t => { t.static['tab-light'] = '#f4f2f7'; })))
      .toThrow('static.tab-light — «#f4f2f7» не ссылка');
  });
});

describe('тема MD3: классы ролей', () => {
  it('половины превью значка — классом роли, с фоном страницы своей темы', () => {
    expect(declarations('bg-tab-light', 'dark')).toBe(`background-color: ${palette.roles.light['surface-container-low']};`);
    expect(declarations('bg-tab-dark', 'light')).toBe(`background-color: ${palette.roles.dark['surface-container-low']};`);
  });

  it('у роли shadow класса нет: text-shadow — не цвет текста', () => {
    // Цвет тени рисуют уровни --md-sys-elevation-*; класс роли перехватил бы утилиты тени.
    expect(declarations('text-shadow', 'light')).toBeNull();
    expect(declarations('bg-shadow', 'light')).toBeNull();
  });
});

describe('тема MD3: состояния и движение', () => {
  const variant = change => { const t = structuredClone(palette); change(t); return t; };

  it('кривые MD3 — классами ease-*, значением токена', () => {
    // Класс нужен сторожу словаря и экранам: без него ease-standard молча не порождал бы ничего.
    expect(declarations('ease-standard', 'light'))
      .toContain('transition-timing-function: cubic-bezier(0.2, 0, 0, 1);');
    expect(declarations('ease-emphasized-decelerate', 'dark')).toContain('cubic-bezier(0.05, 0.7, 0.1, 1)');
  });

  it('прозрачности слоя и длительности — переменными без темы', () => {
    const tokens = generate(theme)['md3-tokens.css'];
    const root = tokens.slice(0, tokens.indexOf(':root[data-theme="dark"]'));
    for (const line of ['--md-sys-state-hover: 0.08;', '--md-sys-state-focus: 0.1;', '--md-sys-state-pressed: 0.1;',
      '--md-sys-state-dragged: 0.16;', '--md-sys-motion-duration-short3: 150ms;'])
      expect(root).toContain(line);
    expect(tokens.slice(tokens.indexOf(':root[data-theme="dark"]'))).not.toContain('--md-sys-state-');
  });

  it('отказ: прозрачность не долей, кривая не cubic-bezier, длительность не в мс', () => {
    expect(() => generate(variant(t => { t.state.hover = '.08'; }))).toThrow('state.hover — «.08», а не доля');
    expect(() => generate(variant(t => { t.state.hover = 8; }))).toThrow('state.hover — «8», а не доля');
    expect(() => generate(variant(t => { t.motion.easing.standard = 'ease'; })))
      .toThrow('motion.easing.standard — «ease», а не cubic-bezier');
    expect(() => generate(variant(t => { t.motion.duration.short1 = '.05s'; })))
      .toThrow('motion.duration.short1 — «.05s», а не миллисекунды');
    expect(() => generate(variant(t => { delete t.state; }))).toThrow('раздел state пуст');
  });
});

describe('тема MD3: форма и возвышение ([UI-66])', () => {
  const variant = change => { const t = structuredClone(palette); change(t); return t; };

  it.each([['xs', 4], ['sm', 8], ['md', 12], ['lg', 16], ['xl', 28]])('rounded-%s — %i px в обеих темах', (step, px) => {
    // Число — договор шкалы, а не копия описания: перепутанная в md3-theme.json ступень краснит здесь.
    for (const mode of ['light', 'dark']) expect(declarations(`rounded-${step}`, mode)).toBe(`border-radius: ${px}px;`);
  });

  it.each([
    [1, '0 1px 2px 0', '0 1px 3px 1px'], [2, '0 1px 2px 0', '0 2px 6px 2px'], [3, '0 1px 3px 0', '0 4px 8px 3px'],
    [4, '0 2px 3px 0', '0 6px 10px 4px'], [5, '0 4px 4px 0', '0 8px 12px 6px'],
  ])('shadow-elevation-%i — слои Material Web цветом роли shadow, одни на обе темы', (n, key, ambient) => {
    const value = `${key} color-mix(in srgb, #000000 30%, transparent), ${ambient} color-mix(in srgb, #000000 15%, transparent)`;
    for (const mode of ['light', 'dark']) expect(declarations(`shadow-elevation-${n}`, mode)).toContain(value);
  });

  it('форма и возвышение — без темы: в тёмной теме не переопределяются', () => {
    const tokens = generate(theme)['md3-tokens.css'];
    const dark = tokens.slice(tokens.indexOf(':root[data-theme="dark"]'));
    expect(dark).not.toContain('--md-sys-shape-');
    expect(dark).not.toContain('--md-sys-elevation-');
    expect(tokens).toContain('--md-sys-elevation-level0: none;');
  });

  it('отказ: ступень не в пикселях или пропущена, слой без доли, нет уровня', () => {
    expect(() => generate(variant(t => { t.shape.small = '.5rem'; }))).toThrow('shape.small — «.5rem», а не пиксели');
    expect(() => generate(variant(t => { delete t.shape.medium; }))).toThrow('в shape нет ступени medium (класс rounded-md)');
    expect(() => generate(variant(t => { t.elevation.level2 = ['0 2px 6px 2px']; })))
      .toThrow('elevation.level2 — слой «0 2px 6px 2px», а не «x y размытие растяжение доля»');
    expect(() => generate(variant(t => { delete t.elevation.level4; }))).toThrow('в elevation нет уровня level4');
  });
});

describe('тема MD3: палитра из seed', () => {
  const seeded = change => { const t = structuredClone(theme); change(t); return resolve(t); };

  it('описание — в режиме seed, без ручных значений ролей', () => {
    expect(theme.mode).toBe('seed');
    expect(theme.roles, 'роли считает генератор: раздел roles в md3-theme.json не пишут').toBeUndefined();
  });

  it('закреплённый primary светлой темы — сам seed, и роли, которые по MD3 и есть он', () => {
    const seed = theme.seed.toLowerCase();
    expect(palette.roles.light.primary).toBe(seed);
    expect(palette.roles.light['surface-tint']).toBe(seed);
    expect(palette.roles.dark['inverse-primary']).toBe(seed);
  });

  it('без закрепления primary светлой темы — тон генератора, не seed', () => {
    const free = seeded(t => { t.pinPrimary = false; });
    expect(free.roles.light.primary).not.toBe(theme.seed.toLowerCase());
    expect(free.roles.light['surface-tint']).toBe(free.roles.light.primary);
    expect(free.roles.dark['inverse-primary']).toBe(free.roles.light.primary);
  });

  it('гармонизация сдвигает свою роль к seed, без неё — свой цвет', () => {
    const harmonized = seeded(t => { for (const e of t.ext) e.harmonize = true; });
    for (const mode of ['light', 'dark'])
      for (const role of ['success', 'success-container', 'warning', 'warning-container'])
        expect(harmonized.ext[mode][role], `${mode}.${role}`).not.toBe(palette.ext[mode][role]);
  });

  it('смена seed меняет роли обеих тем', () => {
    const other = seeded(t => { t.seed = '#8a3ffc'; });
    for (const mode of ['light', 'dark'])
      for (const role of ['primary', 'primary-container', 'secondary-container', 'surface-container'])
        expect(other.roles[mode][role], `${mode}.${role}`).not.toBe(palette.roles[mode][role]);
  });

  it('свои роли — по четыре в обеих темах', () => {
    for (const mode of ['light', 'dark'])
      expect(Object.keys(palette.ext[mode]).sort()).toEqual(['success', 'warning'].flatMap(n =>
        [n, `on-${n}`, `${n}-container`, `on-${n}-container`]).sort());
  });

  it('отказ: неизвестный вариант схемы и seed не цветом', () => {
    expect(() => seeded(t => { t.variant = 'pastel'; })).toThrow('вариант схемы «pastel» неизвестен');
    expect(() => seeded(t => { t.seed = 'blue'; })).toThrow('seed «blue» — не цвет');
  });

  it('отказ: своя роль без имени, с чужим именем или не цветом', () => {
    const ext = entry => seeded(t => { t.ext = [...t.ext, entry]; });
    expect(() => ext({ value: '#1b8a5b' })).toThrow('имя своей роли «undefined» — не kebab-case');
    expect(() => ext({ name: 'Info', value: '#1b8a5b' })).toThrow('имя своей роли «Info» — не kebab-case');
    expect(() => ext({ name: 'success', value: '#1b8a5b' })).toThrow('своя роль «success» повторяет другую свою роль');
    expect(() => ext({ name: 'primary', value: '#1b8a5b' })).toThrow('своя роль «primary» повторяет роль схемы');
    expect(() => ext({ name: 'info', value: 'teal' })).toThrow('цвет своей роли «info» — «teal», а не #rrggbb');
  });
});

describe('тема MD3: контраст ролей', () => {
  it('выбранная схема проходит все пары в обеих темах', () => {
    const failures = contrastFailures(palette);
    expect(failures, `Пары ниже порога WCAG:\n${failures.join('\n')}\n`
      +'Значения ролей руками не правят: смените variant, contrast или свой цвет в md3-theme.json.').toEqual([]);
  });

  it('подпись на самом высоком контейнере тёмной темы — не ниже 4,5:1', () => {
    expect(contrast(palette.roles.dark['on-surface-variant'], palette.roles.dark['surface-container-highest']))
      .toBeGreaterThanOrEqual(4.5);
  });

  it('плохая палитра названа парой, темой и числом', () => {
    // Свой цвет с тёмным тоном контейнера: текст контейнера тонет в нём.
    const bad = structuredClone(palette);
    bad.ext.light['success-container'] = '#2e7d57';
    expect(contrastFailures(bad)).toContainEqual(
      expect.stringMatching(/^светлая: on-success-container на success-container — \d+,\d\d:1, нужно 4,5:1$/));
  });
});
