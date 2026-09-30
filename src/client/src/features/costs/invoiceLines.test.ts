import { describe, expect, it } from 'vitest';
import {
  emptyDraft, fromTable, guessRoles, hasHeader, mismatch, parseTable, preview, toDrafts, toNumber,
  toPayload, totals,
} from './invoiceLines';
import type { InvoiceLineView } from '@/shared/api/invoices';

/** Строка с сервера — минимально полная, чтобы не повторять десять полей в каждом тесте. */
function line(overrides: Partial<InvoiceLineView> = {}): InvoiceLineView {
  return {
    id: 'c0ffee00-0000-0000-0000-000000000001',
    ordinal: 1,
    nomenclatureId: null,
    nomenclatureName: null,
    nomenclatureLost: false,
    supplierText: null,
    supplierCode: null,
    unit: null,
    quantity: null,
    price: null,
    vatRate: null,
    vatAmount: null,
    amount: null,
    note: null,
    allocation: { mode: 'none', parts: [], unallocatedQuantity: null, unallocatedAmount: null, balanced: true },
    ...overrides,
  };
}

describe('предпросмотр сумм', () => {
  it('считает сумму строки количеством на цену', () => {
    const draft = { ...emptyDraft(), quantity: '7,25', price: '1 234,56' };
    expect(preview(draft).amount).toBe(8950.56);
  });

  /**
   * Ставка 20 % на сумме 10 000 даёт 1666,67, а не 2000: НДС считается «в том числе», потому что итог
   * документа по ТЗ так и называется. Числа выбраны так, чтобы два прочтения ставки давали РАЗНЫЕ
   * ответы — на круглых значениях разница спряталась бы в итоге.
   */
  it('считает НДС «в том числе», а не «сверху»', () => {
    const draft = { ...emptyDraft(), quantity: '100', price: '100', vatRate: '20' };
    expect(preview(draft).vat).toBe(1666.67);
  });

  it('набранную руками сумму не пересчитывает', () => {
    const draft = { ...emptyDraft(), quantity: '3', price: '33,33', amount: '99,98', vatRate: '20' };
    expect(preview(draft).amount).toBe(99.98);
  });

  it('без количества или цены суммы нет вовсе — а не нуль', () => {
    expect(preview({ ...emptyDraft(), price: '100' }).amount).toBeNull();
    expect(preview({ ...emptyDraft(), quantity: '2' }).vat).toBeNull();
  });
});

describe('сверка с суммой к оплате', () => {
  it('расхождение — число', () => {
    const drafts = [{ ...emptyDraft(), quantity: '2', price: '100' }];
    const sums = totals(drafts);
    expect(mismatch(250, sums.amount, sums.count)).toBe(50);
  });

  it('сошлось — расхождения нет', () => {
    const drafts = [{ ...emptyDraft(), quantity: '2', price: '100' }];
    expect(mismatch(200, totals(drafts).amount, 1)).toBeNull();
  });

  /** Без строк сверять НЕЧЕГО: «расхождение 128 400» у счёта без строк звучало бы как претензия. */
  it('без строк не сверяет', () => {
    expect(mismatch(128400, 0, 0)).toBeNull();
  });

  it('без суммы в бумаге не сверяет', () => {
    expect(mismatch(null, 200, 1)).toBeNull();
  });
});

describe('итоги по строкам', () => {
  it('считает ждущих позицию', () => {
    const sums = totals([
      { ...emptyDraft(), nomenclatureId: 'a', quantity: '1', price: '10' },
      { ...emptyDraft(), quantity: '1', price: '5' },
    ]);
    expect(sums).toMatchObject({ count: 2, withoutNomenclature: 1, amount: 15 });
  });
});

describe('вставка из буфера', () => {
  const excel = [
    'Наименование\tЕд.\tКол-во\tЦена\tСумма',
    'Кабель ВВГнг-LS 3х2,5\tм\t100\t48,50\t4850,00',
    'Труба гофрированная 20 мм\tм\t50\t12,00\t600,00',
  ].join('\n');

  it('разбирает таблицу по табуляции', () => {
    const table = parseTable(excel);
    expect(table).toHaveLength(3);
    expect(table[1]).toEqual(['Кабель ВВГнг-LS 3х2,5', 'м', '100', '48,50', '4850,00']);
  });

  /** Пробел разделителем НЕ считается: иначе наименование рассыпалось бы на четыре колонки. */
  it('не рвёт наименование по пробелам', () => {
    expect(parseTable('Кабель ВВГнг-LS 3х2,5\t100')[0]).toEqual(['Кабель ВВГнг-LS 3х2,5', '100']);
  });

  it('пустые строки выбрасывает', () => {
    expect(parseTable('a\t1\n\n\nb\t2')).toHaveLength(2);
  });

  it('узнаёт шапку и берёт роли из неё', () => {
    const table = parseTable(excel);
    expect(hasHeader(table)).toBe(true);
    expect(guessRoles(table)).toEqual(['supplierText', 'unit', 'quantity', 'price', 'amount']);
  });

  /**
   * Первая строка ТОВАРОВ за шапку не сходит: у неё нет подписей колонок. Иначе строка, начинающаяся
   * со слова «Кабель», потерялась бы — молча, потому что потерю одной строки из двадцати не видно.
   */
  it('строку товаров за шапку не принимает', () => {
    const table = parseTable('Кабель ВВГ 3х2,5\t100\t48,50\nТруба 20\t50\t12,00');
    expect(hasHeader(table)).toBe(false);
    expect(guessRoles(table)).toEqual(['supplierText', 'quantity', 'price']);
  });

  it('без шапки числа берёт по порядку: количество, цена, сумма', () => {
    const table = parseTable('Кабель\t10\t5\t50\t7');
    expect(guessRoles(table)).toEqual(['supplierText', 'quantity', 'price', 'amount', 'skip']);
  });

  it('собирает строки, выбрасывая шапку', () => {
    const table = parseTable(excel);
    const drafts = fromTable(table, guessRoles(table));

    expect(drafts).toHaveLength(2);
    expect(drafts[0]).toMatchObject({
      supplierText: 'Кабель ВВГнг-LS 3х2,5', unit: 'м', quantity: '100', price: '48,50',
      amount: '4850,00', nomenclatureId: null,
    });
    // ⚠️ Позиции у вставленных строк НЕТ и быть не может: наименование из бумаги — это слова
    // поставщика, и сопоставляет их человек (или C3). Счёт с такими строками живёт в отборе «Разобрать».
    expect(drafts.every(d => d.nomenclatureId === null)).toBe(true);
  });

  it('пропущенные колонки не попадают в строку', () => {
    const table = parseTable('Кабель\tмусор\t10');
    const drafts = fromTable(table, ['supplierText', 'skip', 'quantity']);
    expect(drafts[0].supplierText).toBe('Кабель');
    expect(drafts[0].quantity).toBe('10');
  });
});

describe('что уезжает на сервер', () => {
  it('позиция уезжает ссылкой, пустое — как null', () => {
    const drafts = [{ ...emptyDraft(), nomenclatureId: 'aa', quantity: '5', supplierText: '  ' }];
    expect(toPayload(drafts)[0]).toMatchObject({
      nomenclature: { $ref: 'catalog', entryId: 'aa' },
      quantity: '5',
      supplierText: null,
      note: null,
    });
  });

  /**
   * `id` сохранённой строки уезжает обратно. Без него сервер прочитал бы набор как «удали эти строки и
   * заведи новые» — и ссылки разноски (F1) порвались бы на каждом сохранении формы.
   */
  it('идентификатор сохранённой строки не теряется', () => {
    const drafts = toDrafts([line({ id: 'keep-me', quantity: 3 })]);
    expect(toPayload(drafts)[0].id).toBe('keep-me');
    expect(drafts[0].quantity).toBe('3');
  });

  it('новая строка уезжает без идентификатора', () => {
    expect(toPayload([emptyDraft()])[0].id).toBeNull();
  });
});

describe('числа из набранного', () => {
  it('понимает запятую и пробелы', () => {
    expect(toNumber('1 234,56')).toBe(1234.56);
    expect(toNumber('1 234,56')).toBe(1234.56);
  });

  it('пустое и мусор — не число', () => {
    expect(toNumber('')).toBeNull();
    expect(toNumber('кабель')).toBeNull();
  });
});

// ── Находки ревью PR #1117: каждая роняла числа молча ────────────────────────
//
// Общая черта у всех четырёх: они не ломали вставку, а СДВИГАЛИ смысл. Цена, попавшая в количество,
// даёт правдоподобную сумму; НДС размером во всю строку сходится со сверкой. Заметить это можно было
// только по расхождению с бумагой — то есть не заметить.
describe('разбор вставки не сдвигает смысл', () => {
  it('точка с запятой внутри ячейки таб-таблицы колонок не добавляет', () => {
    const table = parseTable([
      'Кабель ВВГнг-LS 3х2,5\t100\t48,50',
      'Труба 20; ГОСТ 55000\t50\t12,00',
    ].join('\n'));

    expect(table.map(row => row.length)).toEqual([3, 3]);
    expect(table[1][0]).toBe('Труба 20; ГОСТ 55000');

    const rows = fromTable(table, guessRoles(table));
    expect(rows[0].quantity).toBe('100');
    expect(rows[0].price).toBe('48,50');
  });

  it('точка с запятой делит там, где табуляции нет вовсе', () => {
    expect(parseTable('Кабель;100;48,50')).toEqual([['Кабель', '100', '48,50']]);
  });

  it('пустая колонка роль числа не забирает', () => {
    // Два таба подряд — обычный результат извлечения таблицы из PDF: колонка есть, данных в ней нет.
    const table = parseTable('Кабель\t\t10\t5');
    const rows = fromTable(table, guessRoles(table));

    expect(rows[0].quantity).toBe('10');
    expect(rows[0].price).toBe('5');
  });

  it('«Предмет поставки» — наименование, а не единица измерения', () => {
    const table = parseTable([
      'Наименование\tПредмет поставки\tКол-во\tЦена',
      'Кабель\tпоставка по договору № 17/2026 от 01.02.2026 силового кабеля\t10\t5',
    ].join('\n'));
    const roles = guessRoles(table);

    expect(roles).not.toContain('unit');
    expect(fromTable(table, roles)[0].unit).toBe('');
  });

  it('«Ед.» единицей остаётся', () => {
    const table = parseTable('Наименование\tЕд.\tКол-во\tЦена\nКабель\tм\t10\t5');
    expect(fromTable(table, guessRoles(table))[0].unit).toBe('м');
  });

  it('«Сумма с НДС» — итог строки, а не сумма НДС', () => {
    const table = parseTable([
      'Наименование\tКол-во\tЦена\tСумма с НДС',
      'Кабель\t10\t100\t1000',
    ].join('\n'));
    const rows = fromTable(table, guessRoles(table));

    expect(rows[0].amount).toBe('1000');
    expect(rows[0].vatAmount).toBe('');
  });

  it('«Сумма НДС» суммой НДС и остаётся', () => {
    const table = parseTable('Наименование\tКол-во\tЦена\tСумма НДС\nКабель\t10\t100\t200');
    expect(fromTable(table, guessRoles(table))[0].vatAmount).toBe('200');
  });

  it('две колонки на одну роль: вторая пропускается, а шапка остаётся шапкой', () => {
    const table = parseTable([
      'Наименование\tСумма без НДС\tСумма с НДС',
      'Кабель\t800\t1000',
    ].join('\n'));

    // Шапка узнана — значит в счёт она не поедет строкой с числами в примечании.
    expect(hasHeader(table)).toBe(true);

    const roles = guessRoles(table);
    expect(roles.filter(role => role === 'amount')).toHaveLength(1);

    const rows = fromTable(table, roles);
    expect(rows).toHaveLength(1);
    expect(rows[0].note).toBe('');
    expect(rows[0].supplierText).toBe('Кабель');
  });
});

describe('потеря позиции приходит от сервера', () => {
  it('позиция без названия потерянной не считается', () => {
    const [draft] = toDrafts([line({
      nomenclatureId: 'c0ffee00-0000-0000-0000-000000000009',
      nomenclatureName: null,
      nomenclatureLost: false,
    })]);

    expect(draft.nomenclatureId).not.toBeNull();
    expect(draft.nomenclatureLost).toBe(false);
  });

  it('потеря доезжает до правки как есть', () => {
    const [draft] = toDrafts([line({
      nomenclatureId: 'c0ffee00-0000-0000-0000-000000000009',
      nomenclatureName: null,
      nomenclatureLost: true,
    })]);

    expect(draft.nomenclatureLost).toBe(true);
  });
});
