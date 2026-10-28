import { spawnSync } from 'node:child_process';
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { beforeAll, describe, expect, it } from 'vitest';
import { COLOR_UTILITIES } from './class-scan.mjs';
import { loadDeclarations } from './color-classes-lib.mjs';
import { compile, formatReport, migrateFiles, migrateText, sourceFiles } from './md3-migrate.mjs';
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

  it('text-brand-hover без наведения и с ним, fg3, text-fg4, text-stroke-strong, warning-border', () => {
    const source = '"text-brand-hover hover:bg-brand-hover text-fg3 text-fg4 text-stroke-strong border-warning-border"';
    expect(migrated(source)).toBe(source);
    expect(untouched(source)).toEqual(['text-brand-hover', 'bg-brand-hover', 'text-fg3', 'text-fg4',
      'text-stroke-strong', 'border-warning-border']);
  });

  it('та же пара в другой утилите переводится: border-stroke-strong, bg-fg4', () => {
    expect(migrated('"border-stroke-strong bg-fg4"')).toBe('"border-outline bg-outline"');
  });

  it('переменные без пары: var(--color-fg3), --f-fg3', () => {
    const source = 'border-l-[var(--color-fg3)] [color:var(--f-fg3)]';
    expect(migrated(source)).toBe(source);
    expect(untouched(source)).toEqual(['--color-fg3', '--f-fg3']);
  });

  it('отрицательное ожидание теста — не трогается, называется', () => {
    const source = "expect(cls).not.toContain('text-danger');\nexpect(cls).toContain('text-danger');";
    const { text, untouched: u } = run(source, 'src/x.test.tsx');
    expect(text).toBe("expect(cls).not.toContain('text-danger');\nexpect(cls).toContain('text-error');");
    expect(u).toEqual([{ line: 1, token: 'text-danger', reason: expect.stringContaining('отрицательное ожидание') }]);
    // Вне теста «.not.» — просто текст.
    expect(migrated("a.not.b('text-danger')")).toBe("a.not.b('text-error')");
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
  beforeAll(async () => { declarations = await loadDeclarations(join(import.meta.dirname, '..', 'src')); });

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
    const bad = mismatches({ fg3: { to: 'on-surface-variant', same: true } });
    expect(bad.length).toBeGreaterThan(0);
    expect(bad[0]).toMatch(/^bg-fg3 → bg-on-surface-variant \(светлая тема\)/);
    expect(bad.some(m => m.includes('(тёмная тема)'))).toBe(true);
  });
});
