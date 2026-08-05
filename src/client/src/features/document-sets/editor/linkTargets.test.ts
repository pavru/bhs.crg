import { describe, it, expect } from 'vitest';
import { groupByTargetScope, needsFallbackScope, widestTargetScope, type LinkScope } from './linkTargets';

interface Row { key: string }
const row = (key: string): Row => ({ key });

const SET: LinkScope = { scope: 'Set', scopeId: 'set-1' };
const SECTION: LinkScope = { scope: 'Section', scopeId: 'sec-1' };
const SYSTEM: LinkScope = { scope: 'System', scopeId: null };

/** Существующие связки: ключ материала → область, в которой связка живёт. */
function existing(map: Record<string, LinkScope>) {
  return (r: Row) => map[r.key];
}

describe('область записи связки', () => {
  /**
   * Тот самый дефект #681: связка пришла с уровня «Система», в селекторе стоит «Только этот
   * комплект» — и перепривязка обязана переписать системную связку, а не завести вторую рядом.
   */
  it('перепривязка идёт в область действующей связки, а не в область селектора', () => {
    const rows = [row('m1')];
    const groups = groupByTargetScope(rows, existing({ m1: SYSTEM }), SET);

    expect(groups).toEqual([{ scope: 'System', scopeId: null, materials: rows }]);
  });

  it('строка без связки идёт в область селектора', () => {
    const rows = [row('m1')];
    expect(groupByTargetScope(rows, existing({}), SET))
      .toEqual([{ scope: 'Set', scopeId: 'set-1', materials: rows }]);
  });

  it('разнородный выбор разбивается на группы — по одной на область', () => {
    const rows = [row('m1'), row('m2'), row('m3'), row('m4')];
    const groups = groupByTargetScope(
      rows, existing({ m1: SYSTEM, m2: SECTION, m4: SYSTEM }), SET);

    expect(groups).toEqual([
      { scope: 'System', scopeId: null, materials: [rows[0], rows[3]] },
      { scope: 'Section', scopeId: 'sec-1', materials: [rows[1]] },
      { scope: 'Set', scopeId: 'set-1', materials: [rows[2]] },
    ]);
  });

  /** Область — это ПАРА (уровень, объект): два раздела не одна группа, иначе связки уедут в чужой. */
  it('один уровень с разными объектами — разные группы', () => {
    const rows = [row('m1'), row('m2')];
    const groups = groupByTargetScope(rows, existing({
      m1: { scope: 'Section', scopeId: 'sec-1' },
      m2: { scope: 'Section', scopeId: 'sec-2' },
    }), SET);

    expect(groups.map(g => g.scopeId)).toEqual(['sec-1', 'sec-2']);
  });

  it('связка в той же области, что и селектор, лишней группы не создаёт', () => {
    const rows = [row('m1'), row('m2')];
    const groups = groupByTargetScope(rows, existing({ m1: { scope: 'Set', scopeId: 'set-1' } }), SET);

    expect(groups).toHaveLength(1);
    expect(groups[0].materials).toEqual(rows);
  });

  it('пустой список не даёт групп', () => {
    expect(groupByTargetScope([], existing({}), SET)).toEqual([]);
  });
});

describe('область для нового документа', () => {
  /**
   * Новый документ заводится в области, которую видит пикер. Если связка пишется на «Систему», а
   * документ создать в комплекте, то в других комплектах связка найдётся, а документ — нет: в
   * строке останется «(документ)» вместо имени, и открыть его будет нечем.
   */
  it('берётся самая широкая из областей записи, а не из селектора', () => {
    const rows = [row('m1'), row('m2')];
    expect(widestTargetScope(rows, existing({ m1: SYSTEM }), SET)).toEqual(SYSTEM);
  });

  it('все строки в своих узких областях — область селектора не расширяется', () => {
    const rows = [row('m1')];
    expect(widestTargetScope(rows, existing({ m1: { scope: 'Set', scopeId: 'set-1' } }), SET)).toEqual(SET);
  });

  /** Селектор шире связок — он и остаётся: сузить область нового документа было бы ошибкой. */
  it('связки уже селектора область не сужают', () => {
    const rows = [row('m1')];
    expect(widestTargetScope(rows, existing({ m1: SET }), SYSTEM)).toEqual(SYSTEM);
  });

  it('пустой список — область селектора', () => {
    expect(widestTargetScope([], existing({}), SET)).toEqual(SET);
  });
});

describe('нужна ли область из селектора', () => {
  it('нужна, пока в выборе есть хоть одна строка без связки', () => {
    expect(needsFallbackScope([row('m1'), row('m2')], existing({ m1: SYSTEM }))).toBe(true);
  });

  /**
   * Все строки идут в области своих связок — селектор не участвует. Требовать его готовности
   * значило бы блокировать перепривязку, пока догружается комплект, к которому она не относится.
   */
  it('не нужна, когда все строки перепривязываются в своих областях', () => {
    expect(needsFallbackScope([row('m1'), row('m2')], existing({ m1: SYSTEM, m2: SECTION }))).toBe(false);
  });

  it('пустой список области не требует', () => {
    expect(needsFallbackScope([], existing({}))).toBe(false);
  });
});
