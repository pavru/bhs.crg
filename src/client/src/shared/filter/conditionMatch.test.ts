import { describe, expect, it } from 'vitest';
import type { FilterOp } from '@/shared/api/types';
import { listMatches, softKey, softTwin } from './conditionMatch';
import cases from './conditionMatchCases.json';

/**
 * Правило сверки названий (UI-56): общий набор с сервером (`ConditionMatchCasesTests`) и мягкое
 * совпадение для замены.
 */
describe('listMatches — правило сервера, общий набор случаев', () => {
  it.each(cases.cases.map(c => [c.name, c] as const))('%s', (_, c) => {
    expect(listMatches(c.op as FilterOp, c.values, c.items)).toBe(c.expect);
  });
});

describe('softTwin — мягкое совпадение для замены', () => {
  it('ё — как е', () => {
    expect(softTwin('Березовая 4', ['Берёзовая 4'])).toBe('Берёзовая 4');
  });

  it('края, двойные пробелы и пробелы вокруг «/»', () => {
    expect(softTwin(' Комарова  36 ', ['Комарова 36'])).toBe('Комарова 36');
    expect(softTwin('Комарова 36/4 эт.', ['Комарова 36 / 4 эт.'])).toBe('Комарова 36 / 4 эт.');
    expect(softKey('Комарова 36 /4 эт.')).toBe(softKey('Комарова 36/ 4 эт.'));
  });

  it('два мягких совпадения — замены нет', () => {
    expect(softTwin('Березовая  4', ['Берёзовая 4', 'Березовая 4'])).toBeUndefined();
  });

  it('нечёткого поиска нет: без пробела — другое название', () => {
    expect(softTwin('Комарова36', ['Комарова 36'])).toBeUndefined();
  });
});
