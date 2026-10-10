import { describe, expect, it } from 'vitest';
import type { IntakeKind } from '@/shared/api/nomenclatureIntake';
import {
  createBody, defaultKind, missingFields, prefill, prefillHint, similarBody,
} from './newNomenclature';

const kind = (patch: Partial<IntakeKind> = {}): IntakeKind => ({
  typeId: 't-1', code: 'Номенклатура', name: 'Номенклатура', refusals: [],
  fields: [
    { key: 'Наименование', title: 'Наименование', required: true, identity: true, options: null },
    { key: 'Производитель', title: 'Производитель', required: false, identity: true, options: null },
    { key: 'Артикул', title: 'Артикул', required: false, identity: true, options: null },
    {
      key: 'ЕдиницаИзмерения', title: 'Единица измерения', required: true, identity: false,
      options: [{ id: 'u-1', name: 'шт' }, { id: 'u-2', name: 'м' }],
    },
  ],
  ...patch,
});

describe('новая позиция номенклатуры из строки счёта', () => {
  it('вид по умолчанию — единственный годный; из двух выбирает человек', () => {
    const bad = kind({ typeId: 't-2', refusals: ['обязательны поля…'] });
    expect(defaultKind([kind(), bad])?.typeId).toBe('t-1');
    expect(defaultKind([kind(), kind({ typeId: 't-3' })])).toBeNull();
    expect(defaultKind([bad])).toBeNull();
  });

  it('из строки подставляются наименование, артикул и единица, которая есть в справочнике', () => {
    expect(prefill(kind(), { name: ' Кабель 3х2,5 ', code: 'RZ-2W', unit: 'ШТ.' })).toEqual({
      values: { Наименование: 'Кабель 3х2,5', Артикул: 'RZ-2W' },
      refs: { ЕдиницаИзмерения: 'u-1' },
    });
    // Единицы «упак» в справочнике нет — поле остаётся пустым, а не берёт первую попавшуюся.
    expect(prefill(kind(), { name: 'Кабель', unit: 'упак' }).refs).toEqual({});
    expect(prefill(kind(), {})).toEqual({ values: {}, refs: {} });
  });

  it('подставленное из счёта оговорено, набираемое с нуля — нет', () => {
    const k = kind();
    expect(prefillHint(k, k.fields[0], { name: 'Кабель' })).toContain('слова поставщика');
    expect(prefillHint(k, k.fields[2], { code: 'RZ-2W' })).toContain('внутренний код поставщика');
    expect(prefillHint(k, k.fields[1], { name: 'Кабель', code: 'RZ-2W' })).toBeNull();
    expect(prefillHint(k, k.fields[0], {})).toBeNull();
  });

  it('о похожих спрашивают полями ключа, и только когда есть название', () => {
    expect(similarBody(kind(), { Артикул: 'RZ-2W' })).toBeNull();
    expect(similarBody(kind(), { Наименование: ' Кабель ', Артикул: 'RZ-2W', Производитель: ' ' })).toEqual({
      typeId: 't-1', values: { Наименование: 'Кабель', Артикул: 'RZ-2W' },
    });
  });

  it('до создания названо, чего не хватает; пустые поля в запрос не едут', () => {
    expect(missingFields(kind(), { values: {}, refs: {} })).toEqual(['Наименование', 'Единица измерения']);
    const draft = { values: { Наименование: 'Кабель', Артикул: '  ' }, refs: { ЕдиницаИзмерения: 'u-2' } };
    expect(missingFields(kind(), draft)).toEqual([]);
    expect(createBody(kind(), draft)).toEqual({
      typeId: 't-1', values: { Наименование: 'Кабель' }, refs: { ЕдиницаИзмерения: 'u-2' },
    });
  });
});
