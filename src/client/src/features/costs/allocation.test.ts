import { describe, expect, it } from 'vitest';
import type { AllocationPartView, LineAllocationView } from '@/shared/api/invoices';
import { allocationStatus, estimateRemainder, toPartDrafts, toPartsPayload, type PartDraft } from './allocation';

function part(overrides: Partial<AllocationPartView> = {}): AllocationPartView {
  return {
    id: 'p1', ordinal: 1, constructionId: 's1', constructionName: 'Стройка', sectionId: null, sectionName: null,
    targetLost: false, quantity: 100, amount: 4850, rounding: 0, discrepancy: 0, mismatched: false,
    ...overrides,
  };
}

function allocation(overrides: Partial<LineAllocationView> = {}): LineAllocationView {
  return {
    mode: 'quantity', parts: [part()], unallocatedQuantity: 200, unallocatedAmount: 9700, balanced: false,
    ...overrides,
  };
}

function draft(value: string, id: string | null = null): PartDraft {
  return { key: id ?? value, id, constructionId: 's1', sectionId: '', value };
}

describe('набор частей для сервера', () => {
  it('у строки с количеством уезжает количество и никогда сумма', () => {
    const [payload] = toPartsPayload([draft('7,25', 'p1')], 'quantity');
    expect(payload).toEqual({ id: 'p1', construction: 's1', section: null, quantity: 7.25 });
  });

  it('у строки без количества уезжает сумма', () => {
    const [payload] = toPartsPayload([draft('1 000,50')], 'amount');
    expect(payload).toEqual({ id: null, construction: 's1', section: null, amount: 1000.5 });
  });

  it('невыбранная стройка уезжает пустой — отказ сервера назовёт часть, а не потеряет её', () => {
    const [payload] = toPartsPayload([{ ...draft('1'), constructionId: '' }], 'quantity');
    expect(payload.construction).toBeNull();
  });

  it('черновик из ответа берёт количество или сумму по виду строки', () => {
    expect(toPartDrafts(allocation())[0].value).toBe('100');
    expect(toPartDrafts(allocation({ mode: 'amount', parts: [part({ quantity: null, amount: 1500 })] }))[0].value)
      .toBe('1500');
  });

  it('у строки суммой расхождение со счётом в черновик не попадает — его отдал сервер, а не человек', () => {
    const view = allocation({ mode: 'amount', parts: [part({ quantity: null, amount: 1000.5, discrepancy: 0.5 })] });
    expect(toPartDrafts(view)[0].value).toBe('1000');
  });
});

describe('остаток до сохранения', () => {
  it('строка на 300 м, разнесено 250 — не разнесено 50 м и их доля суммы', () => {
    const rest = estimateRemainder([draft('100'), draft('150')], { quantity: 300, amount: 14550 }, 'quantity');
    expect(rest).toEqual({ quantity: 50, amount: 2425 });
  });

  it('строка суммой', () => {
    expect(estimateRemainder([draft('1000')], { quantity: null, amount: 1500 }, 'amount'))
      .toEqual({ quantity: null, amount: 500 });
  });
});

describe('состояние в клетке таблицы', () => {
  it('остаток виден числом с единицей', () => {
    expect(allocationStatus(allocation(), 'м')).toEqual({ text: 'не разнесено: 200 м', tone: 'warning' });
  });

  it('разнесено лишнее — строку уменьшили после разноски', () => {
    expect(allocationStatus(allocation({ unallocatedQuantity: -50 }), 'м').text).toBe('разнесено лишнее: 50 м');
  });

  it('удалённая стройка важнее остатка', () => {
    expect(allocationStatus(allocation({ parts: [part({ targetLost: true })] }), 'м').text).toBe('стройка удалена');
  });

  it('сходится', () => {
    expect(allocationStatus(allocation({ balanced: true, unallocatedQuantity: 0 }), 'м').tone).toBe('ok');
  });
});
