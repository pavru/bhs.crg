import { describe, it, expect } from 'vitest';
import type { FilterCondition, FilterGroup } from '@/shared/api/types';
import { FILTER_OP_LABELS } from '@/shared/api/types';
import { cleanFilterNode } from '@/shared/api/datasetHelpers';
import {
  UNTYPED_OPS, conditionProblem, filterColumns, fromDraft, newCondition, newGroup, opArity,
  opLabel, operatorsFor, pruneDraft, toDraft, valueFits, withColumn, withOperator,
  type DraftGroup, type FilterColumn,
} from './rowFilterModel';

/**
 * Операторы и значение условия по виду колонки (issue #1133). Диалог отбора — единственный редактор
 * дерева условий, и проверяется он здесь, без экрана: что колонке предлагается, в каком виде
 * значение уходит на сервер и какое условие названо негодным ДО сохранения.
 */

// Колонки так, как их отдаёт сервер у источника на таблице модуля: с видом и своими операторами.
const NUMBER_OPS = ['eq', 'neq', 'gt', 'gte', 'lt', 'lte', 'between', 'in', 'not_in', 'is_null', 'is_not_null'];
const columns: FilterColumn[] = [
  { name: 'Номер', kind: 'text', operators: ['eq', 'neq', 'contains', 'in', 'is_empty'] },
  { name: 'Итого', kind: 'number', operators: NUMBER_OPS },
  { name: 'Срок', kind: 'date', operators: NUMBER_OPS },
  { name: 'Оплачен', kind: 'boolean', operators: ['eq', 'neq', 'is_null', 'is_not_null'] },
  { name: 'ВТомЧислеНДС', unavailable: 'нет права на суммы' },
  { name: 'Расчёт' },
];

const cond = (patch: Partial<FilterCondition>): FilterCondition =>
  ({ type: 'condition', column: 'Итого', op: 'eq', value: '5', ...patch });

describe('operatorsFor', () => {
  it('колонке с видом — ровно операторы, присланные сервером', () => {
    expect(operatorsFor(columns[1])).toEqual(NUMBER_OPS);
    expect(operatorsFor(columns[1])).not.toContain('contains');
  });

  it('колонке без вида и колонке, вписанной текстом, — общий список с новыми узлами', () => {
    expect(operatorsFor(columns[5])).toBe(UNTYPED_OPS);
    expect(operatorsFor(undefined)).toBe(UNTYPED_OPS);
    expect(UNTYPED_OPS).toEqual(expect.arrayContaining(['between', 'in', 'not_in', 'contains']));
    // «Не определено» у колонки без вида — то же «пусто» под вторым именем: дважды не предлагаем.
    expect(UNTYPED_OPS).not.toContain('is_null');
  });

  it('у каждого предлагаемого оператора есть подпись, а незнакомый показывается кодом', () => {
    for (const op of [...UNTYPED_OPS, ...NUMBER_OPS]) expect(opLabel(op)).toBe(FILTER_OP_LABELS[op as never]);
    expect(opLabel('new_operator')).toBe('new_operator');
  });
});

describe('withOperator', () => {
  it('«между» держит две границы в values, и введённое не теряется', () => {
    const between = withOperator(cond({ value: '5' }), 'between');
    expect(between).toEqual({ type: 'condition', column: 'Итого', op: 'between', values: ['5', ''] });
    expect('value' in between).toBe(false);
  });

  it('«в списке» держит список в values, без пустых мест', () => {
    expect(withOperator(cond({ value: '5' }), 'in').values).toEqual(['5']);
    expect(withOperator(cond({ value: '' }), 'in').values).toEqual([]);
  });

  it('обратно к одному значению — первое из введённых, в value', () => {
    const one = withOperator(cond({ op: 'between', value: undefined, values: ['5', '10'] }), 'gt');
    expect(one).toEqual({ type: 'condition', column: 'Итого', op: 'gt', value: '5' });
  });

  it('у «между» с одной верхней границей введённое — она: пустое «от» значением не считается', () => {
    const upper = cond({ op: 'between', value: undefined, values: ['', '10'] });
    expect(withOperator(upper, 'lte')).toMatchObject({ op: 'lte', value: '10' });
    expect(withOperator(upper, 'in').values).toEqual(['10']);
  });

  it('оператор без значения не несёт ни value, ни values', () => {
    const none = withOperator(cond({ op: 'in', value: undefined, values: ['5'] }), 'is_null');
    expect(none).toEqual({ type: 'condition', column: 'Итого', op: 'is_null' });
  });

  it('значение никогда не лежит в value и values разом — такое условие сервер отвергает', () => {
    for (const op of NUMBER_OPS) {
      const next = withOperator(cond({ value: '5' }), op as never);
      expect('value' in next && 'values' in next).toBe(false);
    }
  });

  it('число значений оператора', () => {
    expect([opArity('eq'), opArity('between'), opArity('not_in'), opArity('is_not_null'), opArity('is_empty')])
      .toEqual(['one', 'two', 'list', 'none', 'none']);
  });
});

describe('withColumn', () => {
  it('оператор, не подходящий новой колонке, заменяется первым подходящим', () => {
    const moved = withColumn(cond({ column: 'Номер', op: 'contains', value: '15' }), 'Итого', columns);
    expect(moved).toMatchObject({ column: 'Итого', op: 'eq', value: '15' });
  });

  it('значение, которое у новой колонки не разбирается, не переносится — поле появится своего вида', () => {
    expect(withColumn(cond({ value: '5' }), 'Срок', columns)).toMatchObject({ column: 'Срок', op: 'eq', value: '' });
    expect(withColumn(cond({ column: 'Номер', op: 'contains', value: 'мтр' }), 'Итого', columns).value).toBe('');
    // Границы остаются на своих местах, список теряет только негодное.
    expect(withColumn(cond({ op: 'between', value: undefined, values: ['5', '2026-05-31'] }), 'Срок', columns).values)
      .toEqual(['', '2026-05-31']);
    expect(withColumn(cond({ op: 'in', value: undefined, values: ['5', '2026-05-31'] }), 'Срок', columns).values)
      .toEqual(['2026-05-31']);
    // У текста и у колонки без вида годится любое значение.
    expect(withColumn(cond({ value: '5' }), 'Расчёт', columns).value).toBe('5');
  });

  it('«пусто» переезжает тем же вопросом под именем новой колонки', () => {
    expect(withColumn(cond({ column: 'Номер', op: 'is_empty', value: undefined }), 'Срок', columns).op).toBe('is_null');
    expect(withColumn(cond({ op: 'is_not_null', value: undefined }), 'Расчёт', columns).op).toBe('is_not_empty');
  });

  it('границы «между» при смене колонки местами не меняются', () => {
    const upper = cond({ op: 'between', value: undefined, values: ['', '10'] });
    expect(withColumn(upper, 'Срок', columns).values).toEqual(['', '']);
    expect(withColumn({ ...upper, column: 'Срок' }, 'Итого', columns).values).toEqual(['', '10']);
  });

  it('подходящий оператор остаётся', () => {
    expect(withColumn(cond({ op: 'gt' }), 'Срок', columns)).toMatchObject({ column: 'Срок', op: 'gt' });
    expect(withColumn(cond({ op: 'contains' }), 'Расчёт', columns)).toMatchObject({ column: 'Расчёт', op: 'contains' });
  });
});

describe('conditionProblem', () => {
  it('годное условие возражений не вызывает', () => {
    expect(conditionProblem(cond({}), columns)).toBeNull();
    expect(conditionProblem(cond({ column: 'Срок', op: 'between', value: undefined, values: ['2026-05-01', '2026-05-31'] }), columns)).toBeNull();
    expect(conditionProblem(cond({ column: 'Оплачен', value: 'true' }), columns)).toBeNull();
    expect(conditionProblem(cond({ op: 'is_null', value: undefined }), columns)).toBeNull();
  });

  it('оператор, не подходящий к виду, назван — сохранённое условие не прячется', () => {
    expect(conditionProblem(cond({ op: 'contains', value: '1' }), columns))
      .toBe('«содержит» к колонке вида «число» не применяется');
  });

  it('«пусто» и «не определено» годятся колонке любого вида — сервер принимает оба имени', () => {
    for (const op of ['is_empty', 'is_not_empty', 'is_null', 'is_not_null'] as const) {
      expect(conditionProblem(cond({ column: 'Срок', op, value: undefined }), columns)).toBeNull();
      expect(conditionProblem(cond({ column: 'Номер', op, value: undefined }), columns)).toBeNull();
    }
  });

  it('колонка без значений названа причиной', () => {
    expect(conditionProblem(cond({ column: 'ВТомЧислеНДС' }), columns)).toContain('нет права на суммы');
  });

  it('значение, которое сервер не разберёт, названо', () => {
    expect(conditionProblem(cond({ value: '1,5' }), columns)).toBe('значение «1,5» — не число');
    expect(conditionProblem(cond({ value: '' }), columns)).toBe('значение не задано');
    expect(conditionProblem(cond({ column: 'Срок', value: '01.05.2026' }), columns)).toBe('значение «01.05.2026» — не дата');
    expect(conditionProblem(cond({ op: 'between', value: undefined, values: ['5', ''] }), columns)).toBe('не заданы обе границы');
    expect(conditionProblem(cond({ op: 'in', value: undefined, values: [] }), columns)).toBe('список значений пуст');
  });

  it('у текста и у колонки без вида пустое значение законно — с пустой ячейкой сравнивают намеренно', () => {
    expect(conditionProblem(cond({ column: 'Номер', value: '' }), columns)).toBeNull();
    expect(conditionProblem(cond({ column: 'Расчёт', op: 'gt', value: '' }), columns)).toBeNull();
    expect(conditionProblem(cond({ column: 'Нет такой', op: 'contains', value: 'x' }), columns)).toBeNull();
  });

  it('условие без колонки — не ошибка: оно выбрасывается при сохранении', () => {
    expect(conditionProblem(cond({ column: '', op: 'in', value: undefined, values: [] }), columns)).toBeNull();
  });

});

describe('valueFits', () => {
  it('запись числа и даты — та же, что разбирает сервер', () => {
    expect(['110', '-5', '99.99'].every(v => valueFits('number', v))).toBe(true);
    expect(['1,5', '1e3', '12 шт', '.5', ''].some(v => valueFits('number', v))).toBe(false);
    expect(valueFits('date', '2026-05-01')).toBe(true);
    expect(valueFits('date', '2026-05-01T00:00:00')).toBe(false);
    // Дата обязана существовать: сервер разбирает её, а не сверяет с образцом.
    expect(['2026-02-30', '2026-13-01', '2026-00-10', '2025-02-29', '0000-01-01'].some(v => valueFits('date', v))).toBe(false);
    expect(['2024-02-29', '0099-12-31', '9999-12-31'].every(v => valueFits('date', v))).toBe(true);
    expect(valueFits('boolean', 'да')).toBe(false);
    expect(valueFits('text', '')).toBe(true);
    expect(valueFits(undefined, 'что угодно')).toBe(true);
  });
});

describe('filterColumns', () => {
  it('вычисляемые колонки идут следом, без вида; колонка источника с тем же именем остаётся своей', () => {
    const result = filterColumns(columns.slice(0, 2), ['Расчёт', 'Итого', 'Расчёт', '']);
    expect(result.map(c => c.name)).toEqual(['Номер', 'Итого', 'Расчёт']);
    expect(result[1].kind).toBe('number');
    expect(result[2].kind).toBeUndefined();
  });
});

describe('черновик диалога', () => {
  const saved: FilterGroup = {
    type: 'group', logic: 'or',
    children: [cond({}), { type: 'group', logic: 'and', children: [cond({ op: 'between', value: undefined, values: ['1', '2'] })] }],
  };

  it('у каждой строки свой ключ, и на сервер он не уходит', () => {
    const draft = toDraft(saved) as DraftGroup;
    const inner = draft.children[1] as DraftGroup;
    const keys = [draft.key, draft.children[0].key, inner.key, inner.children[0].key, newCondition().key, newGroup().key];
    expect(new Set(keys).size).toBe(keys.length);

    expect(fromDraft(draft)).toEqual(saved);
    expect(JSON.stringify(fromDraft({ ...draft, children: [...draft.children, newCondition()] }))).not.toContain('key');
  });

  it('условие без оператора — это «равно»: так его читает сервер', () => {
    const bare = { type: 'condition', column: 'Итого', value: '5' } as unknown as FilterCondition;
    const draft = toDraft({ type: 'group', logic: 'and', children: [bare] }) as DraftGroup;
    expect(draft.children[0]).toMatchObject({ column: 'Итого', op: 'eq', value: '5' });
    expect(conditionProblem(draft.children[0] as FilterCondition, columns)).toBeNull();
  });

  it('условие без колонки и группа без детей диалог не роняют', () => {
    const broken = {
      type: 'group', logic: 'and',
      children: [{ type: 'condition', op: 'eq', value: '1' }, { type: 'condition', column: null, op: 'eq' }, { type: 'group', logic: 'or' }],
    } as unknown as FilterGroup;
    const draft = toDraft(broken) as DraftGroup;
    expect(draft.children.slice(0, 2)).toMatchObject([{ column: '' }, { column: '' }]);
    expect(draft.children[2]).toMatchObject({ children: [] });
    for (const child of draft.children.slice(0, 2))
      expect(conditionProblem(child as FilterCondition, columns)).toBeNull();
  });

  // Сервер называет условие номером по ОТПРАВЛЕННОМУ дереву («условие 2»). Пустые строки на сервер
  // не уезжают — значит, и на экране их с момента сохранения быть не должно, иначе «условие 2»
  // оказалось бы третьей строкой, а второй — годное условие по той же колонке.
  it('перед отправкой с экрана уходит то, что не уедет: номера условий совпадают с серверными', () => {
    const between = { ...newCondition(), column: 'Итого', op: 'between' as const, values: ['80', '110'] };
    const contains = { ...newCondition(), column: 'Итого', op: 'contains' as const, value: '1' };
    const nested = { ...newCondition(), column: 'Номер', value: 'А' };
    const draft = newGroup([
      newCondition(),                                  // колонка не выбрана
      between,
      newGroup([newCondition()]),                      // группа, в которой после чистки пусто
      contains,
      newGroup([newCondition(), nested]),
    ]);

    const sent = pruneDraft(draft);

    // Ключи уцелевших строк прежние: строка помнит недобранное значение и вид поля.
    expect(sent.children.map(c => c.key)).toEqual([between.key, contains.key, draft.children[4].key]);
    expect((sent.children[2] as DraftGroup).children.map(c => c.key)).toEqual([nested.key]);
    // И это ровно то дерево, что уезжает на сервер.
    expect(fromDraft(sent)).toEqual(cleanFilterNode(fromDraft(draft)));
  });

  it('черновик из одних пустых строк остаётся пустой группой — диалогу есть что показать', () => {
    const sent = pruneDraft(newGroup([newCondition(), newGroup([newCondition()])]));
    expect(sent).toMatchObject({ type: 'group', children: [] });
    expect(cleanFilterNode(fromDraft(sent))).toBeNull();
  });
});
