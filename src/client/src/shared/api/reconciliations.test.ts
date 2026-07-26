import { describe, it, expect } from 'vitest';
import { needsAttention, runSummary, type Finding, type ReconciliationRun } from './reconciliations';

function finding(p: Partial<Finding>): Finding {
  return {
    id: 'f', key: 'k', label: 'ВВГнг(А)-LS 3х2.5',
    leftValue: 50, rightValue: 100, status: 'Mismatch',
    provenance: { left: null, right: null },
    resolved: false, decision: null,
    ...p,
  };
}

function run(p: Partial<ReconciliationRun>): ReconciliationRun {
  return {
    id: 'r', definitionId: 'd', status: 'Completed',
    startedAt: '2026-07-26T00:00:00Z', finishedAt: '2026-07-26T00:00:01Z', error: null,
    matchCount: 0, mismatchCount: 0, missingLeftCount: 0, missingRightCount: 0,
    ...p,
  };
}

describe('needsAttention', () => {
  it('расхождение без решения требует внимания', () => {
    expect(needsAttention(finding({ status: 'Mismatch' }))).toBe(true);
    expect(needsAttention(finding({ status: 'MissingLeft' }))).toBe(true);
  });

  it('совпадение не требует', () => {
    expect(needsAttention(finding({ status: 'Match' }))).toBe(false);
  });

  // Смысл персистентного решения: «давальческое оборудование» не должно всплывать каждый прогон.
  it('принятое решение снимает вопрос, даже если расхождение осталось', () => {
    const decided = finding({
      status: 'Mismatch',
      decision: { id: 'x', key: 'k', kind: 'Accepted', note: null, decidedBy: 'alex', updatedAt: '' },
    });
    expect(needsAttention(decided)).toBe(false);
  });
});

describe('runSummary', () => {
  it('неудачный прогон не выдаёт себя за «расхождений нет»', () => {
    expect(runSummary(run({ status: 'Failed', error: 'Источник не найден' })))
      .toBe('Прогон не выполнен');
  });

  it('чистый прогон', () => {
    expect(runSummary(run({ matchCount: 10 }))).toBe('Расхождений нет (позиций: 10)');
  });

  // Отсутствующие позиции — такая же проблема, как расхождение в количестве, и в сводку входят.
  it('считает все виды проблем, а не только расхождения', () => {
    expect(runSummary(run({ matchCount: 8, mismatchCount: 1, missingLeftCount: 1, missingRightCount: 2 })))
      .toBe('Требует внимания: 4 из 12');
  });
});
