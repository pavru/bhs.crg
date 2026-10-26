import type { EstimateRevision, EstimateRow, RevisionSection } from '@/shared/api/planningEstimates';

/** Образцы для тестов окон сопоставления (plan-09, PR 3): раздел 9 макета П1 и следующий за ним раздел 10. */

export const line = (over: Partial<EstimateRow> & { id: string }): EstimateRow => ({
  order: 1, lsrSection: { code: '9', name: 'Групповые сети. 2–9 этажи' }, numberInSection: null, number: null,
  subordinate: false, kind: 'work', rateCode: 'ГЭСНм08-03-591-02', name: 'Строка', sourceVolume: 1, sourceUnit: '100 шт',
  workTypeId: null, unitId: null, factor: null, volume: null, planItemId: null,
  marks: [{ code: 'unmatched', text: 'Не сопоставлена' }], materials: [], labor: [], ...over,
});

export const ROWS: EstimateRow[] = [
  line({ id: 'r1', number: '9.4', name: 'Розетка штепсельная утопленного типа', sourceVolume: 2.16 }),
  line({ id: 'r2', number: '9.5', name: 'Выключатель одноклавишный', sourceVolume: 1.44 }),
  line({ id: 'r3', number: '9.3', name: 'Труба гофрированная ПВХ', sourceVolume: 6.3, sourceUnit: '100 м' }),
  line({
    id: 'r4', number: '9.1', name: 'Кабель до 35 кВ', rateCode: 'ГЭСНм08-02-399-01', sourceVolume: 12.4, sourceUnit: '100 м',
    workTypeId: 'w1', unitId: 'u-m', factor: 100, volume: 1240,
    marks: [{ code: 'remembered', text: 'Подставлено из запомненного по шифру' }],
  }),
  line({ id: 'r5', number: '9.8', kind: 'material', name: 'Провод ПуГВ 1х2,5', marks: [] }),
  line({ id: 'r6', number: '10.1', lsrSection: { code: '10', name: 'Электроосвещение. Подвал' }, name: 'Светильник' }),
];

export const SECTIONS: RevisionSection[] = [
  { code: '9', name: 'Групповые сети. 2–9 этажи', rows: 5, unmatched: 3, materialsUnmatched: 0, units: [] },
  { code: '10', name: 'Электроосвещение. Подвал', rows: 1, unmatched: 1, materialsUnmatched: 0, units: [] },
];

export const REVISION = {
  id: 'rev3', number: 3, current: false, rows: 6, unmatched: 4, materialsUnmatched: 1, unitChanged: 0, frozen: null,
  version: '5', importedAt: '2026-10-20T10:00:00Z', importedBy: null, sourceFileName: null, schemaVersion: null,
  importId: null, partial: null,
} satisfies EstimateRevision;

export const WORK_TYPES = [
  { id: 'w1', displayName: 'Прокладка кабеля до 1 кВ', data: { Код: '01', ЕдиницаИзмерения: { entryId: 'u-m', displayName: 'м' } } },
  { id: 'w2', displayName: 'Монтаж электроустановочных изделий', data: { Код: '02', ЕдиницаИзмерения: { entryId: 'u-sht', displayName: 'шт' } } },
];
