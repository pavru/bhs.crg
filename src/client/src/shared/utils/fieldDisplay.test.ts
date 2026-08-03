import { describe, it, expect } from 'vitest';
import { formatFieldValue } from './fieldDisplay';
import type { SchemaField } from '@/shared/api/schema';
import type { EnumTypeDef, PrimitiveTypeDef } from '@/shared/api/types';

const f = (type: SchemaField['type'], typeId?: string): SchemaField =>
  ({ key: 'Поле', title: 'Поле', type, typeId, required: false });

const prim = (id: string, baseType: PrimitiveTypeDef['baseType'], constraints = {}): PrimitiveTypeDef =>
  ({ id, name: id, code: id, baseType, constraints, allowedTags: [], group: null,
     createdAt: '', updatedAt: '' });

const enumType: EnumTypeDef = {
  id: 'e1', name: 'Стадия', code: 'stage',
  values: [{ code: 'P', label: 'Проектная' }, { code: 'R', label: 'Рабочая' }],
  group: null, createdAt: '', updatedAt: '',
};

describe('formatFieldValue', () => {
  it('дату показывает по-русски, а не сырым ISO', () => {
    // Ровно issue #611: в сводке стояло «2023-08-10», в поле внутри — «10.08.2023».
    expect(formatFieldValue(f('date'), '2023-08-10')).toBe('10.08.2023');
  });

  it('учитывает точность примитивного date-типа', () => {
    const defs = { primitiveTypes: [
      prim('p1', 'date', { datePrecision: 'month' }),
      prim('p2', 'date'),
    ] };
    expect(formatFieldValue(f('primitive', 'p1'), '2026-07-01', defs)).toBe('07.2026');
    expect(formatFieldValue(f('primitive', 'p2'), '2026-07-01', defs)).toBe('01.07.2026');
  });

  it('незнакомый примитив показывает как есть — справочник мог не догрузиться', () => {
    expect(formatFieldValue(f('primitive', 'нет-такого'), '2026-07-01')).toBe('2026-07-01');
  });

  it('логическое значение — словом', () => {
    expect(formatFieldValue(f('boolean'), true)).toBe('Да');
    expect(formatFieldValue(f('boolean'), 'true')).toBe('Да');
    // Снятый флажок остаётся в сводке (раньше стоял «false») — «Нет» тоже сведение, а не пропуск.
    expect(formatFieldValue(f('boolean'), false)).toBe('Нет');
    expect(formatFieldValue(f('boolean'), 'false')).toBe('Нет'); // строку кладёт распознавание
  });

  it('перечисление — именем варианта, а не кодом', () => {
    expect(formatFieldValue(f('enum', 'e1'), 'R', { enumTypes: [enumType] })).toBe('Рабочая');
  });

  it('код вне вариантов остаётся кодом — иначе значение исчезло бы из сводки', () => {
    expect(formatFieldValue(f('enum', 'e1'), 'X', { enumTypes: [enumType] })).toBe('X');
  });

  it('строку и число не трогает', () => {
    expect(formatFieldValue(f('string'), 'АОСР-1')).toBe('АОСР-1');
    expect(formatFieldValue(f('number'), 12.5)).toBe('12.5');
  });

  it('пусто — пустая строка', () => {
    expect(formatFieldValue(f('date'), null)).toBe('');
    expect(formatFieldValue(f('string'), '')).toBe('');
  });

  it('неразобранную дату отдаёт как есть', () => {
    expect(formatFieldValue(f('date'), 'не дата')).toBe('не дата');
  });
});
