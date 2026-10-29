import { spawnSync } from 'node:child_process';
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, relative } from 'node:path';
import { beforeAll, describe, expect, it } from 'vitest';
import { COLOR_UTILITIES } from './class-scan.mjs';
import { loadDeclarations } from './color-classes-lib.mjs';
import { clientRoot, compile, formatReport, migrateFiles, migrateText, sourceFiles } from './md3-migrate.mjs';
import classMap from './md3-class-map.json' with { type: 'json' };

// Кодмод перевода старых имён (эпик sharedui-md3, [UI-58]): почему команда, а не sed, — в md3-migrate.mjs.
const run = (text, file = 'src/x.tsx') => migrateText(text, { file });
const migrated = text => run(text).text;

describe('md3:migrate: что переводит', () => {
  it('варианты, прозрачность и «!» сохраняются', () => {
    expect(migrated('dark:hover:bg-brand-subtle/60!')).toBe('dark:hover:bg-primary-container/60!');
    expect(migrated('"!text-fg1 data-[state=open]:bg-muted group-hover:border-stroke/[0.4] -outline-brand"'))
      .toBe('"!text-on-surface data-[state=open]:bg-surface-container group-hover:border-outline-variant/[0.4] -outline-primary"');
  });

  it('класс вне разметки: карта вариантов, clsx, константа, шаблон', () => {
    const source = [
      "const VARIANT = { primary: 'bg-brand text-on-brand', danger: 'bg-danger text-on-danger' };",
      "const cls = clsx('border-stroke', active && 'bg-tonal text-on-tonal');",
      "export const DEPTH = ['border-l-stroke-strong', 'bg-muted-hover'];",
      'const t = `${base} text-danger ring-offset-base`;',
    ].join('\n');
    expect(migrated(source)).toBe([
      "const VARIANT = { primary: 'bg-primary text-on-primary', danger: 'bg-error text-on-error' };",
      "const cls = clsx('border-outline-variant', active && 'bg-secondary-container text-on-secondary-container');",
      "export const DEPTH = ['border-l-outline', 'bg-surface-container-high'];",
      'const t = `${base} text-error ring-offset-surface-container-low`;',
    ].join('\n'));
  });

  it('класс в строке внутри строки — с экранированной кавычкой', () => {
    expect(migrated("'${on ? \\'border-brand-subtle\\' : \\'border-stroke\\'}'"))
      .toBe("'${on ? \\'border-primary-container\\' : \\'border-outline-variant\\'}'");
  });

  it('класс в селекторе CSS: querySelector, selector у getByText, файл стилей', () => {
    expect(migrated("querySelectorAll('span.bg-brand-subtle'); getByText('x', { selector: '.text-fg1' })"))
      .toBe("querySelectorAll('span.bg-primary-container'); getByText('x', { selector: '.text-on-surface' })");
    expect(migrated('.bg-brand:hover, .row.border-stroke { }')).toBe('.bg-primary:hover, .row.border-outline-variant { }');
  });

  it('путь файла с именем класса — не класс', () => {
    const source = "import icon from './bg-brand.svg'; url('text-fg1.png') 'bg-brand.tsx'";
    expect(migrated(source)).toBe(source);
    // А точка в конце фразы комментария — граница.
    expect(migrated('// фон — bg-brand.')).toBe('// фон — bg-primary.');
  });

  it('длинное имя — целиком: on-brand-subtle — не brand-subtle', () => {
    expect(migrated("'bg-on-brand-subtle text-on-tertiary-subtle bg-tertiary-subtle'"))
      .toBe("'bg-on-primary-container text-on-tertiary-container bg-tertiary-container'");
  });

  it('комментарий переводится тем же, отдельной строкой отчёта', () => {
    const { text, replaced } = run('// фон — bg-base\nconst a = "bg-base";');
    expect(text).toBe('// фон — bg-surface-container-low\nconst a = "bg-surface-container-low";');
    expect(replaced.map(r => [r.line, r.comment])).toEqual([[1, true], [2, false]]);
  });

  it('переменные: --f-* по карте, var(--color-<старое>) — по именам', () => {
    expect(migrated('shadow-[var(--f-shadow16)] bg-[var(--color-brand)] [--x:var(--f-brand-text)]'))
      .toBe('shadow-[var(--md-sys-elevation-level2)] bg-[var(--color-primary)] [--x:var(--md-sys-color-on-primary)]');
    expect(migrated('.a { color: var(--color-fg2); box-shadow: var(--f-shadow28); }'))
      .toBe('.a { color: var(--color-on-surface-variant); box-shadow: var(--md-sys-elevation-level3); }');
  });

  it('ветка со старыми классами в новом файле', () => {
    expect(migrated('<div className="text-fg2 bg-base shadow-[var(--f-shadow16)]" />'))
      .toBe('<div className="text-on-surface-variant bg-surface-container-low shadow-[var(--md-sys-elevation-level2)]" />');
  });

  it('не классы и роли MD3 не трогает', () => {
    const source = 'textual context-menu bg-[var(--x)] foo--bar text-(--x) bg-primary text-on-surface base muted';
    expect(run(source)).toEqual({ text: source, replaced: [], untouched: [] });
  });
});

describe('md3:migrate: неоднозначное не трогает, а называет', () => {
  const untouched = (text, file) => run(text, file).untouched.map(u => u.token);

  it('text-base — размер, не цвет', () => {
    const { text, untouched: u } = run('"text-base text-fg1"');
    expect(text).toBe('"text-base text-on-surface"');
    expect(u).toEqual([{ line: 1, token: 'text-base', reason: expect.stringContaining('размер текста') }]);
  });

  it('text-brand-hover без наведения и с ним — нет пары, не трогается', () => {
    const source = '"text-brand-hover hover:bg-brand-hover"';
    expect(migrated(source)).toBe(source);
    expect(untouched(source)).toEqual(['text-brand-hover', 'bg-brand-hover']);
  });

  it('у текста две роли (sharedui-md3-04): fg3, fg4 и text-stroke-strong → on-surface-variant', () => {
    expect(migrated('"text-fg3 text-fg4 placeholder:text-fg4 text-stroke-strong"'))
      .toBe('"text-on-surface-variant text-on-surface-variant placeholder:text-on-surface-variant text-on-surface-variant"');
  });

  it('обводки по статусу нет (sharedui-md3-04): warning-border и stroke-subtle → outline-variant', () => {
    expect(migrated('"border border-warning-border ring-1 ring-warning-border border-t border-stroke-subtle"'))
      .toBe('"border border-outline-variant ring-1 ring-outline-variant border-t border-outline-variant"');
  });

  it('роль обводки в тексте — не переводится: text-stroke-subtle, text-warning-border, decoration-stroke', () => {
    const source = '"text-stroke-subtle hover:text-warning-border decoration-stroke caret-stroke-strong"';
    expect(migrated(source)).toBe(source);
    expect(run(source).untouched).toEqual(['text-stroke-subtle', 'text-warning-border', 'decoration-stroke', 'caret-stroke-strong']
      .map(token => ({ line: 1, token, reason: expect.stringContaining('текст цветом обводки запрещён') })));
    // text-stroke-strong названа в classes — перекрывает запрет.
    expect(migrated('"text-stroke-strong"')).toBe('"text-on-surface-variant"');
  });

  it('переменная роли обводки — неоднозначна: бывает и цветом текста', () => {
    const source = '[color:var(--color-stroke-strong)] border-[var(--color-warning-border)]';
    expect(migrated(source)).toBe(source);
    expect(run(source).untouched).toEqual([
      { line: 1, token: '--color-stroke-strong', reason: expect.stringContaining('текст с stroke-strong') },
      { line: 1, token: '--color-warning-border', reason: expect.stringContaining('текст с warning-border') },
    ]);
  });

  it('та же пара в другой утилите переводится: border-stroke-strong, bg-fg4', () => {
    expect(migrated('"border-stroke-strong bg-fg4"')).toBe('"border-outline bg-on-surface-variant"');
  });

  it('роль текста в утилите линии — не переводится: border-fg4, border-l-fg4, ring-fg4, divide-fg4', () => {
    const source = '"border-fg4 border-l-fg4 hover:ring-fg4/40 divide-fg4 outline-fg4"';
    expect(migrated(source)).toBe(source);
    expect(run(source).untouched).toEqual(['border-fg4', 'border-l-fg4', 'ring-fg4', 'divide-fg4', 'outline-fg4']
      .map(token => ({ line: 1, token, reason: expect.stringContaining('обводка цветом текста запрещена') })));
  });

  it('lines пары делает неоднозначной и её переменную', () => {
    const map = { names: { fg9: { to: 'on-surface-variant', lines: 'роль текста' } }, classes: {}, vars: {} };
    expect(compile(map, ['fg9']).variable('--color-fg9'))
      .toEqual({ reason: expect.stringContaining('обводка с fg9 — роль текста') });
    expect(compile(map, ['fg9']).cls('fill-fg9')).toEqual({ to: 'fill-on-surface-variant', pair: 'fg9 → on-surface-variant' });
  });

  it('переменная имени, чей класс пропущен, — неоднозначна: утилиты у неё нет', () => {
    const rules = compile({ names: { fg4: { to: 'outline' } }, classes: { 'text-fg4': { skip: 'текст цветом обводки' } }, vars: {} }, ['fg4']);
    const source = 'border-l-[var(--color-fg4)]';
    const result = migrateText(source, { file: 'src/x.tsx', rules });
    expect(result.text).toBe(source);
    expect(result.untouched).toEqual([{ line: 1, token: '--color-fg4', reason: expect.stringContaining('text-fg4') }]);
    // Пропуск не о цвете (text-base) переменную не задевает.
    expect(migrated('[color:var(--color-base)]')).toBe('[color:var(--color-surface-container-low)]');
  });

  it('переменные: var(--color-fg3) — роль текста бывает и обводкой, --f-fg3 без строки в vars — не трогаются', () => {
    const source = 'border-l-[var(--color-fg3)] [color:var(--f-fg3)]';
    expect(migrated(source)).toBe(source);
    expect(run(source).untouched).toEqual([
      { line: 1, token: '--color-fg3', reason: expect.stringContaining('обводка цветом текста запрещена') },
      { line: 1, token: '--f-fg3', reason: expect.stringContaining('нет строки в vars') },
    ]);
  });

  it('отрицательное ожидание теста — не трогается, называется', () => {
    const source = "expect(cls).not.toContain('text-danger');\nexpect(cls).toContain('text-danger');";
    const { text, untouched: u } = run(source, 'src/x.test.tsx');
    expect(text).toBe("expect(cls).not.toContain('text-danger');\nexpect(cls).toContain('text-error');");
    expect(u).toEqual([{ line: 1, token: 'text-danger', reason: expect.stringContaining('отрицательное ожидание') }]);
    // Вне теста «.not.» — просто текст.
    expect(migrated("a.not.b('text-danger')")).toBe("a.not.b('text-error')");
  });

  it('отрицательное ожидание на нескольких строках и без .not. — тоже', () => {
    const source = [
      'expect(cls)',
      '  .not',
      ".toContain('text-danger');",
      "expect(el.classList.contains('bg-brand')).toBe(false);",
      "expect(screen.queryByText('x', { selector: '.bg-brand' })).toBeNull();",
      "const keep = 'bg-brand'; expect(x).not.toBe(1);",
    ].join('\n');
    const { text, untouched: u } = run(source, 'src/x.test.tsx');
    expect(u.map(x => [x.line, x.token])).toEqual([[3, 'text-danger'], [4, 'bg-brand'], [5, 'bg-brand']]);
    // Класс до `expect(` той же строки — не в цепочке ожидания: переводится.
    expect(text.split('\n')[5]).toBe("const keep = 'bg-primary'; expect(x).not.toBe(1);");
  });
});

describe('md3:migrate: один проход', () => {
  it('цепная карта a → b, b → c: из «a b» — «b c»', () => {
    const rules = compile({ names: { a: { to: 'b' }, b: { to: 'c' } } }, []);
    expect(migrateText('"bg-a bg-b"', { rules }).text).toBe('"bg-b bg-c"');
  });

  it('второй прогон по выходу первого ничего не меняет', () => {
    const source = '"dark:hover:bg-brand-subtle/60! text-fg2 text-base text-fg3" // bg-muted\nvar(--f-shadow4) var(--color-danger)';
    const once = migrated(source);
    expect(once).not.toBe(source);
    const twice = run(once);
    expect(twice.text).toBe(once);
    expect(twice.replaced).toEqual([]);
  });

  it('src/theme/ и scripts/ не обходятся при любом корне: там сами псевдонимы и фикстуры старых имён', () => {
    const files = sourceFiles(clientRoot).map(f => relative(clientRoot, f).replaceAll('\\', '/'));
    expect(files).toContain('src/main.tsx');
    expect(files.filter(f => /^(?:src\/theme|scripts|node_modules)\//.test(f))).toEqual([]);
    expect(sourceFiles(join(clientRoot, 'src', 'theme'))).toEqual([]);
  });

  it('второй прогон по дереву — «замен 0», --check — код 0', () => {
    const dir = mkdtempSync(join(tmpdir(), 'md3-migrate-'));
    try {
      writeFileSync(join(dir, 'a.tsx'), '<p className="text-fg1 bg-base" />\n', 'utf8');
      writeFileSync(join(dir, 'b.css'), '.b { box-shadow: var(--f-shadow16); }\n', 'utf8');
      const cli = args => spawnSync(process.execPath, [join(import.meta.dirname, 'md3-migrate.mjs'), ...args], { encoding: 'utf8' });

      expect(cli(['--check', dir]).status, 'до перевода --check обязан отказать').toBe(1);
      const first = migrateFiles(sourceFiles(dir), { write: true, base: dir });
      expect(first.flatMap(r => r.replaced)).toHaveLength(3);
      expect(readFileSync(join(dir, 'a.tsx'), 'utf8')).toBe('<p className="text-on-surface bg-surface-container-low" />\n');

      const second = migrateFiles(sourceFiles(dir), { write: true, base: dir });
      expect(formatReport(second)).toMatch(/^md3:migrate: замен 0 /);
      const check = cli(['--check', dir]);
      expect(check.status, check.stdout + check.stderr).toBe(0);
    } finally {
      rmSync(dir, { recursive: true, force: true });
    }
  });
});

describe('md3:migrate: пары карты рисуют то же', () => {
  // Каждая цветовая утилита по отдельности: `border-[xytblrse]` — восемь сторон.
  const utilities = COLOR_UTILITIES.split('|')
    .flatMap(u => u === 'border-[xytblrse]' ? [...'xytblrse'].map(s => `border-${s}`) : [u]);
  let declarations;
  beforeAll(async () => {
    declarations = await loadDeclarations(join(import.meta.dirname, '..', 'src'));
    const names = Object.entries(classMap.names).filter(([k]) => !k.startsWith('//')).flatMap(([old, { to }]) => [old, to]);
    declarations.prime(utilities.flatMap(u => names.map(n => `${u}-${n}`)));
  });

  /** Пары `same: true`, у которых старый и новый класс рисуют разное, — с утилитой и темой. */
  const mismatches = names => Object.entries(names).filter(([k, v]) => !k.startsWith('//') && v.same).flatMap(([old, { to }]) =>
    ['light', 'dark'].flatMap(mode => {
      const theme = mode === 'light' ? 'светлая' : 'тёмная';
      if (declarations(`bg-${old}`, mode) == null) return [`${old} → ${to}: bg-${old} Tailwind не порождает`];
      return utilities.filter(u => declarations(`${u}-${old}`, mode) !== declarations(`${u}-${to}`, mode))
        .map(u => `${u}-${old} → ${u}-${to} (${theme} тема): ${declarations(`${u}-${old}`, mode)} ≠ ${declarations(`${u}-${to}`, mode)}`);
    }));

  it('каждая пара same: true × цветовые утилиты — те же объявления в обеих темах', () => {
    expect(mismatches(classMap.names), 'Пара карты с same: true меняет вид — уберите same или поправьте пару в '
      + 'scripts/md3-class-map.json').toEqual([]);
  });

  it('пара с другим значением краснеет и называет пару и тему', () => {
    const bad = mismatches({ 'brand-hover': { to: 'primary', same: true } });
    expect(bad.length).toBeGreaterThan(0);
    expect(bad[0]).toMatch(/^bg-brand-hover → bg-primary \(светлая тема\)/);
    expect(bad.some(m => m.includes('(тёмная тема)'))).toBe(true);
  });
});

describe('md3:migrate: текст на контейнере — парной ролью (sharedui-md3-04)', () => {
  it('в одном списке классов: text-<роль> на bg-<роль>-container → text-on-<роль>-container', () => {
    expect(migrated('"rounded bg-warning-container text-warning px-2"')).toBe('"rounded bg-warning-container text-on-warning-container px-2"');
    expect(migrated("cls: 'bg-error-container text-error/80'")).toBe("cls: 'bg-error-container text-on-error-container/80'");
    expect(migrated('"bg-danger-subtle text-danger"')).toBe('"bg-error-container text-on-error-container"');
  });

  it('многострочный список JSX — тоже', () => {
    expect(migrated('className="flex\n  bg-success-container text-success"')).toBe('className="flex\n  bg-success-container text-on-success-container"');
  });

  it('вариант обязан совпасть: контейнер под наведением — текст не трогается', () => {
    const source = '"text-warning hover:bg-warning-container"';
    expect(migrated(source)).toBe(source);
    expect(migrated('"hover:bg-warning-container hover:text-warning"')).toBe('"hover:bg-warning-container hover:text-on-warning-container"');
  });

  it('условный контейнер в шаблоне и разнесённое по строкам — не трогается', () => {
    const conditional = "`${on ? 'bg-warning-container' : ''} text-warning`";
    expect(migrated(conditional)).toBe(conditional);
    const split = "const box = 'bg-warning-container'; const label = 'text-warning';";
    expect(migrated(split)).toBe(split);
  });

  it('постоянные части шаблона — один список', () => {
    expect(migrated('`bg-success-container ${x} text-success`')).toBe('`bg-success-container ${x} text-on-success-container`');
  });

  it('в комментарии — не трогается, повторный запуск ничего не меняет', () => {
    const comment = '// "bg-warning-container text-warning"';
    expect(migrated(comment)).toBe(comment);
    const once = migrated('"bg-warning-container text-warning"');
    expect(migrated(once)).toBe(once);
  });

  it('строка внутри ${…} — свой список: условная плашка целиком переводится', () => {
    expect(migrated("`px-2 ${open ? 'bg-error-container text-error' : 'bg-surface-container'}`"))
      .toBe("`px-2 ${open ? 'bg-error-container text-on-error-container' : 'bg-surface-container'}`");
    // Скобка в строке выражения не сбивает счёт `${…}`: постоянные части за ним — тот же список.
    expect(migrated("`bg-success-container ${x ? '}' : '{'} text-success`"))
      .toBe("`bg-success-container ${x ? '}' : '{'} text-on-success-container`");
  });

  it('кавычка в регулярном выражении и в тексте JSX не переворачивает разбор', () => {
    expect(migrated('const re = /["\']/;\nconst c = "bg-warning-container text-warning";'))
      .toBe('const re = /["\']/;\nconst c = "bg-warning-container text-on-warning-container";');
    expect(migrated('<p>Нажмите "OK</p>\n<div className="bg-error-container text-error" />'))
      .toBe('<p>Нажмите "OK</p>\n<div className="bg-error-container text-on-error-container" />');
  });

  it('отрицательное ожидание теста — не трогается, называется', () => {
    const source = "expect(cls).not.toContain('bg-error-container text-error');";
    const { text, untouched: u } = run(source, 'src/x.test.tsx');
    expect(text).toBe(source);
    expect(u).toEqual([{ line: 1, token: 'text-error', reason: expect.stringContaining('отрицательное ожидание') }]);
  });
});
