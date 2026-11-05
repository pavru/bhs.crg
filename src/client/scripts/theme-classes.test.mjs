import { readdirSync, readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { beforeAll, describe, expect, it } from 'vitest';
import { themeCandidates } from './class-scan.mjs';
import { loadThemeCheck, nearestRole } from './theme-classes-lib.mjs';

// Сторож словаря темы ([UI-82]; до финала MD3 — сторож цветовых классов, issue #1362): почему так —
// в theme-classes-lib.mjs.
const srcRoot = join(dirname(fileURLToPath(import.meta.url)), '..', 'src');
let unknown;
beforeAll(async () => { unknown = await loadThemeCheck(join(srcRoot, 'index.css')); });

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

  it('произвольный цвет и не-классы не берёт', () => {
    expect(themeCandidates('bg-[var(--md-sys-color-surface-container)] text-(--x) text-[color:var(--x)] context-menu textual rounded-corner.svg'))
      .toEqual([]);
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
    const files = readdirSync(srcRoot, { recursive: true }).map(f => String(f).replaceAll('\\', '/'));
    const stale = EXCEPTIONS.filter(e => !files.some(f => e.files.test(f)
      && themeCandidates(readFileSync(join(srcRoot, f), 'utf8')).some(c => c.cls === e.cls)));
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
    expect(nearestRole('text-[11px]')).toMatch(/^роль текста/);
    expect(nearestRole('bg-gray-500')).toMatch(/^роль цвета MD3/);
  });
});

it('в клиенте нет классов вне словаря темы', () => {
  const files = readdirSync(srcRoot, { recursive: true })
    .map(f => String(f).replaceAll('\\', '/'))
    .filter(f => /\.(tsx?|jsx?)$/.test(f));
  const offenders = files.flatMap(file => offendersIn(file, readFileSync(join(srcRoot, file), 'utf8')));

  // Сообщение называет выход: без него класс заменяют первым похожим, и он снова не рисуется.
  expect(offenders, 'Классы вне словаря темы MD3 — Tailwind их не рисует, или значение мимо шкалы.\n'
    + 'Цвет — роль (src/theme/md3-theme.json), радиус — ступень формы, текст — роль текста, тень — уровень\n'
    + 'возвышения: docs/UI.md, «Цвет», «Форма и возвышение», «Текст». Старое имя из чужой ветки переводит\n'
    + 'npm run md3:migrate. В комментарии называйте класс целиком (bg-outline-variant, а не outline-variant).').toEqual([]);
});
