import { describe, expect, it } from 'vitest';
import type { InvoiceReadinessGap, InvoiceView } from '@/shared/api/invoices';
import { isRequired, readinessAnchor, readinessItems } from './invoiceReadiness';

const view = (gaps: InvoiceReadinessGap[] | null | undefined): InvoiceView => ({
  id: 'i1',
  readiness: gaps === undefined ? undefined : { required: ['Номер', 'Плательщик'], gaps },
} as unknown as InvoiceView);

const all = { edit: true, allocate: true };

describe('readinessItems', () => {
  it('перечня нет — строки нет: счёт разобран либо отклонён', () => {
    expect(readinessItems(view(null), all)).toBeNull();
  });

  it('прежний сервер перечня не присылает — строки нет, а не «всё готово»', () => {
    expect(readinessItems(view(undefined), all)).toBeNull();
  });

  it('пустой перечень — всё готово: это не то же, что «перечня нет»', () => {
    expect(readinessItems(view([]), all)).toEqual([]);
  });

  it('каждый пункт ведёт к своему месту формы', () => {
    const items = readinessItems(view([
      { kind: 'field', label: 'плательщик', field: 'Плательщик' },
      { kind: 'lines', label: 'строки' },
      { kind: 'nomenclature', label: '3 строки без позиции', lines: [1, 2, 5] },
      { kind: 'reference', label: 'поставщик: записи нет в справочнике', field: 'Поставщик' },
      { kind: 'position', label: 'позиции нет в справочнике: строка 2', lines: [2] },
      { kind: 'catalog', label: 'справочник номенклатуры не заведён' },
      { kind: 'allocation', label: 'разноска' },
    ]), all)!;

    expect(items.map(i => i.target && readinessAnchor(i.target))).toEqual([
      'field:Плательщик', 'lines', 'lines', 'field:Поставщик', 'lines', 'lines', 'allocation',
    ]);
    expect(items.map(i => i.label)[2]).toBe('3 строки без позиции');
  });

  it('нет права разносить — пункт «разноска» виден, но не ведёт', () => {
    const items = readinessItems(view([
      { kind: 'field', label: 'плательщик', field: 'Плательщик' },
      { kind: 'allocation', label: 'разноска' },
    ]), { edit: true, allocate: false })!;

    expect(items.map(i => i.label)).toEqual(['плательщик', 'разноска']);
    expect(items[0].target).not.toBeNull();
    expect(items[1].target).toBeNull();
    expect(items[1].why).toContain('право разносить');
  });

  it('нет права вводить счета — пункты шапки и строк видны, но не ведут; разноска ведёт', () => {
    const items = readinessItems(view([
      { kind: 'field', label: 'плательщик', field: 'Плательщик' },
      { kind: 'nomenclature', label: '1 строка без позиции', lines: [1] },
      { kind: 'allocation', label: 'разноска' },
    ]), { edit: false, allocate: true })!;

    expect(items.map(i => i.target === null)).toEqual([true, true, false]);
  });

  it('неизвестный вид пункта не теряется — он ведёт к строкам', () => {
    const items = readinessItems(view([{ kind: 'новое условие', label: 'что-то ещё' }]), all)!;
    expect(items).toHaveLength(1);
    expect(items[0].label).toBe('что-то ещё');
  });
});

describe('isRequired', () => {
  it('по перечню сервера', () => {
    expect(isRequired(view([]), 'Плательщик')).toBe(true);
    expect(isRequired(view([]), 'Основание')).toBe(false);
  });

  it('у разобранного пометки остаются; прежний сервер — без пометок', () => {
    expect(isRequired(view(null), 'Номер')).toBe(true);
    expect(isRequired(view(undefined), 'Номер')).toBe(false);
  });
});
