import { describe, expect, it } from 'vitest';
import type { Assignment } from '@/shared/api/workAssignments';
import { barLabel, filterAssignments, periodLabel, phaseLabel, phaseOf } from './assignmentRows';

const row = (over: Partial<Assignment>): Assignment => ({
  id: 'a', employeeId: 'e', employeeName: 'Белов А. С.', personnelNumber: '0102', constructionId: 's', kind: 'installer',
  dateFrom: '2026-11-01', dateTo: '2026-11-30', active: false, version: '1',
  canChange: true, changeReason: null, canRemove: true, removeReason: null, ...over,
});

describe('строки назначений', () => {
  it('«действует» — поле сервера; «будет» и «закончилось» — по сегодня стройки', () => {
    expect(phaseOf(row({ active: true }), '2026-11-20')).toBe('active');
    expect(phaseOf(row({ dateFrom: '2026-12-01', dateTo: '2026-12-31' }), '2026-11-20')).toBe('future');
    expect(phaseOf(row({ dateTo: '2026-11-10' }), '2026-11-20')).toBe('past');
    expect(phaseOf(row({}), null)).toBe('inactive');
  });

  it('период менеджера без конца — «без срока»', () => {
    expect(periodLabel(row({ kind: 'manager', dateFrom: '2026-09-01', dateTo: null }))).toBe('с 01.09.2026, без срока');
    expect(periodLabel(row({}))).toBe('01.11.2026 — 30.11.2026');
    expect(barLabel(row({}))).toBe('01.11 — 30.11');
  });

  it('подпись состояния будущего называет начало', () => {
    expect(phaseLabel(row({ dateFrom: '2026-12-01', dateTo: '2026-12-31' }), '2026-11-20')).toBe('с 01.12.2026');
  });

  it('отбор «действующие и будущие» скрывает закончившиеся и считает их; без сегодня — не скрывает ничего', () => {
    const items = [row({ id: '1', active: true }), row({ id: '2', dateTo: '2026-11-10' }), row({ id: '3', dateFrom: '2026-12-01', dateTo: null, kind: 'manager' })];
    expect(filterAssignments(items, 'current', '2026-11-20')).toEqual({ shown: [items[0], items[2]], hidden: 1 });
    expect(filterAssignments(items, '', '2026-11-20')).toEqual({ shown: items, hidden: 0 });
    expect(filterAssignments(items, 'current', null)).toEqual({ shown: items, hidden: 0 });
  });
});
