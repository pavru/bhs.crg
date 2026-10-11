import { describe, it, expect } from 'vitest';
import type { InvoiceListItem } from '@/shared/api/invoices';
import type { InvoiceQueues } from '@/shared/api/invoiceQueues';
import {
  afterParsed, backRefused, emptyText, heldRow, keepTrace, lostTitle, placesCount, placesText, queueChip, queueRows, rowReadiness,
} from './invoiceQueues';

/** Очереди списка счетов — чипы «наведите порядок» в рейле (issue #1186). */

const numbers = (patch: Partial<InvoiceQueues> = {}): InvoiceQueues => ({
  lost: 3, archived: 5, locked: 0, doubt: null, unrecognized: 0, ...patch,
});

const invoice = (id: string, patch: Partial<InvoiceListItem> = {}): InvoiceListItem => ({
  id, number: id, issuedOn: null, supplierId: null, supplierName: null, total: null, state: '', payment: '',
  dueDate: null, purpose: null, unconfirmedCount: 0, hasScan: false, linesCount: 0, linesWithoutNomenclature: 0,
  ...patch,
});

describe('чип очереди', () => {
  it('числа не пришли: чипа нет, а нажатый остаётся — без числа и без слова «ноль»', () => {
    expect(queueChip('lost', undefined, false)).toBeNull();
    // Иначе отбор нечем снять.
    expect(queueChip('lost', undefined, true)).toEqual({ count: '', title: 'Сколько таких счетов — не посчитано.', doubt: false });
  });

  it('ноль не рисуется, а нажатый чип остаётся — без числа', () => {
    expect(queueChip('lost', numbers({ lost: 0 }), false)).toBeNull();
    expect(queueChip('lost', numbers({ lost: 0 }), true)?.count).toBe('');
    expect(queueChip('lost', numbers(), false)?.count).toBe('3');
    expect(queueChip('archived', numbers(), false)?.count).toBe('5');
  });

  it('непроверенный ноль — знак вопроса, а не отсутствие чипа', () => {
    const doubt = queueChip('lost', numbers({ lost: 0, doubt: 'проверено не всё: колонок — 1' }), false);

    expect(doubt?.count).toBe('?');
    expect(doubt?.doubt).toBe(true);
    expect(doubt?.title).toContain('Число неполное — проверено не всё: колонок — 1.');
    expect(queueChip('lost', numbers({ doubt: 'проверено не всё' }), false)?.count).toBe('3?');
  });

  it('о запертых говорит подсказка чипа потерь, а в число они не входят', () => {
    expect(queueChip('lost', numbers({ locked: 2 }), true)?.title).toContain('закрытого периода (2)');
    expect(queueChip('lost', numbers(), true)?.title).not.toContain('закрытого периода');
    expect(queueChip('archived', numbers({ locked: 2 }), true)?.title).not.toContain('закрытого периода');
  });

  it('архив не зовёт исправлять', () => {
    const title = queueChip('archived', numbers(), false)!.title;

    expect(title).toContain('Счёт верен');
    expect(title).not.toContain('замените');
  });
});

describe('пометка строки', () => {
  const places = [
    { kind: 'supplier', count: 1 }, { kind: 'position', count: 2 }, { kind: 'allocation', count: 1 },
  ] as const;

  it('считает ссылки и называет места', () => {
    expect(placesCount([...places])).toBe(4);
    expect(placesCount(undefined)).toBe(0);
    expect(placesText([...places])).toBe('поставщик · позиции: 2 · разноска: 1');
  });

  it('говорит, что с потерей можно сделать', () => {
    const of = (lostState: 'fixable' | 'locked' | 'type') => lostTitle(invoice('1', {
      references: { supplierLost: false, lostState, archivedCalls: false, lost: [{ kind: 'type', count: 1 }], archived: [] },
    }));

    expect(of('fixable')).toContain('замените значение');
    expect(of('locked')).toContain('исправить нельзя');
    expect(of('type')).toContain('Заменить тип счёта в форме нечем');
  });
});

describe('открытый счёт под отбором', () => {
  const a = invoice('a'), b = invoice('b'), c = invoice('c');

  it('после исправления строка остаётся на своём месте, пока счёт открыт', () => {
    const held = heldRow(null, 'lost', 'b', [a, b, c]);
    expect(held).toEqual({ queue: 'lost', item: b, index: 1 });

    // Сохранили с заменой: сервер счёт под отбором уже не отдаёт.
    const after = heldRow(held, 'lost', 'b', [a, c]);
    expect(after).toBe(held);
    expect(queueRows([a, c], after)).toEqual([
      { item: a, left: false }, { item: b, left: true }, { item: c, left: false },
    ]);
  });

  it('ушли со счёта, сменили или сняли отбор — строка уходит', () => {
    const held = heldRow(null, 'lost', 'b', [a, b, c]);

    expect(heldRow(held, 'lost', 'a', [a, c])).toEqual({ queue: 'lost', item: a, index: 0 });
    expect(heldRow(held, 'lost', 'c2', [a, c])).toBeNull();
    expect(heldRow(held, 'archived', 'b', [a, c])).toBeNull();
    expect(heldRow(held, null, 'b', [a, b, c])).toBeNull();
    expect(queueRows([a, c], null).map(r => r.item.id)).toEqual(['a', 'c']);
  });

  it('счёт, которого под отбором не было, в список не подставляется', () => {
    expect(heldRow(null, 'lost', 'b', [a, c])).toBeNull();
  });

  it('пока список грузится, запомненное держится, а без изменений — тот же объект', () => {
    const held = heldRow(null, 'lost', 'b', [a, b]);

    expect(heldRow(held, 'lost', 'b', undefined)).toBe(held);
    expect(heldRow(held, 'lost', 'b', [a, b])).toBe(held);
    // Исправили одно место из трёх: строка та же, данные новые.
    const fresh = invoice('b', { linesCount: 5 });
    expect(heldRow(held, 'lost', 'b', [a, fresh])?.item).toBe(fresh);
  });
});

describe('пустой список', () => {
  it('у каждого отбора свои слова', () => {
    expect(emptyText(null, '', numbers())).toBe('Счетов пока нет.');
    expect(emptyText('lost', '', numbers())).toBe('Исправлять нечего: счетов с удалёнными записями нет.');
    expect(emptyText('lost', '', numbers({ locked: 2 }))).toContain('закрытого периода (2)');
    expect(emptyText('lost', '', numbers({ locked: 2, doubt: 'проверено не всё: колонок — 1' })))
      .toBe('Удалённых записей не найдено, но проверено не всё: колонок — 1.');
    expect(emptyText('archived', '', numbers())).toContain('из архива нет');
    expect(emptyText('parsing', '', undefined)).toContain('Разбирать нечего');
  });

  it('числа не пришли — «исправлять нечего» не говорится: о запертых не известно ничего', () => {
    const lost = emptyText('lost', '', undefined);

    expect(lost).not.toContain('Исправлять нечего');
    expect(lost).toContain('не посчитано');
    expect(emptyText('archived', '', undefined)).toContain('не посчитано');
  });

  it('поиск под отбором называет и отбор, и запрос', () => {
    expect(emptyText('lost', ' ромашка ', numbers())).toBe('В отборе «Удалённые записи» по запросу «ромашка» ничего нет.');
    expect(emptyText(null, 'ромашка', undefined)).toBe('Ничего не найдено.');
  });
});

describe('после «Разобран» под отбором очереди', () => {
  const a = invoice('a'), b = invoice('b'), c = invoice('c');
  const rows = (...items: InvoiceListItem[]) => queueRows(items, null);
  const numbered = (id: string, number: string | null) => ({ ...invoice(id), number }) as InvoiceListItem;

  it('открывается следующая строка списка, с последней — первая', () => {
    expect(afterParsed('parsing', rows(a, b, c), 'a', '15', '', false)?.open).toBe(b);
    expect(afterParsed('parsing', rows(a, b, c), 'c', '15', '', false)?.open).toBe(a);
  });

  it('сообщение называет оба счёта, а следующий без номера — не выдумывает его', () => {
    const next = numbered('n', '16');
    expect(afterParsed('parsing', rows(a, next), 'a', '15', '', false)?.message)
      .toBe('Счёт № 15 разобран. Открыт следующий — № 16.');
    expect(afterParsed('parsing', rows(a, numbered('n', null)), 'a', null, '', false)?.message)
      .toBe('Счёт без номера разобран. Открыт следующий.');
  });

  it('вне отбора очереди счёт остаётся открытым и слов нет', () => {
    expect(afterParsed(null, rows(a, b), 'a', '15', '', false)).toBeNull();
    expect(afterParsed('lost', rows(a, b), 'a', '15', '', false)).toBeNull();
  });

  it('очередь кончилась — счёт остаётся, и сказано, что именно пусто', () => {
    expect(afterParsed('parsing', rows(a), 'a', '15', '', false))
      .toEqual({ open: null, message: 'Счёт № 15 разобран. Очередь «Разобрать» пуста.' });
    expect(afterParsed('parsing', rows(a), 'a', '15', ' кабель ', false)?.message)
      .toBe('Счёт № 15 разобран. По запросу «кабель» в отборе больше ничего нет.');
  });

  it('несохранённое в форме — с неё не уводят', () => {
    expect(afterParsed('parsing', rows(a, b), 'a', '15', '', true))
      .toEqual({ open: null, message: 'Счёт № 15 разобран. Следующий не открыт: в форме несохранённые правки.' });
  });

  it('след прежнего разобранного в очередь не входит: к нему не ведут', () => {
    // b разобран раньше и стоит следом; открыт c — последний живой.
    const trace = { queue: 'parsing' as const, item: b, index: 1 };
    const shown = queueRows([a, c], null, trace);
    expect(shown.map(r => [r.item.id, r.left, r.parsed ?? false])).toEqual([
      ['a', false, false], ['b', true, true], ['c', false, false],
    ]);
    expect(afterParsed('parsing', shown, 'c', '15', '', false)?.open).toBe(a);
    expect(afterParsed('parsing', queueRows([c], null, trace), 'c', '15', '', false)?.open).toBeNull();
  });

  it('список не прочитан — это не пустая очередь', () => {
    expect(afterParsed('parsing', null, 'a', '15', '', false))
      .toEqual({ open: null, message: 'Счёт № 15 разобран. Следующий не открыт: список счетов не прочитан.' });
  });

  it('следующий берётся из перечитанного списка: разобранный стоит в нём удержанной строкой', () => {
    // Сервер b уже не отдаёт; сосед тем временем убрал и c — следующим идёт a, а не c.
    const held = { queue: 'parsing' as const, item: b, index: 1 };
    expect(afterParsed('parsing', queueRows([a, c], held), 'b', '15', '', false)?.open).toBe(c);
    expect(afterParsed('parsing', queueRows([a], held), 'b', '15', '', false)?.open).toBe(a);
    // Поиск скрыл сам разобранный — идём с начала найденного.
    expect(afterParsed('parsing', rows(a, c), 'b', '15', 'запрос', false)?.open).toBe(a);
  });

  it('след снимается сменой отбора и возвратом счёта в список', () => {
    const trace = { queue: 'parsing' as const, item: b, index: 1 };
    expect(keepTrace(trace, 'parsing', [a, c])).toBe(true);
    expect(keepTrace(trace, 'parsing', undefined)).toBe(true);
    expect(keepTrace(trace, 'parsing', [a, b, c])).toBe(false);
    expect(keepTrace(trace, 'archived', [a, c])).toBe(false);
    expect(keepTrace(trace, null, [a, c])).toBe(false);
    expect(keepTrace(null, 'parsing', [a])).toBe(false);
  });

  it('«Вернуться» при несохранённом называет, где счёт искать', () => {
    expect(backRefused('15'))
      .toBe('Счёт № 15 не открыт: в форме несохранённые правки. Он стоит в списке строкой «разобран».');
  });

  it('след и удержанный открытый счёт стоят каждый на своём месте', () => {
    const trace = { queue: 'parsing' as const, item: a, index: 0 };
    const held = { queue: 'parsing' as const, item: c, index: 2 };
    expect(queueRows([b], held, trace).map(r => r.item.id)).toEqual(['a', 'b', 'c']);
    // Открыли сам след — строка одна.
    expect(queueRows([b], { ...trace }, trace).map(r => r.item.id)).toEqual(['a', 'b']);
  });
});

describe('rowReadiness', () => {
  const row = (readiness: string[] | null | undefined) => ({ id: 'i', readiness }) as unknown as InvoiceListItem;

  it('причин нет — строка молчит: это не «готов»', () => {
    expect(rowReadiness(row(null))).toBeNull();
    expect(rowReadiness(row(undefined))).toBeNull();
  });

  it('пустой перечень — готов, осталось нажать', () => {
    expect(rowReadiness(row([]))).toMatchObject({ text: 'готов к «Разобран»', ready: true });
  });

  it('в строку помещаются два пункта, остальные — числом и подсказкой', () => {
    expect(rowReadiness(row(['плательщик']))?.text).toBe('осталось: плательщик');
    expect(rowReadiness(row(['плательщик', 'разноска']))?.text).toBe('осталось: плательщик · разноска');

    const many = rowReadiness(row(['номер', 'плательщик', 'строки', 'разноска']))!;
    expect(many.text).toBe('осталось: номер · плательщик · ещё 2');
    expect(many.title).toBe('До «Разобран» осталось: номер · плательщик · строки · разноска.');
    expect(many.ready).toBe(false);
  });
});
