import { describe, expect, it } from 'vitest';
import type { EstimateImport } from '@/shared/api/planningImports';
import { importVerdict, openingOf, partialCommitLabel, reasonGroups, targetLabel } from './importReport';

/** Правила отчёта импорта (plan-09, PR 2, ТЗ PLAN-7): что можно сделать с импортом — по состоянию сервера. */

function report(over: Partial<EstimateImport> = {}): EstimateImport {
  return {
    id: 'i1', constructionId: 'c1', fileName: 'ЛСР_02-01-03.gsfx', state: 'parsed', kind: 'lsr',
    kindName: 'Локальный сметный расчёт', programVersion: '2026.1', schemaVersion: '2026.1.1', progress: null,
    refusal: null, parsed: 300, inFile: 300, unparsed: [], skipped: [],
    summary: { lsrNumber: '02-01-03', lsrName: 'ЭОМ', method: 'rim', chapters: [], rows: 300, inactive: 0 },
    marks: [], revisionId: null, createdAt: '2026-10-26T10:00:00Z', createdBy: 'Смирнова А. В.', version: '5',
    target: { estimateId: 'e1', estimateName: 'ЭОМ корпус 2', newEstimate: null },
    ...over,
  };
}

const unparsed = (n: number, reason: string) =>
  Array.from({ length: n }, (_, i) => ({ place: `Раздел 1, поз. ${i + 1}`, line: 100 + i, reason }));

describe('что можно сделать с импортом', () => {
  it('полный разбор — создать редакцию без согласия', () => {
    expect(importVerdict(report())).toEqual({ kind: 'ready', unparsed: 0 });
  });

  it('неполный разбор — создать только явным согласием: число не разобранных названо', () => {
    expect(importVerdict(report({ parsed: 298, unparsed: unparsed(2, 'Нет единицы измерения.') })))
      .toEqual({ kind: 'ready', unparsed: 2 });
  });

  it('ВОР и КА — приложения, не редакция (решение В-7)', () => {
    const notEstimate = [{ code: 'notEstimate', text: 'Это ведомость объёмов работ — редакцией сметы она не становится.' }];
    expect(importVerdict(report({ kind: 'vor', marks: notEstimate, summary: null, parsed: null, inFile: null })).kind).toBe('attachment');
    expect(importVerdict(report({ kind: 'ka', summary: null })).kind).toBe('attachment');
  });

  it('отказ разбора и прерванный разбор — отказ с причиной сервера, а не пустой отчёт', () => {
    expect(importVerdict(report({ state: 'rejected', refusal: { code: 'UnsupportedVersion', text: 'Версия 2019.2 не поддерживается.' } })))
      .toEqual({ kind: 'refused', text: 'Версия 2019.2 не поддерживается.' });
    expect(importVerdict(report({ state: 'parsing', marks: [{ code: 'interrupted', text: 'Разбор прерван.' }] })))
      .toEqual({ kind: 'refused', text: 'Разбор прерван.' });
  });

  it('все позиции неактивны — создавать нечего', () => {
    expect(importVerdict(report({ summary: { lsrNumber: null, lsrName: null, method: 'rim', chapters: [], rows: 0, inactive: 4 } })).kind)
      .toBe('empty');
  });

  it('идёт разбор и уже создана — решать нечего', () => {
    expect(importVerdict(report({ state: 'parsing' })).kind).toBe('parsing');
    expect(importVerdict(report({ state: 'committed' })).kind).toBe('committed');
  });
});

describe('слова отчёта', () => {
  it('причины — группами, частые первыми', () => {
    expect(reasonGroups([...unparsed(2, 'Без расценки и ресурсов.'), ...unparsed(14, 'Нет единицы измерения.')]))
      .toEqual([{ reason: 'Нет единицы измерения.', count: 14 }, { reason: 'Без расценки и ресурсов.', count: 2 }]);
  });

  it('цель: новая редакция сметы или новая смета с видом и разделом', () => {
    expect(targetLabel(report().target)).toBe('новая редакция сметы «ЭОМ корпус 2»');
    expect(targetLabel({ estimateId: null, estimateName: null, newEstimate: { name: 'ЭОМ', kind: 'additional', sectionId: 's1' } },
      id => (id === 's1' ? 'Корпус 1' : undefined))).toBe('новая смета «ЭОМ» · дополнительная · раздел «Корпус 1»');
  });

  it('кнопка частичной редакции называет последствие', () => {
    expect(partialCommitLabel(1)).toBe('Создать редакцию без 1 строки…');
    expect(partialCommitLabel(20)).toBe('Создать редакцию без 20 строк…');
  });

  it('созданная с несопоставленным открывается на отборе «не сопоставлено»', () => {
    expect(openingOf({ estimateId: 'e1', revisionId: 'r4', unmatched: true })).toEqual({ revisionId: 'r4', queue: 'unmatched' });
    expect(openingOf({ estimateId: 'e1', revisionId: 'r4', unmatched: false })).toEqual({ revisionId: 'r4', queue: 'all' });
  });
});
