import { describe, expect, it } from 'vitest';
import type { SchemaField } from './schema';
import cases from './suggestableCases.json';
import { suggestReason } from './suggestable';

/** Правило «поле подсказывается» (issue #1529). */

describe('общий набор случаев с сервером', () => {
  // Тот же файл считает SuggestableCasesTests на сервере: разойдутся правила — краснеет тот, кто отстал.
  it.each(cases.cases.map(c => [c.name, c] as const))('%s', (_, c) => {
    expect(suggestReason({ ...c.field, type: c.field.type as SchemaField['type'] })).toBe(c.expect);
  });
});

describe('поле без необязательных свойств', () => {
  it('строка без тэгов и флагов подсказывается', () => {
    expect(suggestReason({ type: 'string' })).toBe('suggested');
  });
});
