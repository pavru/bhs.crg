import { describe, it, expect } from 'vitest';
import { showsArrayTable } from './constants';
import type { SchemaField } from '@/shared/api/schema';

const f = (key: string, type: SchemaField['type']): SchemaField => ({ key, title: key, type, required: false });

/**
 * Табличный ввод предлагается только ОДНОРОДНЫМ массивам (issue #748).
 *
 * У union'а подполя — это варианты, и колонка на каждый читается как «и», а не «одно из»:
 * заполнив две в одной строке, человек получает два ключа и ломает инвариант #320 без единого
 * предупреждения. Тест держит именно это: снятие условия обязано ломать сборку, а не тихо
 * возвращать возможность испортить данные.
 */
describe('showsArrayTable', () => {
  const plain = [f('Наименование', 'string'), f('Количество', 'number')];

  it('обычному массиву таблица предлагается', () => {
    expect(showsArrayTable(plain, false)).toBe(true);
  });

  it('union-массиву — нет, даже когда подполя табличных типов', () => {
    // Живой случай: «Кабельная линия» — пять complex-вариантов, все табличного типа.
    const variants = [f('ЭО', 'complex'), f('ЭОН', 'complex'), f('Слаботочная', 'complex')];
    expect(showsArrayTable(variants, true)).toBe(false);
    expect(showsArrayTable(plain, true)).toBe(false);
  });

  it('массиву без табличных подполей — нет и без union', () => {
    expect(showsArrayTable([f('Файл', 'file'), f('Схемы', 'doc-array')], false)).toBe(false);
  });
});
