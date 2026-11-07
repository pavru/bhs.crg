import { describe, expect, it } from 'vitest';
import type { InvoiceView } from '@/shared/api/invoices';
import { parsedHold, payHold, primaryOf, summaryMeta } from './invoicePrimary';

/** Основное действие сводки (costs-invoice-screen-10-layout, D-10.6, ТЗ COST-6.2). */

const view = (state: string, payment: Partial<InvoiceView['payment']> = {}, requisites: Record<string, unknown> = {}) => ({
  id: 'i1',
  requisites: { Состояние: state, ...requisites },
  payment: { paid: false, paidOn: null, refusal: null, lockedBy: null, ...payment },
} as unknown as InvoiceView);

const all = { edit: true, pay: true };
const usual = { correcting: false, gone: false };

describe('primaryOf', () => {
  it('черновик → «Разобран»', () => {
    expect(primaryOf(view('Черновик'), all, usual)).toBe('parsed');
  });

  it('разобранный неоплаченный с правом оплаты → «Отметить оплату…»', () => {
    expect(primaryOf(view('Разобран'), { edit: false, pay: true }, usual)).toBe('pay');
  });

  it('без права на действие — ничего', () => {
    expect(primaryOf(view('Разобран'), { edit: true, pay: false }, usual)).toBeNull();
    expect(primaryOf(view('Черновик'), { edit: false, pay: true }, usual)).toBeNull();
  });

  it('исправление → «Проверить исправление…»', () => {
    expect(primaryOf(view('Разобран', { paid: true, lockedBy: 'период закрыт' }), all, { correcting: true, gone: false })).toBe('check');
  });

  it('отклонённый, оплаченный разобранный, запертый и удалённый — ничего', () => {
    expect(primaryOf(view('Отклонён'), all, usual)).toBeNull();
    expect(primaryOf(view('Разобран', { paid: true }), all, usual)).toBeNull();
    expect(primaryOf(view('Черновик', { lockedBy: 'период закрыт' }), all, usual)).toBeNull();
    expect(primaryOf(view('Черновик'), all, { correcting: false, gone: true })).toBeNull();
  });
});

describe('почему неактивно', () => {
  it('«Разобран»: несохранённое сильнее перечня — перечень о сохранённом', () => {
    expect(parsedHold([{ id: 'a', label: 'плательщик', target: null }], true)).toMatch(/сохраните правки/);
  });

  it('«Разобран»: подсказка называет первые пункты готовности, остальные — числом', () => {
    const items = ['плательщик', 'разноска', '3 строки без позиции', 'сумма'].map(label => ({ id: label, label, target: null }));
    expect(parsedHold(items, false)).toBe('До «Разобран» осталось: плательщик · разноска · 3 строки без позиции и ещё 1');
  });

  it('«Разобран»: перечень пуст или его нет — активна', () => {
    expect(parsedHold([], false)).toBeNull();
    expect(parsedHold(null, false)).toBeNull();
  });

  it('оплата: несохранённая строка гасит «Отметить оплату…» с причиной «сохраните правки»', () => {
    expect(payHold(true)).toMatch(/сохраните правки/);
  });

  it('«Разобран»: пока запись в пути — неактивна, даже при пустом перечне (COST-6.4)', () => {
    expect(parsedHold([], false, true)).toMatch(/сохраняется/);
  });

  it('оплата: нет отказа и всё сохранено — активна', () => {
    expect(payHold(false)).toBeNull();
  });

  it('оплата: отказ сервера — в подсказке неактивной кнопки его словами (COST-6.4, 47–48)', () => {
    expect(payHold(false, 'сумма строк не сходится')).toBe('Оплатить нельзя: сумма строк не сходится');
    expect(payHold(true, 'сумма строк не сходится')).toMatch(/сохраните правки/);
    expect(payHold(false, null, true)).toMatch(/сохраняется/);
  });
});

describe('summaryMeta', () => {
  it('поставщик · дата · сумма — по сохранённому', () => {
    expect(summaryMeta(view('Черновик', {}, { Дата: '2026-09-03', Итого: 128400.5 }), 'ООО «Кабель-Торг»'))
      .toMatch(/^ООО «Кабель-Торг» · от 03\.09\.2026 · 128\s400,50/);
    expect(summaryMeta(view('Черновик'), null)).toBe('поставщик не выбран');
  });
});
