// @vitest-environment jsdom
import { afterEach, describe, expect, it } from 'vitest';
import { cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import type { InvoiceRecognition, UnplacedRead } from '@/shared/api/invoiceRecognition';
import { RecognitionNotes } from './InvoiceRecognitionNote';
import { unplacedGroups, unplacedReason, unplacedSummary, unplacedWhere } from './unplaced';

/**
 * «Прочитано, в счёт не положено» в форме счёта (ТЗ COST-8, issue #1484, макет 1b). Чем ломается: колонка
 * «Артикл» с опечаткой не легла, а свёрнутая строка её не называет; прежнее распознавание показывает «0»;
 * строка окрашена предупреждением и стала шумом в каждом счёте.
 */

afterEach(cleanup);

const line = (row: number, key: string, value: string): UnplacedRead =>
  ({ place: 'line', row, key, value, reason: 'no-field', detail: null, field: null });
const head = (key: string, value: string, reason: UnplacedRead['reason'], detail: string | null = null, field: string | null = null): UnplacedRead =>
  ({ place: 'header', row: null, key, value, reason, detail, field });

const COUNTRIES = ['Россия', 'Китай', 'Россия', 'Турция'];
const LIST: UnplacedRead[] = [
  ...COUNTRIES.map((c, i) => line(i + 1, 'Страна', c)),
  line(2, 'Артикл', 'ВВГ-3х2,5'), line(5, 'Артикл', 'ГОФ-20'),
  head('Срок', '01.11.2026', 'not-recognizable', 'срок считает правило', 'Оплатить до'),
];

const recognition = (patch: Partial<InvoiceRecognition>): InvoiceRecognition => ({
  state: 'done', reason: null, error: null, engine: null, progress: null, values: null, offers: null,
  lines: null, notes: [], startedAt: null, finishedAt: null, canStart: true, whyNot: null, parties: null, byFormerImage: false,
  unplaced: null, ...patch,
});

describe('свёрнутая строка', () => {
  it('называет ключи строк с числом строк и шапку числом — без окраски предупреждения', () => {
    render(<RecognitionNotes recognition={recognition({ unplaced: LIST })} />);
    const block = screen.getByTestId('unplaced');
    expect(block.textContent).toContain('Ещё прочитано, в счёт не положено: Страна (в 4 строках), Артикл (в 2 строках), и в шапке: 1');
    expect(block.className).not.toContain('warning');
    expect(block.closest('section')!.className).not.toContain('warning');
    // Свёрнута: таблицы нет, пока не нажато «Показать».
    expect(screen.queryByRole('table')).toBeNull();
  });

  it('блок показан и без замечаний — ради одного непустого списка', () => {
    render(<RecognitionNotes recognition={recognition({ unplaced: [line(1, 'Страна', 'Россия')] })} />);
    expect(screen.getByRole('heading', { name: 'Замечания распознавания' })).toBeTruthy();
    expect(screen.queryByRole('list')).toBeNull();
  });

  it('null и [] — строки нет: «не знаем» не показывается нулём', () => {
    const { container } = render(<RecognitionNotes recognition={recognition({ unplaced: null })} />);
    expect(container.textContent).toBe('');
    cleanup();
    const empty = render(<RecognitionNotes recognition={recognition({ unplaced: [] })} />);
    expect(empty.container.textContent).toBe('');
    cleanup();
    // Замечания есть, список пуст — замечания на месте, строки «в счёт не положено» нет.
    render(<RecognitionNotes recognition={recognition({ notes: ['Номер: впишите вручную.'], unplaced: [] })} />);
    expect(screen.queryByTestId('unplaced')).toBeNull();
    expect(screen.getByRole('list').textContent).toContain('впишите вручную');
  });

  it('только шапка — без перечня ключей', () => {
    render(<RecognitionNotes recognition={recognition({ unplaced: [head('КодКлиента', 'К-1', 'no-field')] })} />);
    expect(screen.getByTestId('unplaced').textContent).toContain('в счёт не положено: в шапке: 1');
  });
});

describe('раскрытие', () => {
  it('«Показать» раскрывает сводку по ключу и причине, шеврон ключа — строки файла', () => {
    render(<RecognitionNotes recognition={recognition({ unplaced: LIST })} />);
    fireEvent.click(screen.getByRole('button', { name: /Показать/ }));
    const table = screen.getByRole('table');
    // Шапка первой, потом ключи строк: одна строка на ключ, а не на строку файла.
    const rows = within(table).getAllByRole('row').slice(1);
    expect(rows.map(r => r.textContent)).toEqual([
      'Срокшапка01.11.2026поле «Оплатить до» не заполняется распознаванием: срок считает правило',
      'Странастроки 1–4Россия, Китай, Турцияу строки счёта нет поля с таким ключом',
      'Артиклстроки 2, 5ВВГ-3х2,5, ГОФ-20у строки счёта нет поля с таким ключом',
    ]);

    fireEvent.click(within(table).getByRole('button', { name: /Артикл/ }));
    const opened = within(screen.getByRole('table')).getAllByRole('row').slice(1).map(r => r.textContent);
    expect(opened.slice(3)).toEqual(['строка 2ВВГ-3х2,5', 'строка 5ГОФ-20']);

    fireEvent.click(screen.getByRole('button', { name: /Скрыть/ }));
    expect(screen.queryByRole('table')).toBeNull();
  });
});

describe('сводка', () => {
  it('ключи с разной причиной — разные строки; строки файла считаются без повторов', () => {
    const list = [line(1, 'Цена ', '5'), { ...line(1, 'Цена ', '5'), reason: 'duplicate-key' as const, detail: 'Цена' }, line(3, 'Цена ', '7')];
    expect(unplacedGroups(list).map(g => [g.key, g.reason, g.items.length])).toEqual([
      ['Цена ', 'no-field', 2], ['Цена ', 'duplicate-key', 1],
    ]);
    expect(unplacedSummary(list)).toEqual({ lineKeys: [{ key: 'Цена ', rows: 2 }], header: 0 });
  });

  it('«Где»: одна строка, подряд, вразброс и длинный перечень', () => {
    const where = (rows: number[]) => unplacedWhere(unplacedGroups(rows.map(r => line(r, 'К', 'v')))[0]);
    expect(where([7])).toBe('строка 7');
    expect(where([3, 1, 2])).toBe('строки 1–3');
    expect(where([2, 5, 9])).toBe('строки 2, 5, 9');
    expect(where([1, 3, 5, 7, 9, 11, 13])).toBe('строки 1, 3, 5, 7, 9 и ещё 2');
  });

  it('у каждой причины свои слова; поле названо подписью', () => {
    expect(unplacedReason(head('Номер', '1', 'occupied', null, 'Номер счёта'))).toBe('поле «Номер счёта» уже заполнено — прочитанное предложено под полем');
    expect(unplacedReason(head('Сумма', 'x', 'unparsed', null, 'Сумма'))).toBe('значение не разобрано — предложено под полем «Сумма»');
    expect(unplacedReason(head('Код', 'X-1', 'guard', 'код — «Д-» и цифры', 'Код договора')))
      .toBe('охрана записи: код — «Д-» и цифры — предложено под полем «Код договора»');
    expect(unplacedReason(head('НомерСчёта', '1', 'duplicate-key', 'Номер', 'Номер счёта')))
      .toBe('повторяет ключ «Номер» — поле «Номер счёта» уже заполнено им');
    expect(unplacedReason(head('КодКлиента', '1', 'no-field'))).toBe('у счёта нет поля с таким ключом');
  });
});
