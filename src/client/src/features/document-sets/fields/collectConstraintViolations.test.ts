import { describe, it, expect } from 'vitest';
import { collectConstraintViolations } from './collectConstraintViolations';
import type { DocumentType, PrimitiveTypeDef } from '@/shared/api/types';
import type { SchemaField } from '@/shared/api/schema';

/**
 * Проверка ограничений по всему документу (issue #463). Раньше она вызывалась только для полей
 * верхнего уровня — так в поле «Цело число» и оказался «3.3».
 */

const INT = 'int-type';
const HIER = 'hier-type';
const WORK = 'work-type';
const ROUTE = 'route-type';

const primitives = [
  { id: INT, name: 'Цело число', code: 'Integer', baseType: 'number', constraints: { integer: true } },
  { id: HIER, name: 'Иерархический номер', code: 'Hier', baseType: 'string',
    constraints: { pattern: '^\\d+(?:\\.\\d+)*\\.?$' } },
] as unknown as PrimitiveTypeDef[];

const field = (key: string, type: string, typeId?: string): SchemaField =>
  ({ key, type, typeId, title: key } as unknown as SchemaField);

const docType = (id: string, fields: SchemaField[]): DocumentType =>
  ({ id, name: id, schema: { fields }, parentId: null } as unknown as DocumentType);

const types = [
  docType(WORK, [field('ПорядковыйНомер', 'primitive', INT)]),
  docType(ROUTE, [field('Длина', 'primitive', INT)]),
];

describe('collectConstraintViolations', () => {
  it('ловит нарушение внутри строки таблицы — то, чего не видела прежняя проверка', () => {
    const v = collectConstraintViolations(
      { Работы: [{ ПорядковыйНомер: 1 }, { ПорядковыйНомер: 3.3 }] },
      [field('Работы', 'array', WORK)], types, primitives);

    // Адрес обязателен: без него нарушение внутри строки не найти, не открыв её.
    expect(Object.keys(v)).toEqual(['Работы[1].ПорядковыйНомер']);
    expect(v['Работы[1].ПорядковыйНомер']).toMatch(/целое/i);
  });

  it('ловит нарушение внутри составного поля', () => {
    const v = collectConstraintViolations(
      { Трасса: { Длина: 4.5 } },
      [field('Трасса', 'complex', ROUTE)], types, primitives);

    expect(Object.keys(v)).toEqual(['Трасса.Длина']);
  });

  it('проверяет и верхний уровень, как прежде', () => {
    const v = collectConstraintViolations(
      { Номер: 'не число' }, [field('Номер', 'primitive', INT)], types, primitives);
    expect(Object.keys(v)).toEqual(['Номер']);
  });

  it('корректные значения молчат', () => {
    const v = collectConstraintViolations(
      { Работы: [{ ПорядковыйНомер: 10 }] },
      [field('Работы', 'array', WORK)], types, primitives);
    expect(v).toEqual({});
  });

  it('строковый паттерн: «3.10» проходит, «3,10» нет', () => {
    const f = [field('Номер', 'primitive', HIER)];
    expect(collectConstraintViolations({ Номер: '3.10' }, f, types, primitives)).toEqual({});
    expect(Object.keys(collectConstraintViolations({ Номер: '3,10' }, f, types, primitives))).toEqual(['Номер']);
  });

  it('пустые значения не проверяются — незаполненность это не нарушение формата', () => {
    expect(collectConstraintViolations({ Номер: null }, [field('Номер', 'primitive', INT)], types, primitives))
      .toEqual({});
  });

  it('расчётные поля пропускаются: претензия к значению — претензия к выражению', () => {
    const computed = { ...field('Номер', 'primitive', INT), computed: true } as SchemaField;
    expect(collectConstraintViolations({ Номер: 'мусор' }, [computed], types, primitives)).toEqual({});
  });

  it('неизвестный тип строки таблицы не роняет обход', () => {
    expect(collectConstraintViolations(
      { Работы: [{ Что: 1 }] }, [field('Работы', 'array', 'нет-такого')], types, primitives)).toEqual({});
  });
});
