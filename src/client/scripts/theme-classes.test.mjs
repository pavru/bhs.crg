import { readdirSync, readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { beforeAll, describe, expect, it } from 'vitest';
import { stripComments, themeCandidates } from './class-scan.mjs';
import { loadThemeCheck, nearestRole } from './theme-classes-lib.mjs';

// Сторож словаря темы ([UI-82]; до финала MD3 — сторож цветовых классов, issue #1362): почему так —
// в theme-classes-lib.mjs.
const srcRoot = join(dirname(fileURLToPath(import.meta.url)), '..', 'src');
let unknown;
beforeAll(async () => { unknown = await loadThemeCheck(join(srcRoot, 'index.css')); });

/** Файлы клиента — `путь от src/ → текст`, читаются один раз на все проверки файла. */
const sources = new Map(readdirSync(srcRoot, { recursive: true })
  .map(f => String(f).replaceAll('\\', '/'))
  .filter(f => /\.(?:tsx?|jsx?|css)$/.test(f))
  .map(f => [f, readFileSync(join(srcRoot, f), 'utf8')]));
/** Код без стилей: классы сторож ищет в нём, переменные — везде. */
const scripts = [...sources].filter(([f]) => !f.endsWith('.css'));

/**
 * Произвольные значения, у которых в словаре нет ступени, — по месту и с причиной. Класс из списка в
 * другом файле — снова отказ: исключение названо местом, а не классом вообще.
 */
const EXCEPTIONS = [
  { cls: 'rounded-[2px]', files: /^shared\/ui\/Checkbox(?:\.test)?\.tsx$/,
    reason: 'контейнер флажка MD3 — 2 dp, ниже шкалы формы (ступень xs — 4 px)' },
  { cls: 'text-[0.9em]', files: /^shared\/ui\/Markdown\.tsx$/,
    reason: 'код в строке Markdown — доля текста вокруг: роль текста задаёт абсолютный размер, а строка бывает в любой роли' },
];
const excused = (cls, file) => EXCEPTIONS.some(e => e.cls === cls && e.files.test(file));

/** Нарушения в тексте файла — как в отчёте сторожа: `файл:строка класс → роль`. */
const offendersIn = (file, text) => {
  const found = themeCandidates(text);
  const bad = new Set(unknown(found.map(c => c.cls)));
  return found.filter(c => bad.has(c.cls) && !excused(c.cls, file))
    .map(c => `src/${file}:${c.line} ${c.cls} → ${nearestRole(c.cls)}`);
};

describe('словарь темы: что сторож видит', () => {
  it('утилиту с вариантами, прозрачностью и в шаблонной строке', () => {
    expect(themeCandidates('hover:bg-surface2 has-[:disabled]:bg-surface-container `${a} border-error/30` "text-fg"')
      .map(c => c.cls)).toEqual(['bg-surface2', 'bg-surface-container', 'border-error/30', 'text-fg']);
  });

  it('номер строки', () => {
    expect(themeCandidates('a\nb\n  className="p-1 text-fg"')).toEqual([{ cls: 'text-fg', line: 3 }]);
  });

  it('комментарии пропускает, строки — нет; номер строки после комментария верен', () => {
    expect(themeCandidates('/* text-fg\n bg-surface2 */ "https://x" // text-fg\n<p className="text-fg"/>'))
      .toEqual([{ cls: 'text-fg', line: 3 }]);
    expect(themeCandidates(`{/* text-xs/fg2 */} '//' + "bg-surface2"`).map(c => c.cls)).toEqual(['bg-surface2']);
  });

  it('радиус, размер текста и тень — ступенью, без ступени и произвольным значением', () => {
    expect(themeCandidates('"rounded-2xl rounded rounded-t md:rounded-tl-lg text-2xl text-[11px] text-[0.9em] '
      + 'text-(length:--x) shadow-lg shadow-[0_1px_2px_#000] rounded-[28px] drop-shadow-(--x)"').map(c => c.cls))
      .toEqual(['rounded-2xl', 'rounded', 'rounded-t', 'rounded-tl-lg', 'text-2xl', 'text-[11px]', 'text-[0.9em]',
        'text-(length:--x)', 'shadow-lg', 'shadow-[0_1px_2px_#000]', 'rounded-[28px]', 'drop-shadow-(--x)']);
  });

  it('произвольный text- берёт весь — цвет он или размер, решает движок', () => {
    expect(themeCandidates('"text-(--x) text-[var(--x)] text-[calc(1rem+2px)]"').map(c => c.cls))
      .toEqual(['text-(--x)', 'text-[var(--x)]', 'text-[calc(1rem+2px)]']);
  });

  it('произвольный цвет прочих утилит и не-классы не берёт', () => {
    expect(themeCandidates('bg-[var(--md-sys-color-surface-container)] border-(--x) context-menu textual rounded-corner.svg'))
      .toEqual([]);
  });

  it('голый rounded — только в строке: вне неё это имя переменной или свойства JSX', () => {
    expect(themeCandidates('function f({ value, rounded }) { return rounded ? 1 : 0 }\nconst x = <S rounded />;')).toEqual([]);
    expect(themeCandidates('const c = on ? "rounded p-1" : `rounded-t ${x}`;').map(c => c.cls)).toEqual(['rounded', 'rounded-t']);
  });
});

describe('словарь темы: что вне него', () => {
  it('имена, которых нет в @theme, — отказ', () => {
    expect(unknown(['bg-surface2', 'text-fg', 'text-on-surface-muted', 'border-danger-border', 'stroke-strong']))
      .toEqual(['bg-surface2', 'text-fg', 'text-on-surface-muted', 'border-danger-border', 'stroke-strong']);
  });

  it('произвольное значение размера текста, радиуса и тени — отказ', () => {
    expect(unknown(['text-[11px]', 'rounded-[28px]', 'rounded-t-[6px]', 'shadow-[0_1px_2px_#000]', 'drop-shadow-(--x)']))
      .toEqual(['text-[11px]', 'rounded-[28px]', 'rounded-t-[6px]', 'shadow-[0_1px_2px_#000]', 'drop-shadow-(--x)']);
  });

  it('произвольный text- — отказ, когда Tailwind ставит им размер, а не цвет', () => {
    expect(unknown(['text-[calc(1rem+2px)]', 'text-[clamp(1rem,2vw,2rem)]', 'text-(length:--x)', 'text-[var(--x)]',
      'text-(--x)', 'text-[color:var(--x)]'])).toEqual(['text-[calc(1rem+2px)]', 'text-[clamp(1rem,2vw,2rem)]', 'text-(length:--x)']);
  });

  it('ступени тени у drop-shadow, inset-shadow и text-shadow — вне словаря', () => {
    expect(unknown(['drop-shadow-md', 'inset-shadow-sm', 'text-shadow-sm', 'shadow-elevation-1']))
      .toEqual(['drop-shadow-md', 'inset-shadow-sm', 'text-shadow-sm']);
  });

  it('роли MD3 — и те, которыми ещё никто не рисует, — проходят', () => {
    expect(unknown(['bg-primary', 'text-on-surface-variant', 'bg-surface-container-high', 'border-outline-variant',
      'bg-inverse-surface', 'text-inverse-on-surface', 'bg-scrim/32', 'text-on-success-container'])).toEqual([]);
  });

  it('ступени формы, роли текста, уровни возвышения и не-словарные значения тех же утилит — проходят', () => {
    expect(unknown(['rounded-xs', 'rounded-t-sm', 'rounded-xl', 'rounded-full', 'rounded-none', 'text-label-small',
      'text-body-medium', 'shadow-elevation-2', 'shadow-none', 'text-left', 'border-2', 'border-t', 'ring-2',
      'outline-none', 'bg-transparent', 'bg-current', 'text-inherit', 'border-error/30'])).toEqual([]);
  });
});

describe('словарь темы: исключения', () => {
  it('названы местом: тот же класс в другом файле — отказ', () => {
    expect(excused('rounded-[2px]', 'shared/ui/Checkbox.tsx')).toBe(true);
    expect(excused('rounded-[2px]', 'features/costs/InvoiceForm.tsx')).toBe(false);
  });

  it('каждое исключение ещё стоит на своём месте: снятое снимается и из списка', () => {
    const stale = EXCEPTIONS.filter(e => !scripts.some(([f, text]) => e.files.test(f)
      && themeCandidates(text).some(c => c.cls === e.cls)));
    expect(stale.map(e => e.cls)).toEqual([]);
  });
});

describe('словарь темы: отказ называет роль', () => {
  it.each([
    ['text-fg2', 'text-on-surface-variant'],
    ['hover:bg-brand', 'bg-primary'],
    ['border-stroke/50', 'border-outline-variant/50'],
    ['text-xs', 'text-body-small'],
    ['bg-black/40', 'bg-scrim/32'],
    // Своя прозрачность пары главнее прозрачности класса: не `text-on-surface/38/50`.
    ['text-fg-disabled/50', 'text-on-surface/38'],
  ])('%s → %s', (cls, role) => {
    expect(nearestRole(themeCandidates(`"${cls}"`)[0].cls)).toBe(role);
  });

  it('файл экрана со старым именем, палитрой, ступенями Tailwind и своим значением — отказ на каждом', () => {
    // Фикстура задачи 1.2 и сценария [UI-82]: класс в className, в clsx, в карте вариантов и в шаблоне.
    const screen = [
      'const tone = { quiet: "text-fg2", raw: "bg-gray-500" };',
      'export const X = ({ on }) => <div className={clsx("rounded-2xl", on && "text-[11px]")}>',
      '  <p className={`shadow-lg ${on ? "text-fg1" : ""}`} />',
      '</div>;',
    ].join('\n');
    expect(offendersIn('features/x.tsx', screen)).toEqual([
      'src/features/x.tsx:1 text-fg2 → text-on-surface-variant',
      'src/features/x.tsx:1 bg-gray-500 → роль цвета MD3: text-on-surface, bg-surface-container, border-outline-variant, text-primary… (src/theme/md3-theme.json; docs/UI.md, «Цвет»)',
      'src/features/x.tsx:2 rounded-2xl → ступень формы: rounded-xs, -sm, -md, -lg, -xl, rounded-full, rounded-none (docs/UI.md, «Форма и возвышение»)',
      'src/features/x.tsx:2 text-[11px] → роль текста: text-body-medium, text-body-small, text-label-small, text-title-small… (docs/UI.md, «Текст»)',
      'src/features/x.tsx:3 shadow-lg → уровень возвышения: shadow-elevation-1…5 (docs/UI.md, «Форма и возвышение»)',
      'src/features/x.tsx:3 text-fg1 → text-on-surface',
    ]);
  });

  it('у текста роли обводки нет пары: отказ называет причину, а не похожий класс', () => {
    expect(nearestRole('text-stroke')).toMatch(/^текст цветом обводки запрещён/);
  });

  it('без пары в карте — шкала или раздел docs/UI.md', () => {
    expect(nearestRole('rounded-[28px]')).toMatch(/^ступень формы/);
    expect(nearestRole('shadow-lg')).toMatch(/^уровень возвышения/);
    expect(nearestRole('inset-shadow-sm')).toMatch(/^уровень возвышения/);
    expect(nearestRole('text-shadow-sm')).toMatch(/^уровень возвышения/);
    expect(nearestRole('text-[11px]')).toMatch(/^роль текста/);
    expect(nearestRole('bg-gray-500')).toMatch(/^роль цвета MD3/);
  });
});

/**
 * Переменные пространств имён темы (`var(--color-brand)`, `var(--radius-lg)`) вне слоя темы и вне своего
 * файла — `файл:строка переменная`. Сброшенное имя не определено нигде, и `var()` молча отдаёт пустое:
 * до финала это держал `legacy-class` храповика, теперь — сторож словаря. Своя переменная файла
 * (`--text-scale-factor` слоя текста просмотрщика) — его дело.
 */
const THEME_VAR = /var\(\s*(--(?:color|radius|text|(?:drop-|inset-)?shadow)-[\w-]+)/g;
const themeVars = new Set([...readFileSync(join(srcRoot, 'theme', 'md3-tailwind.css'), 'utf8')
  .matchAll(/^\s*(--[\w-]+):/gm)].map(m => m[1]));
const unknownVars = (file, source) => {
  const text = stripComments(source);
  const own = new Set([...text.matchAll(/(--[\w-]+)\s*:/g)].map(m => m[1]));
  return [...text.matchAll(THEME_VAR)].filter(m => !themeVars.has(m[1]) && !own.has(m[1]))
    .map(m => `src/${file}:${text.slice(0, m.index).split('\n').length} ${m[1]}`);
};

describe('словарь темы: переменные', () => {
  it('сброшенное имя — отказ; роль, ступень и своя переменная файла — нет', () => {
    expect(unknownVars('shared/ui/x.css', '.a { outline: 2px solid var(--color-brand); color: var(--color-primary); }\n'
      + '.b { border-radius: var(--radius-lg); --text-k: 2; font-size: calc(var(--text-k) * 1px); }  /* var(--color-fg1) */'))
      .toEqual(['src/shared/ui/x.css:1 --color-brand']);
    expect(unknownVars('shared/ui/x.css', '.c { filter: drop-shadow(var(--drop-shadow-md)); }'))
      .toEqual(['src/shared/ui/x.css:1 --drop-shadow-md']);
  });

  it('в клиенте нет переменных вне словаря темы', () => {
    expect([...sources].filter(([f]) => !f.startsWith('theme/')).flatMap(([f, text]) => unknownVars(f, text)),
      'Переменная вне словаря темы — var() отдаёт пустое. Цвет — var(--color-<роль>) или --md-sys-color-<роль>:\n'
      + 'docs/UI.md, «Цвет».').toEqual([]);
  });
});

it('в клиенте нет классов вне словаря темы', () => {
  const offenders = scripts.flatMap(([file, text]) => offendersIn(file, text));

  // Сообщение называет выход: без него класс заменяют первым похожим, и он снова не рисуется.
  expect(offenders, 'Классы вне словаря темы MD3 — Tailwind их не рисует, или значение мимо шкалы.\n'
    + 'Цвет — роль (src/theme/md3-theme.json), радиус — ступень формы, текст — роль текста, тень — уровень\n'
    + 'возвышения: docs/UI.md, «Цвет», «Форма и возвышение», «Текст». Старое имя из чужой ветки переводит\n'
    + 'npm run md3:migrate. В комментарии называйте класс целиком (bg-outline-variant, а не outline-variant).').toEqual([]);
});
