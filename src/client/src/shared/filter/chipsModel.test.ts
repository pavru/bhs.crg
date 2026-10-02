import { describe, it, expect } from 'vitest';
import type { FilterCondition, FilterGroup } from '@/shared/api/types';
import { tableRequest } from '@/shared/api/tables';
import {
  chipProblem, chipsView, chipText, conditionEntered, fromChips, offeredColumns, offeredCondition, withChip,
  withoutChip,
} from './chipsModel';
import { withColumn, type FilterColumn } from './rowFilterModel';

/**
 * Чипы отбора (задача G1d, issue #1091): что чип говорит, когда отбор чипами не показать и что
 * происходит с самим отбором при снятии чипа. Проверяется здесь, без экрана.
 */

// Колонки так, как их отдаёт таблица счетов: условие стоит на ключе, человеку — заголовок.
const columns: FilterColumn[] = [
  { name: 'Номер', label: 'Номер счёта', kind: 'text', operators: ['eq', 'neq', 'contains', 'in', 'is_empty'] },
  { name: 'Итого', label: 'Сумма к оплате', kind: 'number', operators: ['eq', 'gt', 'lt', 'between', 'is_null'] },
  { name: 'Срок', label: 'Оплатить до', kind: 'date', operators: ['eq', 'lt', 'between', 'is_null', 'is_not_null'] },
  { name: 'СрокПросрочен', label: 'Просрочен', kind: 'boolean', operators: ['eq', 'neq'] },
  { name: 'СостояниеОплаты', label: 'Состояние оплаты', kind: 'choice', operators: ['eq', 'neq', 'in', 'not_in'],
    options: ['Не оплачен', 'Частично оплачен', 'Оплачен'] },
  { name: 'ВТомЧислеНДС', label: 'В том числе НДС', kind: 'number', operators: ['eq'], unavailable: 'нет права на суммы' },
];

const cond = (patch: Partial<FilterCondition>): FilterCondition =>
  ({ type: 'condition', column: 'Итого', op: 'eq', value: '5', ...patch });

const and = (...children: FilterGroup['children']): FilterGroup => ({ type: 'group', logic: 'and', children });

describe('chipsView — когда отбор читается рядом чипов', () => {
  it('отбора нет — чипов нет', () => {
    expect(chipsView(null)).toEqual({ mode: 'chips', conditions: [] });
  });

  it('условия, связанные «И», — по чипу на условие, в том же порядке', () => {
    const a = cond({}), b = cond({ column: 'Срок', op: 'is_null', value: undefined });
    expect(chipsView(and(a, b))).toEqual({ mode: 'chips', conditions: [a, b] });
  });

  it('«ИЛИ» и вложенная группа чипами не подменяются — отбор назван сложным, с числом условий', () => {
    const a = cond({}), b = cond({ value: '6' });
    expect(chipsView({ type: 'group', logic: 'or', children: [a, b] })).toEqual({ mode: 'complex', count: 2 });
    expect(chipsView(and(a, and(b, cond({ value: '7' }))))).toEqual({ mode: 'complex', count: 3 });
  });

  it('«ИЛИ» от одного условия — то же условие: логика одиночки ни на что не влияет', () => {
    const a = cond({});
    expect(chipsView({ type: 'group', logic: 'or', children: [a] })).toEqual({ mode: 'chips', conditions: [a] });
  });
});

describe('chipText — чип называет колонку, оператор и значение', () => {
  it('колонка названа заголовком, а не ключом условия', () => {
    expect(chipText(cond({ column: 'Срок', op: 'is_null', value: undefined }), columns)).toBe('Оплатить до: не определено');
  });

  it('значение показано так, как его читает человек: дата — днём, флаг — словом', () => {
    expect(chipText(cond({ column: 'Срок', op: 'between', value: undefined, values: ['2026-10-01', '2026-10-31'] }), columns))
      .toBe('Оплатить до: 01.10.2026 — 31.10.2026');
    expect(chipText(cond({ column: 'СрокПросрочен', value: 'true' }), columns)).toBe('Просрочен: да');
    expect(chipText(cond({ column: 'Итого', op: 'gt', value: '1000' }), columns)).toBe('Сумма к оплате > 1000');
    expect(chipText(cond({ column: 'Номер', op: 'contains', value: 'СЧ' }), columns)).toBe('Номер счёта содержит «СЧ»');
  });

  it('список назван значениями; длинный — первыми тремя и числом остальных', () => {
    expect(chipText(cond({ column: 'СостояниеОплаты', op: 'in', value: undefined, values: ['Не оплачен', 'Частично оплачен'] }), columns))
      .toBe('Состояние оплаты: Не оплачен, Частично оплачен');
    expect(chipText(cond({ column: 'СостояниеОплаты', op: 'not_in', value: undefined, values: ['Оплачен'] }), columns))
      .toBe('Состояние оплаты — кроме: Оплачен');
    expect(chipText(cond({ column: 'Номер', op: 'in', value: undefined, values: ['1', '2', '3', '4', '5'] }), columns))
      .toBe('Номер счёта: «1», «2», «3» и ещё 2');
  });

  it('у каждого оператора свой текст — два разных условия не читаются одинаково', () => {
    const ops = ['eq', 'neq', 'contains', 'not_contains', 'starts_with', 'ends_with', 'gt', 'gte', 'lt', 'lte',
      'is_empty', 'is_not_empty', 'is_null', 'is_not_null'] as const;
    const texts = ops.map(op => chipText(cond({ column: 'Номер', op, value: 'а' }), columns));
    expect(new Set(texts).size).toBe(ops.length);
  });

  it('негодное значение чип не приукрашивает — показывает как есть', () => {
    expect(chipText(cond({ column: 'Срок', op: 'lt', value: '01.05.2026' }), columns)).toBe('Оплатить до < «01.05.2026»');
    expect(chipText(cond({ column: 'Итого', op: 'gt', value: '' }), columns)).toBe('Сумма к оплате > …');
  });
});

describe('колонка-выбор не принимает произвольную строку', () => {
  it('слово вне перечня названо негодным — у самого чипа', () => {
    expect(chipProblem(cond({ column: 'СостояниеОплаты', value: 'Оплочен' }), columns))
      .toBe('значения «Оплочен» в перечне колонки нет');
    expect(chipProblem(cond({ column: 'СостояниеОплаты', op: 'in', value: undefined, values: ['Оплачен', 'Чепуха'] }), columns))
      .toBe('значения «Чепуха» в перечне колонки нет');
    expect(chipProblem(cond({ column: 'СостояниеОплаты', value: 'Оплачен' }), columns)).toBeNull();
  });

  it('перечень сверяется буква в букву — как на сервере', () => {
    expect(chipProblem(cond({ column: 'СостояниеОплаты', value: 'оплачен' }), columns)).not.toBeNull();
  });

  it('операторов текста у выбора нет: «содержит» — это уже набранная строка', () => {
    expect(chipProblem(cond({ column: 'СостояниеОплаты', op: 'contains', value: 'Опл' }), columns)).toContain('не применяется');
  });

  it('строка, набранная у текстовой колонки, в колонку-выбор не переезжает', () => {
    const moved = withColumn(cond({ column: 'Номер', value: 'что угодно' }), 'СостояниеОплаты', columns);
    expect(moved.value).toBe('');
  });

  it('значение не выбрано — условие названо незаданным, а не «равно пустоте»', () => {
    expect(chipProblem(cond({ column: 'СостояниеОплаты', value: '' }), columns)).toBe('значение не задано');
  });
});

describe('chipProblem — битый отбор виден чипом с причиной', () => {
  it('колонка, которой в таблице нет, названа', () => {
    expect(chipProblem(cond({ column: 'УдалённоеПоле' }), columns)).toContain('такой колонки в таблице нет');
  });

  it('пока колонки не пришли, о колонке сказать нечего — это не «колонки нет»', () => {
    expect(chipProblem(cond({ column: 'Итого' }), [])).toBeNull();
  });

  it('закрытая колонка названа своей причиной', () => {
    expect(chipProblem(cond({ column: 'ВТомЧислеНДС' }), columns)).toContain('нет права на суммы');
  });
});

describe('снятие чипа меняет запрос, а не только вид', () => {
  const overdue = cond({ column: 'СрокПросрочен', value: 'true' });
  const unpaid = cond({ column: 'СостояниеОплаты', value: 'Не оплачен' });
  const filterOf = (request: ReturnType<typeof tableRequest>) =>
    request.method === 'get' ? request.params.filter : request.body.filter;

  it('снятый чип уходит из отбора, который получает сервер', () => {
    const both = tableRequest('costs.invoices', { filter: fromChips([overdue, unpaid]) });
    const one = tableRequest('costs.invoices', { filter: withoutChip([overdue, unpaid], 0) });

    expect(filterOf(both)).toContain('СрокПросрочен');
    expect(filterOf(one)).not.toContain('СрокПросрочен');
    expect(JSON.parse(String(filterOf(one)))).toEqual(and(unpaid));
  });

  it('снят последний чип — в запросе нет отбора вовсе, а не пустая группа', () => {
    const none = tableRequest('costs.invoices', { filter: withoutChip([overdue], 0) });

    expect(none.method).toBe('get');
    expect(none.method === 'get' && 'filter' in none.params).toBe(false);
  });

  it('новый чип встаёт в конец, правка — на своё место', () => {
    expect(withChip([overdue], unpaid)).toEqual(and(overdue, unpaid));
    const edited = cond({ column: 'СрокПросрочен', value: 'false' });
    expect(withChip([overdue, unpaid], edited, 0)).toEqual(and(edited, unpaid));
  });

  it('длинный отбор уходит телом запроса — адрес упёрся бы в потолок длины', () => {
    const many = cond({ column: 'Номер', op: 'in', value: undefined,
      values: Array.from({ length: 200 }, (_, i) => `Поставщик номер ${i}`) });
    const request = tableRequest('costs.invoices', { filter: fromChips([many]) });

    expect(request.method).toBe('post');
    expect(request.url).toBe('/tables/costs.invoices/query');
  });
});

/** Места под условие, которые отбор предлагает готовыми (задача G4, issue #1097). */
describe('offeredCondition — предложенное место под условие', () => {
  it('у даты место сразу «между»: в реестре оно называется «период»', () => {
    expect(offeredCondition('Срок', columns)).toEqual({ type: 'condition', column: 'Срок', op: 'between', values: ['', ''] });
  });

  it('у остальных — первый годный оператор колонки и пустое значение', () => {
    expect(offeredCondition('СостояниеОплаты', columns)).toEqual(
      { type: 'condition', column: 'СостояниеОплаты', op: 'eq', value: '' });
    expect(offeredCondition('Номер', columns).op).toBe('eq');
  });

  it('место — ещё не условие: с пустым значением оно названо негодным, а не отбирает всё подряд', () => {
    expect(chipProblem(offeredCondition('СостояниеОплаты', columns), columns)).not.toBeNull();
  });
});

describe('offeredColumns — какие места показать', () => {
  const suggested = ['Срок', 'Номер', 'СостояниеОплаты'];

  it('отбора нет — места стоят все, в порядке представления', () => {
    expect(offeredColumns(suggested, columns, [])).toEqual(suggested);
  });

  it('по колонке уже стоит условие — места под неё нет: оно правится чипом', () => {
    const period = cond({ column: 'Срок', op: 'between', value: undefined, values: ['2026-09-01', '2026-09-30'] });
    expect(offeredColumns(suggested, columns, [period])).toEqual(['Номер', 'СостояниеОплаты']);
  });

  it('колонки, которой у таблицы нет, местом не предложить', () => {
    expect(offeredColumns(['Номер', 'НетТакой'], columns, [])).toEqual(['Номер']);
  });

  it('представление мест не называет — их нет', () => {
    expect(offeredColumns(undefined, columns, [])).toEqual([]);
  });

  it('колонка закрыта правом — места под неё нет: оно вело бы прямо в отказ', () => {
    expect(offeredColumns(['Номер', 'ВТомЧислеНДС'], columns, [])).toEqual(['Номер']);
  });
});

describe('conditionEntered — стало ли место условием', () => {
  it('у только что открытого места значения нет — у текста, выбора и периода одинаково', () => {
    expect(conditionEntered(offeredCondition('Номер', columns))).toBe(false);
    expect(conditionEntered(offeredCondition('СостояниеОплаты', columns))).toBe(false);
    expect(conditionEntered(offeredCondition('Срок', columns))).toBe(false);
  });

  it('значение введено — условие есть; у периода хватает одной границы, остальное скажет подсказка', () => {
    expect(conditionEntered(cond({ column: 'Номер', value: 'А-1' }))).toBe(true);
    expect(conditionEntered(cond({ column: 'Срок', op: 'between', value: undefined, values: ['2026-09-01', ''] }))).toBe(true);
    expect(conditionEntered(cond({ column: 'Номер', op: 'in', value: undefined, values: ['А-1'] }))).toBe(true);
    expect(conditionEntered(cond({ column: 'Номер', op: 'in', value: undefined, values: [] }))).toBe(false);
  });

  it('оператору без значения вводить нечего: «пусто» — условие сразу', () => {
    expect(conditionEntered(cond({ column: 'Номер', op: 'is_empty', value: undefined }))).toBe(true);
    expect(conditionEntered(cond({ column: 'Срок', op: 'is_null', value: undefined }))).toBe(true);
  });
});
