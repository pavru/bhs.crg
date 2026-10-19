import { describe, expect, it } from 'vitest';
import { invoiceLineWhere, plateMeta, supplierMatchWhere, waybillLineWhere } from './pickerLine';

describe('плашка строки в выборе позиции (#1517)', () => {
  it('называет строку и поставщика; без поставщика — без разделителя', () => {
    expect(invoiceLineWhere(3, 'ЭНЕРГОСФЕРА')).toBe('Строка 3 счёта · ЭНЕРГОСФЕРА');
    expect(invoiceLineWhere(3, '  ')).toBe('Строка 3 счёта');
    expect(invoiceLineWhere(1, null)).toBe('Строка 1 счёта');
    expect(waybillLineWhere(4)).toBe('Строка 4 накладной');
    expect(supplierMatchWhere('ООО «Свет»')).toBe('Соответствие поставщика «ООО «Свет»»');
    expect(supplierMatchWhere(null)).toBe('Соответствие поставщика');
  });

  it('пустые части не выводятся', () => {
    expect(plateMeta({ code: 'mcb10-1-06C', unit: 'шт' }, '12')).toBe('арт. mcb10-1-06C · 12 шт');
    expect(plateMeta({ code: '', unit: 'м' }, '18')).toBe('18 м');
    expect(plateMeta({ code: 'A1' }, '')).toBe('арт. A1');
    expect(plateMeta({ unit: 'шт' })).toBe('шт');
    expect(plateMeta({})).toBe('');
  });
});
