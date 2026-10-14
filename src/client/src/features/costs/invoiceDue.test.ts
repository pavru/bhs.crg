import { describe, expect, it } from 'vitest';
import type { InvoiceView } from '@/shared/api/invoices';
import { deferralNote, dueState, hints, rowDue, shippedBeforeIssued } from './invoiceDue';

function view(due: string | null, manual: boolean, byRule: string | null): InvoiceView {
  return { requisites: { Срок: due }, due: { manual, byRule } } as unknown as InvoiceView;
}

describe('dueState', () => {
  it('срок по правилу — сказать нечего, кнопки нет', () => {
    expect(dueState(view('2026-10-10', false, '2026-10-10'), {}, false)).toEqual({ note: null, offerRule: false });
  });

  it('ручной срок помечен, «По правилу» предложено', () => {
    const state = dueState(view('2026-10-20', true, '2026-10-10'), {}, false);
    expect(state.offerRule).toBe(true);
    expect(state.note).toBe('Изменён вручную; по правилу — 10.10.2026');
  });

  it('срок по прежнему правилу называет новое значение', () => {
    const state = dueState(view('2026-10-10', false, '2026-10-11'), {}, false);
    expect(state).toEqual({ note: 'По действующему правилу — 11.10.2026', offerRule: true });
  });

  it('без отгрузки — «не определён», и стирать нечего', () => {
    expect(dueState(view(null, false, null), {}, false))
      .toEqual({ note: 'Не определён: нужны дата отгрузки и отсрочка', offerRule: false });
  });

  it('срок из бумаги без правила кнопкой не стирается', () => {
    expect(dueState(view('2026-10-20', false, null), {}, false).offerRule).toBe(false);
  });

  it('ручной срок без правила — кнопки нет: возвращать некуда', () => {
    const state = dueState(view('2026-12-01', true, null), {}, false);
    expect(state).toEqual({ note: 'Изменён вручную — правило его не меняет', offerRule: false });
  });

  it('пока шапка правится и у запертого счёта кнопки нет', () => {
    expect(dueState(view('2026-10-20', true, '2026-10-10'), { Назначение: 'x' }, false).offerRule).toBe(false);
    expect(dueState(view('2026-10-20', true, '2026-10-10'), {}, true).offerRule).toBe(false);
  });

  it('старый сервер признаков не прислал — форма молчит', () => {
    expect(dueState({ requisites: {} } as unknown as InvoiceView, {}, false)).toEqual({ note: null, offerRule: false });
  });
});

describe('hints', () => {
  it('показывает все подсказки, а не первую', () => {
    expect(hints('Распознано, не подтверждено', 'Отгрузка раньше даты счёта')).toBe('Распознано, не подтверждено · Отгрузка раньше даты счёта');
    expect(hints(undefined, null)).toBeUndefined();
    expect(hints(null, 'одна')).toBe('одна');
  });
});

describe('shippedBeforeIssued', () => {
  it('отгрузка раньше даты счёта', () => {
    expect(shippedBeforeIssued('2026-09-01', '2026-09-03')).toBe(true);
    expect(shippedBeforeIssued('2026-09-03', '2026-09-03')).toBe(false);
    expect(shippedBeforeIssued(null, '2026-09-03')).toBe(false);
    expect(shippedBeforeIssued('2026-09-01', '')).toBe(false);
  });
});

describe('deferralNote', () => {
  const supplier = { $ref: 'catalog', entryId: 'v1' };
  function termed(deferral: number | null, terms: number | null): InvoiceView {
    return {
      requisites: { Поставщик: supplier, Отсрочка: deferral },
      due: { manual: false, byRule: null, supplierDeferral: terms },
    } as unknown as InvoiceView;
  }

  it('отсрочка как в условиях — «из условий поставщика»', () => {
    expect(deferralNote(termed(30, 30), {})).toBe('Из условий поставщика');
  });

  it('отсрочка отличается или пуста — названы условия', () => {
    expect(deferralNote(termed(45, 30), {})).toBe('В условиях поставщика — 30 дн.');
    expect(deferralNote(termed(null, 30), {})).toBe('В условиях поставщика — 30 дн.');
  });

  it('набранное в форме сравнивается с условиями', () => {
    expect(deferralNote(termed(45, 30), { Отсрочка: '30' })).toBe('Из условий поставщика');
  });

  it('условий нет — молчит', () => {
    expect(deferralNote(termed(30, null), {})).toBeNull();
  });

  it('поставщика сменили и не сохранили — молчит: условия прежнего', () => {
    expect(deferralNote(termed(30, 30), { Поставщик: { $ref: 'catalog', entryId: 'v2' } })).toBeNull();
    expect(deferralNote(termed(30, 30), { Поставщик: supplier })).toBe('Из условий поставщика');
  });
});

describe('rowDue', () => {
  it('просроченный помечен словом', () => {
    expect(rowDue({ daysLeft: -3, overdue: true, dueDate: '2026-10-11' }))
      .toEqual({ text: 'просрочен', tone: 'danger', title: 'Срок оплаты прошёл 3 дн. назад (11.10.2026)' });
  });

  it('в день срока — «сегодня»', () => {
    expect(rowDue({ daysLeft: 0, overdue: false, dueDate: '2026-10-14' })?.text).toBe('сегодня');
  });

  it('срок близко — предупреждение, далеко — тихо', () => {
    expect(rowDue({ daysLeft: 3, overdue: false, dueDate: null })?.tone).toBe('warning');
    expect(rowDue({ daysLeft: 20, overdue: false, dueDate: null })).toMatchObject({ text: '20 дн.', tone: 'quiet' });
  });

  it('счёт оплаты не ждёт либо срока нет — отметки нет', () => {
    expect(rowDue({ daysLeft: null, overdue: false, dueDate: '2026-10-14' })).toBeNull();
    expect(rowDue({ daysLeft: undefined, overdue: undefined, dueDate: null })).toBeNull();
  });
});
