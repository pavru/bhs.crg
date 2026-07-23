import { describe, it, expect } from 'vitest';
import { evalComputed, referencedKeys, validateComputed, findComputedCycles } from './computedExpression';

describe('computedExpression', () => {
  it('referencedKeys extracts distinct get() targets', () => {
    expect(referencedKeys('get("a") + get(\'b\') + get("a")')).toEqual(['a', 'b']);
    expect(referencedKeys('1 + 2')).toEqual([]);
  });

  it('evalComputed computes arithmetic via get()', () => {
    expect(evalComputed('get("Кол") * get("Цена")', { Кол: 4, Цена: 25 }).value).toBe(100);
    expect(evalComputed('get("a") + "шт"', { a: 2 }).value).toBe('2шт');
    expect(evalComputed('', {}).value).toBeUndefined();
  });

  it('evalComputed returns error on bad expression', () => {
    const r = evalComputed('get("a" +', { a: 1 });
    expect(r.error).toBeTruthy();
    expect(r.value).toBeUndefined();
  });

  it('validateComputed flags syntax errors and unknown refs', () => {
    const known = new Set(['Кол', 'Цена']);
    expect(validateComputed('get("Кол") * get("Цена")', known)).toEqual({ syntaxError: undefined, unknownRefs: [] });
    expect(validateComputed('get("Кол") * get("НетТакого")', known).unknownRefs).toEqual(['НетТакого']);
    expect(validateComputed('get("Кол" *', known).syntaxError).toBeTruthy();
  });

  it('findComputedCycles detects dependency cycles and self-reference', () => {
    // sum → withTax (нет цикла)
    expect([...findComputedCycles({
      sum: 'get("x") + get("y")',
      withTax: 'get("sum") * 1.2',
    })]).toEqual([]);
    // a ↔ b (цикл)
    expect(findComputedCycles({ a: 'get("b")', b: 'get("a")' })).toEqual(new Set(['a', 'b']));
    // самоссылка
    expect(findComputedCycles({ c: 'get("c") + 1' })).toEqual(new Set(['c']));
  });
});
