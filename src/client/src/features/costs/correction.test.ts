import { describe, expect, it } from 'vitest';
import type { AllocationPartView, InvoiceLineView, InvoiceView } from '@/shared/api/invoices';
import type { CorrectionPreview } from '@/shared/api/invoiceCorrection';
import {
  conflictKind, correctionBody, correctionRows, mayCorrect, readyToCheck, signed, type CorrectionContent,
} from './correction';
import { emptyDraft, toDrafts } from './invoiceLines';
import { draftRows, targetsOfState } from './matrix';

function part(construction: string, ordinal: number, overrides: Partial<AllocationPartView> = {}): AllocationPartView {
  return {
    id: `${construction}-${ordinal}`, ordinal, constructionId: construction, constructionName: construction,
    sectionId: null, sectionName: null, articleId: null, articleName: null, targetLost: false,
    quantity: null, amount: 100, rounding: 0, discrepancy: 0, mismatched: false, ...overrides,
  };
}

function line(id: string, mode: 'quantity' | 'amount', parts: AllocationPartView[]): InvoiceLineView {
  return {
    id, ordinal: 1, nomenclatureId: null, nomenclatureName: null, nomenclatureLost: false, supplierText: 'Услуга',
    supplierCode: null, unit: null, quantity: mode === 'quantity' ? 10 : null, price: null, vatRate: null,
    vatAmount: null, amount: 1000, note: null,
    allocation: { mode, parts, unallocatedQuantity: null, unallocatedAmount: 0, balanced: true },
  } as InvoiceLineView;
}

function invoice(lines: InvoiceLineView[], document: AllocationPartView[] = [], lockedBy: string | null = 'период закрыт'): InvoiceView {
  return {
    id: 'i1', version: '7', lines, requisites: { 'Номер': 'СЧ-1', 'Итого': 1000 },
    payment: { lockedBy },
    allocation: { document: { parts: document } },
  } as unknown as InvoiceView;
}

const nothing: CorrectionContent = {
  reason: 'опечатка в номере', edits: {}, lines: null, vatMode: null, allocation: null,
};

describe('тело исправления', () => {
  it('нетронутое уезжает таким, каким лежит: строки с идентификаторами и частями в записанном порядке', () => {
    // Части нарочно перечислены не по порядку: сервер сравнивает разноску по порядку частей, и
    // переставленные прочлись бы «разноска меняется» — отказом тому, у кого нет права разноски.
    const view = invoice([line('l1', 'amount', [part('B', 2, { amount: 400.5, discrepancy: 0.5 }), part('A', 1, { amount: 600 })])]);
    const body = correctionBody(view, { ...nothing, edits: { 'Номер': 'СЧ-2' } });

    expect(body.reason).toBe('опечатка в номере');
    expect(body.requisites).toEqual({ 'Номер': 'СЧ-2', 'Итого': 1000 });
    expect(body.lines).toHaveLength(1);
    expect(body.lines[0]).toMatchObject({ id: 'l1', amount: '1000' });
    // Сумма части — без расхождения со счётом: его добавляет сервер при чтении, хранится часть без него.
    expect(body.lines[0].parts).toEqual([
      { construction: 'A', section: null, article: null, quantity: null, amount: 600 },
      { construction: 'B', section: null, article: null, quantity: null, amount: 400 },
    ]);
    expect(body.document).toEqual([]);
    // Режим едет всегда (issue #1498): тело — содержимое целиком, и нетронутый режим — режим счёта.
    expect(body.vatMode).toBe('Included');
  });

  it('у строки с количеством уезжает количество, а не сумма', () => {
    const view = invoice([line('l1', 'quantity', [part('A', 1, { quantity: 4, amount: 400 })])]);
    expect(correctionBody(view, nothing).lines[0].parts).toEqual([
      { construction: 'A', section: null, article: null, quantity: 4, amount: null },
    ]);
  });

  it('разноска из матрицы — по ключу черновика: и записанной строки, и новой', () => {
    const view = invoice([line('l1', 'amount', [part('A', 1)])]);
    const added = { ...emptyDraft(), supplierText: 'Доставка', amount: '50' };
    const drafts = [...toDrafts(view.lines, 'Included'), added];
    const body = correctionBody(view, {
      ...nothing, lines: drafts,
      allocation: {
        lines: [
          { line: 'l1', parts: [{ construction: 'B', section: null, article: null, quantity: null, amount: 1000 }] },
          { line: added.key, parts: [{ construction: 'B', section: null, article: null, quantity: null, amount: 50 }] },
        ],
        document: [],
      },
    });

    expect(body.lines.map(l => l.id)).toEqual(['l1', null]);
    expect(body.lines.map(l => (l.parts as { amount: number }[])[0].amount)).toEqual([1000, 50]);
  });

  it('новая строка без разноски из матрицы уходит неразнесённой, а не с чужими частями', () => {
    const view = invoice([line('l1', 'amount', [part('A', 1)])]);
    const body = correctionBody(view, { ...nothing, lines: [...toDrafts(view.lines, 'Included'), emptyDraft()] });
    expect(body.lines[1].parts).toEqual([]);
  });

  it('счёт без строк — части счёта целиком; со строками — пустой набор', () => {
    const whole = invoice([], [part('A', 1, { amount: 1000 })]);
    expect(correctionBody(whole, nothing).document).toEqual([
      { construction: 'A', section: null, article: null, quantity: null, amount: 1000 },
    ]);
    expect(correctionBody(whole, nothing).lines).toEqual([]);
  });
});

describe('проверить можно', () => {
  it('только с причиной и с правкой', () => {
    expect(readyToCheck({ ...nothing, edits: { 'Номер': 'СЧ-2' } })).toBe(true);
    expect(readyToCheck({ ...nothing, reason: '   ', edits: { 'Номер': 'СЧ-2' } })).toBe(false);
    expect(readyToCheck(nothing)).toBe(false);
    expect(readyToCheck({ ...nothing, allocation: { lines: [], document: [] } })).toBe(true);
  });
});

describe('отказ 409 — по свежему виду', () => {
  it('счёт больше не заперт — исправление не нужно', () => {
    expect(conflictKind(invoice([], [], null), '7')).toBe('unneeded');
  });

  it('версия сменилась — черновик устарел', () => {
    expect(conflictKind(invoice([]), '6')).toBe('stale');
  });

  it('версия та же — устарел предпросмотр', () => {
    expect(conflictKind(invoice([]), '7')).toBe('stamp');
  });
});

describe('строки предпросмотра', () => {
  const line = (day: string, gross: number, shifted?: boolean) => ({
    constructionId: 'A', sectionId: null, articleId: null, day, gross, net: gross, vatUnknown: false, shifted,
  });
  const preview = (lines: ReturnType<typeof line>[]): CorrectionPreview => ({
    version: 2, day: '2026-10-16', stamp: 's',
    records: [
      { kind: 'opening', day: '2026-10-16', tolerance: 1, lines: [line('2026-09-10', 1000)] },
      { kind: 'correction', day: '2026-10-16', tolerance: 1, lines },
    ],
  });

  it('две группы — днём исправления и своим днём; пустая не рисуется', () => {
    const rows = correctionRows(preview([line('2026-10-16', -300), line('2026-10-02', 300)]), iso => iso);
    expect(rows.map(r => (r.kind === 'group' ? r.title : r.line.gross))).toEqual([
      'Днём исправления 2026-10-16', -300, 'Своим днём (период открыт)', 300,
    ]);
    expect(correctionRows(preview([line('2026-10-16', -300)]), iso => iso).filter(r => r.kind === 'group')).toHaveLength(1);
  });

  it('группа — по признаку переноса: своя строка дня исправления — своим днём, а не рядом с перенесённой', () => {
    const rows = correctionRows(preview([line('2026-10-16', -30, true), line('2026-10-16', 30, false)]), iso => iso);
    expect(rows.map(r => (r.kind === 'group' ? r.title : r.line.gross))).toEqual([
      'Днём исправления 2026-10-16', -30, 'Своим днём (период открыт)', 30,
    ]);
  });

  it('без денег — строк нет: меняются только версия и причина', () => {
    expect(correctionRows(preview([]), iso => iso)).toEqual([]);
  });

  it('знак у прибавки явный', () => {
    expect(signed(300, String)).toBe('+300');
    expect(signed(-300, String)).toBe('-300');
  });
});

describe('матрица исправления', () => {
  it('строки — по черновику: новая строка тоже, вид разноски — правилом сервера', () => {
    const drafts = [
      { ...emptyDraft(), supplierText: 'Кабель', quantity: '10', price: '5', unit: 'м' },
      { ...emptyDraft(), supplierText: 'Доставка', amount: '50' },
      { ...emptyDraft() },
    ];
    expect(draftRows(drafts, null, 'Included').map(r => [r.lineId, r.mode, r.whole])).toEqual([
      [drafts[0].key, 'quantity', 10], [drafts[1].key, 'amount', 50], [drafts[2].key, 'none', null],
    ]);
  });

  it('набор черновика открывает матрицу своими объектами', () => {
    const targets = targetsOfState({
      lines: [{ line: 'l1', parts: [
        { construction: 'B', section: null, article: null, quantity: null, amount: 1 },
        { construction: 'A', section: null, article: null, quantity: null, amount: 1 },
      ] }],
      document: [{ construction: 'B', section: null, article: null, quantity: null, amount: 1 }],
    });
    expect(targets.map(t => t.construction)).toEqual(['B', 'A']);
  });
});

describe('кнопка «Исправить»', () => {
  // Наборы прав системных ролей (SystemRoles): руководитель читает всё без правки, бухгалтер исправляет.
  const reader = new Set(['costs.invoice.read', 'costs.report.read']);
  const accountant = new Set(['costs.invoice.read', 'costs.invoice.pay', 'costs.invoice.correct', 'costs.invoice.edit',
    'costs.allocation.edit']);
  const has = (set: Set<string>) => (code: string) => set.has(code);

  it('бухгалтеру у запертого счёта — есть', () => {
    expect(mayCorrect(invoice([]), has(accountant), false)).toBe(true);
  });

  it('руководителю — нет: правки счетов у него нет', () => {
    expect(mayCorrect(invoice([]), has(reader), false)).toBe(false);
  });

  it('с правкой, но без права исправления — нет', () => {
    expect(mayCorrect(invoice([]), has(new Set(['costs.invoice.edit'])), false)).toBe(false);
  });

  it('с правом исправления, но без правки счетов — нет: исправление пишет счёт, и адрес откажет 403', () => {
    expect(mayCorrect(invoice([]), has(new Set(['costs.invoice.read', 'costs.invoice.correct'])), false)).toBe(false);
  });

  it('у незапертого и у удалённого счёта — нет', () => {
    expect(mayCorrect(invoice([], [], null), has(accountant), false)).toBe(false);
    expect(mayCorrect(invoice([]), has(accountant), true)).toBe(false);
  });
});
