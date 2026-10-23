import { describe, expect, it } from 'vitest';
import type { SchemaField } from './schema';
import cases from './suggestableCases.json';
import { SUGGEST_REASONS, suggestReason } from './suggestable';

/** Правило «поле подсказывается» (issue #1529). */

describe('общий набор случаев с сервером', () => {
  // Тот же файл считает SuggestableCasesTests на сервере: разойдутся правила — краснеет тот, кто отстал.
  it.each(cases.cases.map(c => [c.name, c] as const))('%s', (_, c) => {
    expect(suggestReason({ ...c.field, type: c.field.type as SchemaField['type'] })).toBe(c.expect);
  });

  it('каждый код причины есть в наборе', () => {
    // Код без случая расходился бы с сервером незамеченным: ни один случай не зашёл бы в его ветку.
    const expected = new Set(cases.cases.map(c => c.expect));
    expect(SUGGEST_REASONS.filter(code => !expected.has(code))).toEqual([]);
  });
});

describe('поле без необязательных свойств', () => {
  it('строка без тэгов и флагов подсказывается', () => {
    expect(suggestReason({ type: 'string' })).toBe('suggested');
  });
});
