import { describe, it, expect } from 'vitest';
import type { InvoiceListItem } from '@/shared/api/invoices';
import type { TableShortcut } from '@/shared/api/tables';
import { emptyText, heldRow, lostTitle, placesCount, placesText, queueChip, queueRows } from './invoiceQueues';

/** Очереди списка счетов — чипы «наведите порядок» в рейле (issue #1186). */

const shortcut = (patch: Partial<TableShortcut>): TableShortcut => ({
  code: 'lost', title: 'Ссылки на удалённые записи', hint: null, column: 'Ссылки', op: 'eq', value: 'есть',
  count: 3, unchecked: null, quiet: false, ...patch,
});

const invoice = (id: string, patch: Partial<InvoiceListItem> = {}): InvoiceListItem => ({
  id, number: id, issuedOn: null, supplierId: null, supplierName: null, total: null, state: '', payment: '',
  dueDate: null, purpose: null, unconfirmedCount: 0, hasScan: false, linesCount: 0, linesWithoutNomenclature: 0,
  ...patch,
});

describe('чип очереди', () => {
  it('без готового отбора чипа нет: сервер его не предложил', () => {
    expect(queueChip('lost', undefined, false)).toBeNull();
    // Даже нажатый: права править счёт нет — и снимать нечего.
    expect(queueChip('lost', undefined, true)).toBeNull();
  });

  it('ноль не рисуется, а нажатый чип остаётся — без числа', () => {
    expect(queueChip('lost', shortcut({ count: 0 }), false)).toBeNull();
    expect(queueChip('lost', shortcut({ count: 0 }), true)?.count).toBe('');
    expect(queueChip('lost', shortcut({}), false)?.count).toBe('3');
  });

  it('непроверенный ноль — знак вопроса, а не отсутствие чипа', () => {
    const doubt = queueChip('lost', shortcut({ count: 0, unchecked: 'проверено не всё: колонок — 1' }), false);

    expect(doubt?.count).toBe('?');
    expect(doubt?.doubt).toBe(true);
    expect(doubt?.title).toContain('Число неполное — проверено не всё: колонок — 1.');
    expect(queueChip('lost', shortcut({ unchecked: 'проверено не всё' }), false)?.count).toBe('3?');
  });

  it('о запертых говорит подсказка, а в число они не входят', () => {
    expect(queueChip('lost', shortcut({}), true, 2)?.title).toContain('закрытого периода (2)');
    expect(queueChip('lost', shortcut({}), true, 0)?.title).not.toContain('закрытого периода');
  });

  it('архив не зовёт исправлять', () => {
    const title = queueChip('archived', shortcut({ code: 'archived', quiet: true }), false)!.title;

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
      references: { supplierLost: null, lostState, archivedCalls: false, lost: [{ kind: 'type', count: 1 }], archived: [] },
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
    expect(emptyText(null, '', null, 0)).toBe('Счетов пока нет.');
    expect(emptyText('lost', '', null, 0)).toBe('Исправлять нечего: счетов с удалёнными записями нет.');
    expect(emptyText('lost', '', null, 2)).toContain('закрытого периода (2)');
    expect(emptyText('lost', '', 'проверено не всё: колонок — 1', 2))
      .toBe('Удалённых записей не найдено, но проверено не всё: колонок — 1.');
    expect(emptyText('archived', '', null, 0)).toContain('из архива нет');
    expect(emptyText('parsing', '', null, 0)).toContain('Разбирать нечего');
  });

  it('поиск под отбором называет и отбор, и запрос', () => {
    expect(emptyText('lost', ' ромашка ', null, 0)).toBe('В отборе «Удалённые записи» по запросу «ромашка» ничего нет.');
    expect(emptyText(null, 'ромашка', null, 0)).toBe('Ничего не найдено.');
  });
});
