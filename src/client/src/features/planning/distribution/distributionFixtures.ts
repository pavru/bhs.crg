import type { CarryOverItem, Distribution, DistributionLine } from '@/shared/api/planningDistribution';

/** Распределение для тестов экрана (plan-10, задача 2.3): два раздела стройки, раздел ЛСР «1» без правила. */

export function line(over: Partial<DistributionLine> = {}): DistributionLine {
  return {
    rowId: 'l1', order: 1, lsrSectionCode: '1', number: '1', name: 'Прокладка кабеля', kind: 'work', subordinate: false,
    source: 'none', status: 'undistributed', splittable: true, volume: { row: 12, plan: 1200 },
    units: { row: '100 м', plan: 'м' }, parts: [], rest: null, targets: [], whole: false, ...over,
  };
}

export function item(over: Partial<CarryOverItem> = {}): CarryOverItem {
  return {
    id: 'i1', category: 'ambiguous', subject: 'row', pending: true, lsrSectionCode: '1', lsrSectionName: 'Кабельные линии',
    rateCode: 'ГЭСН-X', numberInSection: '2', number: '2', name: 'Позиция X', sourceVolume: 4, sourceUnit: '100 м',
    parts: [{ sectionId: 's1', quantity: { row: 4, plan: 400 } }], target: null, rowId: null, newVolume: null, candidates: ['l1'],
    ...over,
  };
}

export function distribution(over: Partial<Distribution> = {}): Distribution {
  const lines = over.lines ?? [line(), line({ rowId: 'l2', order: 2, number: '2', name: 'Монтаж щита', splittable: false, splitReason: 'no-volume', volume: null })];
  return {
    revisionId: 'r1', available: true, version: '4', changedAt: null, changedBy: null, current: true, frozen: false,
    counts: { rows: lines.length, distributed: 0, partial: 0, whole: 0, undistributed: lines.length, carryOverPending: 0, changedAt: null },
    targets: {
      sections: [{ sectionId: 's1', name: 'Корпус 1', deleted: false, rows: 0 }, { sectionId: 's2', name: 'Корпус 2', deleted: false, rows: 0 }],
      wholeRows: 0, undistributedRows: lines.length,
    },
    rules: [{ lsrSectionCode: '1', lsrSectionName: 'Кабельные линии', target: null, rows: lines.length, exceptions: 0, undistributed: lines.length }],
    lines, carryOver: null, ...over,
  };
}
