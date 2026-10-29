import { execFileSync } from 'node:child_process';
import { mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import { migrateFiles, migrateText } from './md3-migrate.mjs';
import { commitsAfterTransition, gitOldLines, migrateShadows } from './md3-shape-migrate.mjs';

// Шаги формы и возвышения команды md3:migrate (sharedui-md3-06, [UI-66]): почему цепь радиусов
// смотрит в git blame — в md3-shape-migrate.mjs.
const old = (text, file = 'src/x.tsx') => migrateText(text, { file, isOld: () => true });

describe('md3:migrate: радиусы — цепью по значению', () => {
  it('ступени: 4 → xs, 6 → sm, 10 → md, 16 → lg, 28 → xl', () => {
    expect(old("'rounded rounded-sm rounded-md rounded-lg rounded-xl rounded-[28px]'").text)
      .toBe("'rounded-xs rounded-xs rounded-sm rounded-md rounded-lg rounded-xl'");
  });

  it('rounded-md → rounded-sm один раз: результат замены не заменяется снова', () => {
    expect(old('"rounded-md rounded-lg"').text).toBe('"rounded-sm rounded-md"');
  });

  it('стороны, углы, варианты и «!» сохраняются', () => {
    expect(old('"rounded-t-md rounded-tl-lg rounded-b rounded-s-xl md:rounded-md hover:!rounded-lg rounded-r-lg!"').text)
      .toBe('"rounded-t-sm rounded-tl-md rounded-b-xs rounded-s-lg md:rounded-sm hover:!rounded-md rounded-r-md!"');
  });

  it('на шкале и full — не трогает и не называет', () => {
    const { text, replaced, untouched } = old('"rounded-full rounded-none rounded-l-full rounded-xs"');
    expect(text).toBe('"rounded-full rounded-none rounded-l-full rounded-xs"');
    expect([replaced, untouched]).toEqual([[], []]);
  });

  it('вне шкалы — в отчёт, а не по цепи: rounded-2xl, rounded-3xl, rounded-[…]', () => {
    const { text, untouched } = old('"rounded-2xl rounded-3xl rounded-[22%] rounded-t-[20px]"');
    expect(text).toBe('"rounded-2xl rounded-3xl rounded-[22%] rounded-t-[20px]"');
    expect(untouched.map(u => u.token)).toEqual(['rounded-2xl', 'rounded-3xl', 'rounded-[22%]', 'rounded-t-[20px]']);
    expect(untouched[0].reason).toContain('вне шкалы формы MD3');
  });

  it('строка после перехода не сдвигается: rounded-md в ней уже 12 px', () => {
    const source = 'const a = "rounded-md";\nconst b = "rounded-md";';
    expect(migrateText(source, { isOld: line => line === 2 }).text).toBe('const a = "rounded-md";\nconst b = "rounded-sm";');
    // Без сведений о строках — ни одной: радиус не сдвигается вслепую.
    expect(migrateText(source).text).toBe(source);
  });

  it('не класс — не трогает: проп rounded="md", имя roundedCorner, путь файла', () => {
    const source = '<SearchInput rounded="md" /> const roundedCorner = 1; import x from "./rounded-md.svg";';
    expect(old(source).text).toBe(source);
    // Имя пропа в коде и ссылка на него в комментарии — не класс `rounded` (нашлось на SearchInput).
    const prop = "// через `rounded`\nfunction S({ rounded = 'md', a }) { return `${rounded === 'full' ? 'x' : 'y'}` + f(rounded) + {rounded}; }";
    expect(old(prop).text).toBe(prop);
    expect(old("@apply rounded; cls('p-2 rounded', `rounded border`)").text).toBe("@apply rounded-xs; cls('p-2 rounded-xs', `rounded-xs border`)");
  });

  it('отрицательное ожидание теста — не трогается, называется', () => {
    const { text, untouched } = old("expect(el.className).not.toContain('rounded-md');", 'src/x.test.tsx');
    expect(text).toBe("expect(el.className).not.toContain('rounded-md');");
    expect(untouched[0].reason).toContain('отрицательное ожидание');
  });
});

describe('md3:migrate: тени — классом уровня', () => {
  it('класс с переменной уровня → shadow-elevation-N, с вариантом', () => {
    expect(migrateShadows('"hover:shadow-[var(--md-sys-elevation-level2)] shadow-[var(--md-sys-elevation-level1)]"').text)
      .toBe('"hover:shadow-elevation-2 shadow-elevation-1"');
  });

  it('стиль с переменной уровня → класс того же элемента: строка и шаблон', () => {
    expect(migrateShadows([
      '<div className="z-50 rounded-sm"',
      "  style={{ boxShadow: 'var(--md-sys-elevation-level2)' }}>",
    ].join('\n')).text).toBe('<div className="z-50 rounded-sm shadow-elevation-2">');
    expect(migrateShadows("<div className={`a ${on ? 'b' : `c`} d`} style={{ boxShadow: 'var(--md-sys-elevation-level3)' }} />").text)
      .toBe("<div className={`a ${on ? 'b' : `c`} d shadow-elevation-3`} />");
  });

  it('стиль без className и условный стиль — не трогаются, называются', () => {
    const source = [
      "<span style={{ boxShadow: 'var(--md-sys-elevation-level1)' }} />",
      "<div className=\"x\" style={on ? undefined : { boxShadow: 'var(--md-sys-elevation-level3)' }} />",
    ].join('\n');
    const { text, untouched } = migrateShadows(source);
    expect(text).toBe(source);
    expect(untouched.map(u => u.line)).toEqual([1, 2]);
    expect(untouched[0].reason).toContain('shadow-elevation-N по роли слоя');
  });

  it('второй прогон ничего не меняет', () => {
    const once = migrateShadows("<i className=\"a\" style={{ boxShadow: 'var(--md-sys-elevation-level1)' }} />").text;
    expect(migrateShadows(once).text).toBe(once);
  });
});

describe('md3:migrate: радиусы по git blame — живой репозиторий', () => {
  let dir;
  const git = (...args) => execFileSync('git', args, { cwd: dir, encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] });
  const file = () => join(dir, 'src', 'x.tsx');
  const write = lines => writeFileSync(file(), `${lines.join('\n')}\n`);
  const read = () => readFileSync(file(), 'utf8').trimEnd().split('\n');
  const migrate = () => {
    const after = commitsAfterTransition(dir);
    return migrateFiles([file()], { write: true, base: dir, oldLines: p => gitOldLines(p, dir, after) });
  };
  const commit = message => { git('add', '-A'); git('commit', '-qm', message); };

  beforeAll(() => {
    dir = mkdtempSync(join(tmpdir(), 'md3-shape-'));
    mkdirSync(join(dir, 'src', 'theme'), { recursive: true });
    git('init', '-q', '-b', 'master');
    git('config', 'user.email', 't@t');
    git('config', 'user.name', 't');
    git('config', 'commit.gpgsign', 'false');
    git('config', 'core.autocrlf', 'false');
    writeFileSync(join(dir, 'src', 'theme', 'md3-tokens.css'), ':root { --x: 1; }\n');
    write(['const a = "rounded-md";', 'const b = "p-2";']);
    commit('до перехода');
    git('branch', 'feature');
  });
  afterAll(() => rmSync(dir, { recursive: true, force: true }));

  it('переход: старая строка сдвигается, коммит с меткой шкалы её закрепляет, повторный запуск — ноль замен', () => {
    migrate();
    expect(read()).toEqual(['const a = "rounded-sm";', 'const b = "p-2";']);
    // Ещё не закоммиченная строка не сдвигается: второй запуск до коммита ничего не меняет.
    expect(migrate().flatMap(r => r.replaced)).toEqual([]);
    writeFileSync(join(dir, 'src', 'theme', 'md3-tokens.css'), ':root { --md-sys-shape-corner-small: 8px; }\n');
    commit('переход на шкалу');
    expect(migrate().flatMap(r => r.replaced)).toEqual([]);
    // Строка после перехода: rounded-md уже 12 px.
    write([...read(), 'const c = "rounded-md";']);
    commit('после перехода');
    expect(migrate().flatMap(r => r.replaced)).toEqual([]);
    expect(read()[2]).toBe('const c = "rounded-md";');
  });

  it('чужая ветка, начатая до перехода: её строка сдвигается один раз, строки master — нет', () => {
    git('checkout', '-q', 'feature');
    write(['const a = "rounded-md";', 'const b = "p-2 rounded-lg";']);
    commit('ветка до перехода');
    // Порядок docs/UI.md: принять master в конфликтах, закоммитить слияние, затем перевод.
    try { git('merge', '-q', 'master'); } catch {
      git('checkout', '--theirs', 'src/x.tsx');
      const merged = read();
      merged[1] = 'const b = "p-2 rounded-lg";';
      writeFileSync(file(), `${merged.join('\n')}\n`);
      commit('слияние master');
    }
    const before = read();
    expect(before).toEqual(['const a = "rounded-sm";', 'const b = "p-2 rounded-lg";', 'const c = "rounded-md";']);
    migrate();
    expect(read()).toEqual(['const a = "rounded-sm";', 'const b = "p-2 rounded-md";', 'const c = "rounded-md";']);
    commit('перевод ветки');
    expect(migrate().flatMap(r => r.replaced)).toEqual([]);
  });
});
