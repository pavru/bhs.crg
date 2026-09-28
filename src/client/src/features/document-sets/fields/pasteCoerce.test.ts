import { describe, it, expect } from 'vitest';
import { coerceScalar, parseBooleanCell, parseDateCell, rejectReason, type CoerceContext } from './pasteCoerce';
import type { SchemaField } from '@/shared/api/schema';
import type { EnumTypeDef, PrimitiveTypeDef } from '@/shared/api/types';

const prim = (id: string, baseType: PrimitiveTypeDef['baseType']) =>
  ({ id, name: id, code: id, baseType, constraints: {}, allowedTags: [] }) as unknown as PrimitiveTypeDef;

const enumDef = {
  id: 'enum-1', name: 'Единица', code: 'unit',
  values: [{ code: 'pcs', label: 'Штука' }, { code: 'm', label: 'Метр' }],
} as unknown as EnumTypeDef;

const ctx: CoerceContext = {
  locale: 'ru-RU',
  primitiveTypes: [prim('p-num', 'number'), prim('p-date', 'date'), prim('p-str', 'string')],
  enumTypes: [enumDef],
};

const field = (f: Partial<SchemaField>) => ({ key: 'k', title: 'Поле', ...f }) as SchemaField;

describe('parseDateCell', () => {
  it('принимает ДД.ММ.ГГГГ и ISO', () => {
    expect(parseDateCell('15.01.2026')).toBe('2026-01-15');
    expect(parseDateCell('5/1/2026')).toBe('2026-01-05');
    expect(parseDateCell('5-1-2026')).toBe('2026-01-05');
    expect(parseDateCell('2026-01-15')).toBe('2026-01-15');
  });

  it('непонятная дата — отказ, а не сырая строка в поле', () => {
    // Сырая строка выглядела бы пустым полем при непустых данных: DateInput показать её не может.
    expect(parseDateCell('15.01.26')).toBeNull();     // двузначный год: 1926-й или 2026-й?
    expect(parseDateCell('15 января 2026')).toBeNull();
    expect(parseDateCell('31.02.2026')).toBeNull();   // такой даты нет — подставлять 3 марта нельзя
    expect(parseDateCell('12.31.2026')).toBeNull();   // американский порядок
    expect(parseDateCell('')).toBeNull();
  });
});

describe('parseBooleanCell', () => {
  it('опознанные слова', () => {
    expect(parseBooleanCell('да')).toBe(true);
    expect(parseBooleanCell('ДА')).toBe(true);
    expect(parseBooleanCell('1')).toBe(true);
    expect(parseBooleanCell('нет')).toBe(false);
    expect(parseBooleanCell('false')).toBe(false);
    expect(parseBooleanCell('0')).toBe(false);
  });

  it('неопознанное — отказ, а не «нет»', () => {
    expect(parseBooleanCell('нет данных')).toBeNull();
    expect(parseBooleanCell('частично')).toBeNull();
    expect(parseBooleanCell('2 шт')).toBeNull();
    expect(parseBooleanCell('')).toBeNull();
  });
});

describe('coerceScalar', () => {
  it('число — по региональной настройке', () => {
    expect(coerceScalar(field({ type: 'number' }), '1.234,56', ctx)).toBe(1234.56);
    expect(coerceScalar(field({ type: 'number' }), '12 шт', ctx)).toBeNull();
  });

  it('пользовательский тип разбирается по своей базе, а не как строка', () => {
    const num = field({ type: 'primitive', typeId: 'p-num' });
    expect(coerceScalar(num, '1 234,56', ctx)).toBe(1234.56);
    expect(coerceScalar(num, '12 шт', ctx)).toBeNull();

    const date = field({ type: 'primitive', typeId: 'p-date' });
    expect(coerceScalar(date, '15.01.2026', ctx)).toBe('2026-01-15');
    expect(coerceScalar(date, 'когда-то', ctx)).toBeNull();

    expect(coerceScalar(field({ type: 'primitive', typeId: 'p-str' }), 'ГОСТ 1.2', ctx)).toBe('ГОСТ 1.2');
  });

  it('незагруженный справочник типов не превращается в отказ по данным', () => {
    expect(coerceScalar(field({ type: 'primitive', typeId: 'нет-такого' }), '12 шт', ctx)).toBe('12 шт');
  });

  it('список — по коду или подписи, хранится код', () => {
    const f = field({ type: 'enum', typeId: 'enum-1' });
    expect(coerceScalar(f, 'Штука', ctx)).toBe('pcs');
    expect(coerceScalar(f, 'pcs', ctx)).toBe('pcs');
    expect(coerceScalar(f, ' метр ', ctx)).toBe('m');
    expect(coerceScalar(f, 'килограмм', ctx)).toBeNull();
  });

  it('список без реестра — по options схемы', () => {
    const f = field({ type: 'enum', options: ['шт', 'м'] });
    expect(coerceScalar(f, 'ШТ', ctx)).toBe('шт');
    expect(coerceScalar(f, 'км', ctx)).toBeNull();
  });

  it('строка остаётся строкой', () => {
    expect(coerceScalar(field({ type: 'string' }), '12 шт', ctx)).toBe('12 шт');
    expect(coerceScalar(field({ type: 'text' }), ' многострочно ', ctx)).toBe(' многострочно ');
  });
});

describe('rejectReason', () => {
  it('называет, чем значение не оказалось', () => {
    expect(rejectReason(field({ type: 'number' }), ctx)).toBe('не число');
    expect(rejectReason(field({ type: 'date' }), ctx)).toContain('не дата');
    expect(rejectReason(field({ type: 'boolean' }), ctx)).toContain('«да»');
    expect(rejectReason(field({ type: 'enum' }), ctx)).toContain('списке');
    expect(rejectReason(field({ type: 'primitive', typeId: 'p-num' }), ctx)).toBe('не число');
    expect(rejectReason(field({ type: 'primitive', typeId: 'p-date' }), ctx)).toContain('не дата');
  });
});
