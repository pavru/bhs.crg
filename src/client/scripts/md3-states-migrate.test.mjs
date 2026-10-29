import { describe, expect, it } from 'vitest';
import { migrateText } from './md3-migrate.mjs';
import { compileStates, migrateStates } from './md3-states-migrate.mjs';
import { countDarkVariants, countRawStates } from './ui-ratchet-md3.mjs';

// Проход состояний кодмода md3:migrate (sharedui-md3-05, [UI-64]): почему класс целиком, с вариантом, —
// в md3-states-migrate.mjs.
const migrated = (text, file = 'src/x.tsx') => migrateText(text, { file }).text;

describe('md3:migrate: состояния → слой MD3', () => {
  it('наведение заливкой и пара тёмной темы — один слой, переход цвета снят', () => {
    expect(migrated('"p-1.5 rounded-full hover:bg-black/5 dark:hover:bg-white/10 transition-colors"'))
      .toBe('"p-1.5 rounded-full state-layer"');
    expect(migrated('"px-3 hover:bg-surface-container-low"')).toBe('"px-3 state-layer"');
    expect(migrated('"bg-secondary-container hover:brightness-95 transition"')).toBe('"bg-secondary-container state-layer"');
    expect(migrated('"outline-none data-[highlighted]:bg-surface-container-low"')).toBe('"outline-none state-layer"');
  });

  it('слой уже есть — второй не добавляется', () => {
    expect(migrated('"state-layer hover:bg-surface-container"')).toBe('"state-layer"');
  });

  it('условная ветка шаблона: слой в ветке, переход снят и в постоянной части', () => {
    expect(migrated("`px-2 transition-colors ${active ? 'bg-primary-container' : 'hover:bg-surface-container'}`"))
      .toBe("`px-2 ${active ? 'bg-primary-container' : 'state-layer'}`");
  });

  it('наведение заливкой выбранного контейнера — не состояние: не трогается', () => {
    const source = '"hover:bg-primary-container hover:bg-error-container"';
    expect(migrated(source)).toBe(source);
  });

  it('отключённое: заливка — disabled-container, рамка — disabled-outlined, иначе disabled-content', () => {
    expect(migrated('"bg-primary text-on-primary disabled:opacity-40"')).toBe('"bg-primary text-on-primary disabled-container"');
    expect(migrated('"border border-outline-variant disabled:opacity-50 disabled:hover:bg-transparent"'))
      .toBe('"border border-outline-variant disabled-outlined"');
    expect(migrated('"text-primary disabled:opacity-50"')).toBe('"text-primary disabled-content"');
    expect(migrated('"outline-none data-[disabled]:opacity-40 data-[disabled]:pointer-events-none"'))
      .toBe('"outline-none disabled-content data-[disabled]:pointer-events-none"');
  });

  it('заливка под наведением — не заливка элемента: отключённое без контейнера', () => {
    expect(migrated('"text-primary hover:bg-primary-container disabled:opacity-50"'))
      .toBe('"text-primary hover:bg-primary-container disabled-content"');
  });

  it('затемнение — роль scrim; подложка — поверхность; снятое не оставляет двойных пробелов', () => {
    expect(migrated('"fixed inset-0 bg-black/40"')).toBe('"fixed inset-0 bg-scrim/32"');
    expect(migrated('"rounded-lg bg-black/5 dark:bg-white/5 p-3"')).toBe('"rounded-lg bg-surface-container p-3"');
    expect(migrated('"text-primary hover:text-brand-hover"')).toBe('"text-primary"');
  });

  it('после перевода храповик не видит ни raw-state, ни dark-variant; второй прогон ничего не меняет', () => {
    const source = [
      '<button className="rounded-full hover:bg-black/5 dark:hover:bg-white/10 disabled:opacity-40" />',
      "const row = `px-3 ${on ? 'bg-primary-container' : 'hover:bg-surface-container'}`;",
      '<pre className="bg-black/5 dark:bg-white/5" />',
    ].join('\n');
    expect(countRawStates(source)).toBe(5);
    expect(countDarkVariants(source)).toBe(2);
    const once = migrated(source);
    expect(countRawStates(once)).toBe(0);
    expect(countDarkVariants(once)).toBe(0);
    expect(migrateText(once, { file: 'src/x.tsx' }).replaced).toEqual([]);
  });

  it('отрицательное ожидание теста — не трогается, называется', () => {
    const source = "expect(cls).not.toContain('hover:bg-black/5');";
    const result = migrateText(source, { file: 'src/x.test.tsx' });
    expect(result.text).toBe(source);
    expect(result.untouched.map(u => u.token)).toEqual(['hover:bg-black/5']);
  });

  it('комментарий — не строка: не трогается', () => {
    const source = '// было hover:bg-black/5 dark:hover:bg-white/10\nconst a = 1;';
    expect(migrateStates(source, { rules: compileStates() }).text).toBe(source);
  });
});
