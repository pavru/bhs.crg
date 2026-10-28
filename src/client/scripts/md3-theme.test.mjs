import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { beforeAll, describe, expect, it } from 'vitest';
import { loadDesign } from './color-classes-lib.mjs';
import { generate, readTheme, themeDir } from './md3-theme.mjs';

// Сторож темы MD3 (эпик sharedui-md3, [UI-57] [UI-58] [UI-59]): почему файлы генерируются — в
// md3-theme.mjs.
const srcRoot = join(themeDir, '..');
const serverRoot = join(srcRoot, '..', '..', 'server');
const theme = readTheme();
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
  'fg1': 'on-surface', 'fg2': 'on-surface-variant', 'fg4': 'outline',
  'base': 'surface-container-low', 'muted': 'surface-container', 'muted-hover': 'surface-container-high',
  'stroke': 'outline-variant', 'stroke-strong': 'outline',
  'brand': 'primary', 'brand-subtle': 'primary-container', 'on-brand': 'on-primary', 'on-brand-subtle': 'on-primary-container',
  'tonal': 'secondary-container', 'on-tonal': 'on-secondary-container',
  'tertiary-subtle': 'tertiary-container', 'on-tertiary-subtle': 'on-tertiary-container',
  'success-subtle': 'success-container', 'warning-subtle': 'warning-container',
  'danger': 'error', 'danger-subtle': 'error-container', 'on-danger': 'on-error',
};
/** Имена без роли того же значения — держат своё до ребёнка эпика, который их снимает (04, 05). */
const OWN_VALUE = ['fg3', 'fg-disabled', 'brand-hover', 'brand-pressed', 'warning-border', 'stroke-subtle'];
const UTILITIES = ['bg', 'text', 'border', 'border-t', 'ring', 'outline', 'divide', 'fill', 'stroke', 'from',
  'accent', 'caret', 'decoration'];

/** Переменные :root светлой и тёмной темы из сгенерированных файлов — тёмная поверх светлой. */
function themeVars() {
  const light = new Map();
  const dark = new Map();
  for (const name of ['md3-tokens.css', 'md3-legacy.css']) {
    const css = read(join(themeDir, name));
    for (const [, selector, body] of css.matchAll(/^(:root(?:\[data-theme="dark"\])?) \{\n([^}]*)\}/gm))
      for (const [, k, v] of body.matchAll(/(--[\w-]+):\s*([^;]+);/g)) (selector === ':root' ? light : dark).set(k, v.trim());
  }
  return { light, dark: new Map([...light, ...dark]) };
}

let design;
let vars;
beforeAll(async () => {
  design = await loadDesign(read(join(srcRoot, 'index.css')), srcRoot);
  vars = themeVars();
});

/** Объявления класса без селектора, переменные — значениями темы. */
function declarations(cls, mode) {
  const [css] = design.candidatesToCss([cls]);
  if (css == null) return null;
  const resolve = text => text.replace(/var\((--[\w-]+)\)/g, (m, k) => vars[mode].has(k) ? resolve(vars[mode].get(k)) : m);
  return resolve(css.slice(css.indexOf('{') + 1, css.lastIndexOf('}'))).replace(/\s+/g, ' ').trim();
}

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
    const want = theme.roles[mode][key];
    const got = value(read(join(serverRoot, file)));
    expect(got, `Не найдено значение ${name} в src/server/${file}`).toBeTruthy();
    expect(got.toLowerCase(), `src/server/${file}: ${name} = ${got}, а роль ${role} в src/theme/md3-theme.json — ${want}.\n`
      + 'Сервер повторяет цвет темы — поправьте константу вместе с палитрой.').toBe(want.toLowerCase());
  });
});

describe('тема MD3: генератор отказывает кривому описанию', () => {
  const variant = change => { const t = structuredClone(theme); change(t); return t; };

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
    expect(declarations('bg-tab-light', 'dark')).toBe(`background-color: ${theme.roles.light['surface-container-low']};`);
    expect(declarations('bg-tab-dark', 'light')).toBe(`background-color: ${theme.roles.dark['surface-container-low']};`);
  });

  it('у роли shadow класса нет: text-shadow — не цвет текста', () => {
    // Цвет тени рисуют уровни --md-sys-elevation-*; класс роли перехватил бы утилиты тени.
    expect(declarations('text-shadow', 'light')).toBeNull();
    expect(declarations('bg-shadow', 'light')).toBeNull();
  });
});
