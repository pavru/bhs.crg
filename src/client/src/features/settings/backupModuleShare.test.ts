import { describe, expect, it } from 'vitest';
import { backupModuleShare } from './backupModuleShare';

/** Строка веса копии называет долю файлов модулей (work-05, WORK-35). */
describe('backupModuleShare', () => {
  it('называет модуль с файлами — его вес и число файлов', () => {
    expect(backupModuleShare([
      { code: 'costs', title: 'Счета и накладные', bytes: 3 * 1024 * 1024, count: 14 },
      { code: 'plan', title: 'Планирование', bytes: 0, count: 0 },
    ])).toBe('из них файлы модулей: 3 МБ (Счета и накладные: 3 МБ, файлов 14)');
  });

  it('несколько модулей — общий вес и каждый по отдельности', () => {
    expect(backupModuleShare([
      { code: 'costs', title: 'Счета и накладные', bytes: 1024 * 1024, count: 2 },
      { code: 'work', title: 'Учёт работ', bytes: 512 * 1024, count: 3 },
    ])).toBe('из них файлы модулей: 1,5 МБ (Счета и накладные: 1 МБ, файлов 2; Учёт работ: 512 КБ, файлов 3)');
  });

  it('модули без файлов строки не дают', () => {
    expect(backupModuleShare([{ code: 'costs', title: 'Счета и накладные', bytes: 0, count: 0 }])).toBeNull();
    expect(backupModuleShare([])).toBeNull();
  });
});
