import { describe, it, expect } from 'vitest';
import { defaultColumnKeys, tableFilterColumns, tableRequest, type TableColumn, type TableData } from '@/shared/api/tables';
import {
  cellText, gridColumns, gridState, hiddenByRight, hiddenCountText, pageCount, plural, shownOf,
} from './tableCells';

/** Таблица модуля → сетка и отбор (задачи G1d и G1e, issue #1091, #1092). */

const column = (patch: Partial<TableColumn>): TableColumn => ({
  key: 'Номер', label: 'Номер счёта', kind: 'text', operators: ['eq'], system: true,
  unavailable: null, reason: null, dependsOnFilter: false, onDemand: false, note: null, options: null, requires: null, ...patch,
});

const table = (patch: Partial<TableData>): TableData => ({
  address: 'costs.invoices', title: 'Счета на оплату', grain: 'счёт', boundary: '', columns: [], rows: [],
  state: null, count: 0, offset: 0, limit: 200, totals: null, keys: null, ...patch,
});

const amounts = (key: string, label: string) => column({
  key, label, kind: 'number', unavailable: 'no-right', reason: 'нет права на суммы', requires: 'costs.invoice.read',
});

describe('gridColumns', () => {
  it('число — вправо; подпись смысла под отбором — в заголовке', () => {
    const [amount] = gridColumns([column({ key: 'СуммаПоОтбору', label: 'Сумма', kind: 'number', note: 'доля: Комарова 36' })]);
    expect(amount).toMatchObject({ label: 'Сумма (доля: Комарова 36)', align: 'right' });
  });

  it('колонка, закрытая правом, в сетку не идёт — о ней говорит строка над таблицей', () => {
    const grid = gridColumns([column({}), amounts('Итого', 'Сумма к оплате')]);
    expect(grid.map(c => c.key)).toEqual(['Номер']);
  });

  it('колонка, которой нет в типе, остаётся — с причиной; незнакомый код колонку не роняет', () => {
    expect(gridColumns([column({ unavailable: 'removed' })])[0].unavailable).toBe('removed');
    expect(gridColumns([column({ unavailable: 'module-off' })])[0].unavailable).toBeNull();
  });
});

describe('hiddenByRight — строка над таблицей называет число колонок и код права', () => {
  const columns = [column({}), amounts('Итого', 'Сумма к оплате'), amounts('ВТомЧислеНДС', 'В том числе НДС')];

  it('колонки одного права — одной записью: число, причина, код, названия', () => {
    expect(hiddenByRight(columns, null)).toEqual([{
      count: 2, reason: 'нет права на суммы', requires: 'costs.invoice.read',
      labels: ['Сумма к оплате', 'В том числе НДС'],
    }]);
  });

  it('у кого прав хватает, сказать нечего — строки над таблицей нет', () => {
    expect(hiddenByRight([column({}), column({ key: 'Итого' })], null)).toEqual([]);
  });

  it('считаются только колонки представления: убранная самим человеком не «скрыта»', () => {
    expect(hiddenByRight(columns, ['Номер', 'Итого'])[0]).toMatchObject({ count: 1, labels: ['Сумма к оплате'] });
    expect(hiddenByRight(columns, ['Номер'])).toEqual([]);
  });

  it('разные права — разными записями', () => {
    const mixed = [...columns, column({
      key: 'Маржа', label: 'Маржа', unavailable: 'no-right', reason: 'нет права на отчёты', requires: 'costs.report.read',
    })];
    expect(hiddenByRight(mixed, null).map(g => g.requires)).toEqual(['costs.invoice.read', 'costs.report.read']);
  });

  it('число согласовано со словом', () => {
    expect(hiddenCountText(1)).toBe('1 колонка скрыта');
    expect(hiddenCountText(3)).toBe('3 колонки скрыты');
    expect(hiddenCountText(5)).toBe('5 колонок скрыто');
    expect(plural(11, 'а', 'б', 'в')).toBe('в');
    expect(plural(21, 'а', 'б', 'в')).toBe('а');
  });
});

describe('cellText', () => {
  it('дата — днём, флаг — словом, перечень — через запятую', () => {
    expect(cellText('2026-10-03', 'date')).toBe('03.10.2026');
    expect(cellText(false, 'boolean')).toBe('нет');
    expect(cellText(['Комарова 36', 'Склад'], 'list')).toBe('Комарова 36, Склад');
    expect(cellText('СЧ-1', 'text')).toBe('СЧ-1');
  });
});

describe('gridState — пустая выдача под отбором и без него отвечает разное', () => {
  it('под отбором — «отбор ничего не нашёл», без отбора — «строк нет»', () => {
    expect(gridState(table({}), true)).toBe('filtered-out');
    expect(gridState(table({}), false)).toBe('no-data');
  });

  it('выключенный модуль перекрывает оба', () => {
    expect(gridState(table({ state: 'module-off' }), true)).toBe('module-off');
  });
});

describe('shownOf — страница названа числами, а не обрезана молча', () => {
  it('всё на одной странице — число строк', () => {
    expect(shownOf(table({ rows: [{}, {}], count: 2 }))).toBe('Строк: 2');
  });

  it('страница меньше отбора — какие строки показаны и сколько их всего', () => {
    expect(shownOf(table({ rows: [{}, {}], count: 1340 }))).toMatch(/^Строки 1–2 из 1.340$/);
    expect(shownOf(table({ rows: [{}, {}], count: 1340, offset: 200 }))).toMatch(/^Строки 201–202 из 1.340$/);
  });

  it('страница за концом отбора — так и сказано, а не «строк нет»', () => {
    expect(shownOf(table({ rows: [], count: 7, offset: 200 }))).toBe('Строк в отборе: 7, на этой странице их нет');
  });

  it('страниц — сколько нужно на весь отбор; у пустого отбора одна', () => {
    expect(pageCount(60, 50)).toBe(2);
    expect(pageCount(50, 50)).toBe(1);
    expect(pageCount(0, 50)).toBe(1);
  });
});

describe('tableFilterColumns', () => {
  it('условие встанет на ключ, человеку — заголовок; перечень и операторы — как прислал сервер', () => {
    const [state] = tableFilterColumns([column({
      key: 'СостояниеОплаты', label: 'Состояние оплаты', kind: 'choice', operators: ['eq', 'in'], options: ['Оплачен'],
    })]);
    expect(state).toEqual({
      name: 'СостояниеОплаты', label: 'Состояние оплаты', kind: 'choice', operators: ['eq', 'in'],
      options: ['Оплачен'], unavailable: undefined,
    });
  });

  it('колонка, чьё значение зависит от отбора, в отбор не идёт; закрытая остаётся с причиной', () => {
    const columns = tableFilterColumns([
      column({ key: 'СуммаПоОтбору', dependsOnFilter: true, operators: [] }),
      column({ key: 'Итого', unavailable: 'no-right', reason: 'нет права на суммы' }),
    ]);
    expect(columns.map(c => c.name)).toEqual(['Итого']);
    expect(columns[0].unavailable).toBe('нет права на суммы');
  });
});

describe('tableRequest — состояние экрана становится запросом', () => {
  it('колонки, сортировка, итоги, страница и строка уходят параметрами — теми, что читает сервер', () => {
    const request = tableRequest('costs.invoices', {
      filter: null, columns: ['Номер', 'Итого'],
      sort: [{ column: 'Итого', descending: true }, { column: 'Номер', descending: false }],
      totals: ['Итого'], offset: 100, limit: 50, row: 'abc',
    });

    expect(request).toEqual({
      method: 'get', url: '/tables/costs.invoices',
      params: { limit: 50, columns: 'Номер,Итого', sort: 'Итого:desc,Номер:asc', totals: 'Итого', offset: 100, row: 'abc' },
    });
  });

  it('чего в состоянии нет, того нет и в запросе — пустой параметр сервер прочёл бы по-своему', () => {
    const request = tableRequest('costs.invoices', { filter: null, columns: null, sort: [], totals: [], offset: 0 });
    expect(request).toEqual({ method: 'get', url: '/tables/costs.invoices', params: { limit: 200 } });
  });

  it('отбор, который деревом не разобрался, уходит серверу как есть — отказывает он', () => {
    const request = tableRequest('costs.invoices', { filter: '{"type":"condi' });
    expect(request.method === 'get' && request.params.filter).toBe('{"type":"condi');
  });

  it('длину меряем байтами адреса: кириллица стоит шести байт за знак', () => {
    // 700 знаков кириллицы — меньше прежнего потолка «1500 знаков», но в адресе это 4200 байт.
    const filter = { type: 'condition' as const, column: 'Назначение', op: 'contains' as const, value: 'я'.repeat(700) };
    const request = tableRequest('costs.invoices', { filter, sort: [{ column: 'Номер', descending: true }] });

    expect(request.method).toBe('post');
    expect(request.method === 'post' && request.body).toMatchObject({
      limit: 200, sort: [{ column: 'Номер', descending: true }],
    });
    // Латиница той же длины в адрес помещается.
    expect(tableRequest('costs.invoices', { filter: { ...filter, value: 'z'.repeat(700) } }).method).toBe('get');
  });
});

describe('колонки по умолчанию', () => {
  // «Колонки не выбраны» и запрос без списка колонок обязаны значить одно: сервер такую колонку не
  // пришлёт, и галочка у неё в окошке выбора была бы неправдой (issue #1186).
  it('колонка, приходящая только по требованию, в умолчание не входит', () => {
    const columns = [column({ key: 'Номер' }), column({ key: 'Ссылки', onDemand: true }), column({ key: 'Дата' })];
    expect(defaultColumnKeys(columns)).toEqual(['Номер', 'Дата']);
  });
});
