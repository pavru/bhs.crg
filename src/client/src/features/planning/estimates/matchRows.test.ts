import { describe, expect, it } from 'vitest';
import { memoryText, nextWaitingSection, parseFactor, planVolume, sectionRows, unitWord, waiting, waitingUnits } from './matchRows';
import { ROWS, SECTIONS } from './matchFixtures';

/** Правила окна сопоставления раздела (plan-09, задача 4.1, макет П1 1г). */
describe('окно сопоставления: что ждёт и в каких единицах', () => {
  it('ждут только строки-работы с пометкой «не сопоставлена»; материал и запомненная — нет', () => {
    expect(ROWS.filter(waiting).map(r => r.id)).toEqual(['r1', 'r2', 'r3', 'r6']);
  });

  it('единицы раздела — ждущих строк, больше строк — первой', () => {
    expect(waitingUnits(sectionRows(ROWS, '9'))).toEqual([{ unit: '100 шт', rows: 2 }, { unit: '100 м', rows: 1 }]);
  });

  it('следующий раздел — тот, где ещё ждут; после последнего — нет', () => {
    expect(nextWaitingSection(SECTIONS, ROWS, '9')?.code).toBe('10');
    expect(nextWaitingSection(SECTIONS, ROWS, '10')).toBeNull();
  });

  it('коэффициент: запятая, пробелы разрядов; ноль и не число — нет коэффициента', () => {
    expect(parseFactor('100')).toBe(100);
    expect(parseFactor('2,5')).toBe(2.5);
    expect(parseFactor('1 000')).toBe(1000);
    expect(parseFactor('0')).toBeNull();
    expect(parseFactor('сто')).toBeNull();
  });

  it('объём в плане — объём расценки × коэффициент', () => {
    expect(planVolume(ROWS[0], 100)).toBeCloseTo(216);
  });

  it('единица классификатора из единицы расценки — без множителя', () => {
    expect(unitWord('100 шт')).toBe('шт');
    expect(unitWord('1000 м3')).toBe('м3');
    expect(unitWord(null)).toBe('');
  });

  it('запомненное словами: «не удалось» — отдельно от «нечего»', () => {
    expect(memoryText({ remembered: 0, replaced: 0, failed: false })).toBe('');
    expect(memoryText({ remembered: 2, replaced: 1, failed: false })).toBe(' · запомнено, заменено прежнее: 1');
    expect(memoryText({ remembered: 0, replaced: 0, failed: true })).toMatch(/запомнить не удалось/);
  });
});
