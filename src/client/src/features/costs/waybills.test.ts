import { describe, expect, it } from 'vitest';
import type { WaybillView } from '@/shared/api/waybills';
import {
  emptyLine, headerPayload, isDirty, linesPayload, linesWord, quantityInput, toDraft, unmatchedCount,
  unmatchedNote, withoutBlanks,
} from './waybills';

const view: WaybillView = {
  id: 'w1', version: '7', number: 'РН-7', issuedOn: '2026-09-03', warehouse: 'Основной', constructionId: 'c1',
  constructionName: 'ЖК «Север»', constructionLost: false, receivedBy: null, note: null,
  state: 'Draft', postedAt: null,
  lines: [
    { id: 'l1', ordinal: 1, nomenclatureId: 'n1', nomenclatureName: 'Кабель', nomenclatureLost: false,
      sourceText: 'Кабель ВВГ', unit: 'м', quantity: 12.5, note: null },
    { id: 'l2', ordinal: 2, nomenclatureId: null, nomenclatureName: null, nomenclatureLost: false,
      sourceText: 'Хомут', unit: 'шт', quantity: 4, note: null },
  ],
  totals: { count: 2, unmatched: 1 },
};

describe('форма накладной', () => {
  it('пустое поле уезжает как null, а не как пустая строка', () => {
    const header = headerPayload({ ...toDraft(view), warehouse: '  ', receivedBy: '' });
    expect(header.warehouse).toBeNull();
    expect(header.receivedBy).toBeNull();
    expect(header.construction).toBe('c1');
  });

  it('пустая заготовка строки на сервер не едет и правкой не считается', () => {
    const draft = toDraft(view);
    draft.lines.push(emptyLine());

    expect(linesPayload(draft)).toHaveLength(2);
    expect(isDirty(draft, view)).toBe(false);
    expect(unmatchedCount(draft)).toBe(1);
  });

  it('перед сохранением пустые заготовки уходят и с экрана: номера строк совпадают с отказом сервера', () => {
    const draft = toDraft(view);
    draft.lines.unshift(emptyLine());

    const sent = withoutBlanks(draft);
    expect(sent.lines.map(line => line.id)).toEqual(['l1', 'l2']);
    // Чистая форма возвращается той же ссылкой: лишнего рендера и сброса курсора нет.
    expect(withoutBlanks(sent)).toBe(sent);
  });

  it('то же количество, набранное иначе, — не правка', () => {
    const draft = toDraft(view);
    expect(draft.lines[0].quantity).toBe('12,5');

    draft.lines[0].quantity = '12.50';
    expect(isDirty(draft, view)).toBe(false);

    draft.lines[0].quantity = '13';
    expect(isDirty(draft, view)).toBe(true);
  });

  it('строка едет со ссылкой на позицию и с количеством как набрано', () => {
    const [first, second] = linesPayload(toDraft(view));
    expect(first).toMatchObject({ id: 'l1', nomenclature: { $ref: 'catalog', entryId: 'n1' }, quantity: '12,5' });
    expect(second.nomenclature).toBeNull();
  });

  it('количество в поле — с запятой и без дописанных нулей', () => {
    expect(quantityInput(null)).toBe('');
    expect(quantityInput(100)).toBe('100');
    expect(quantityInput(0.125)).toBe('0,125');
  });
});

describe('оговорка о несопоставленных строках', () => {
  it('склоняет слово по числу', () => {
    expect([1, 2, 4, 5, 11, 12, 21, 22, 25, 111].map(linesWord))
      .toEqual(['строка', 'строки', 'строки', 'строк', 'строк', 'строк', 'строка', 'строки', 'строк', 'строк']);
  });

  it('называет число и говорит, что делать, — по-разному у черновика и у проведённой', () => {
    expect(unmatchedNote(0, false)).toBeNull();
    expect(unmatchedNote(4, false)).toContain('Не сопоставлено: 4 строки');
    expect(unmatchedNote(4, false)).toContain('Провести накладную можно');
    expect(unmatchedNote(1, true)).toContain('проводить заново не нужно');
  });
});
