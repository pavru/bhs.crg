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
/** Имена без роли того же значения — держат своё до ребёнка эпика, который их снимает (04, 05). */
const OWN_VALUE = ['fg-disabled', 'brand-hover', 'brand-pressed', 'warning-border', 'stroke-subtle'];
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
      .toEqual([...Object.keys(ALIAS_ROLE), ...OWN_VALUE].sort());
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

  it.each(OWN_VALUE)('%s держит своё значение в обеих темах', old => {
    for (const mode of ['light', 'dark']) {
      const value = declarations(`bg-${old}`, mode);
      expect(value, `bg-${old} (${mode}) не разрешается в цвет`).toMatch(/^background-color: #[0-9a-f]{6};$/);
    }
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
