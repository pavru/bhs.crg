import { describe, it, expect } from 'vitest';
import type { FilterNode } from '@/shared/api/types';
import {
  DEFAULT_VIEW, chooserOrder, filterChanged, parseView, viewHash, withColumnMoved, withColumnShown,
  withColumnsReset,
  withFilter, withPage, withPinned, withRow, withSize, withSort, withTotal, type TableView,
} from './tableViewState';

/** Состояние экрана таблицы ↔ адрес страницы (задача G1e, issue #1092). */

const unpaid: FilterNode = { type: 'condition', column: 'СостояниеОплаты', op: 'eq', value: 'Не оплачен' };
const all = ['Номер', 'Дата', 'Поставщик', 'Итого'];
const view = (patch: Partial<TableView>): TableView => ({ ...DEFAULT_VIEW, ...patch });

describe('адрес ↔ состояние', () => {
  it('состояние по умолчанию в адрес не идёт вовсе', () => {
    expect(viewHash(DEFAULT_VIEW)).toBe('');
    expect(parseView('')).toEqual(DEFAULT_VIEW);
    expect(parseView('#')).toEqual(DEFAULT_VIEW);
  });

  it('полное состояние переживает запись в адрес и чтение из него', () => {
    const full = view({
      columns: ['Номер', 'Итого'],
      filter: { type: 'group', logic: 'and', children: [unpaid] },
      sort: [{ column: 'Итого', descending: true }, { column: 'Номер', descending: false }],
      totals: [{ column: 'Итого', aggregate: 'sum' }],
      pinned: 1, page: 3, size: 50, row: '0b1c-строка',
    });

    expect(parseView(viewHash(full))).toEqual(full);
  });

  it('списки в адресе читаются глазами: запятая и двоеточие не кодируются', () => {
    const hash = viewHash(view({ columns: ['a', 'b'], sort: [{ column: 'a', descending: true }] }));
    expect(hash).toBe('#columns=a,b&sort=a:desc');
  });

  it('значение с амперсандом и плюсом возвращается тем же', () => {
    const tricky: FilterNode = { type: 'condition', column: 'Назначение', op: 'contains', value: 'кабель & лоток + 100%' };
    expect(parseView(viewHash(view({ filter: tricky }))).filter).toEqual(tricky);
  });

  it('отбор, который деревом не разобрался, не выброшен: он остаётся в состоянии и в адресе', () => {
    const broken = parseView('#filter=%7B%22type%22%3A%22condi');

    expect(broken.filter).toBeNull();
    expect(broken.brokenFilter).toBe('{"type":"condi');
    expect(parseView(viewHash(broken)).brokenFilter).toBe('{"type":"condi');
    // JSON верный, а узлом отбора не является — то же самое.
    expect(parseView(`#filter=${encodeURIComponent('{"column":"Номер"}')}`).brokenFilter).toBe('{"column":"Номер"}');
    expect(parseView(`#filter=${encodeURIComponent('[1,2]')}`).brokenFilter).toBe('[1,2]');
  });

  it('мусор в числах — значение по умолчанию; размера страницы вне перечня не бывает', () => {
    const parsed = parseView('#page=-2&pin=много&size=7');
    expect(parsed).toMatchObject({ page: 1, pinned: 0, size: DEFAULT_VIEW.size });
    expect(parseView('#size=50').size).toBe(50);
  });

  it('сортировка: направление — после ПОСЛЕДНЕГО двоеточия; без направления — по возрастанию', () => {
    expect(parseView('#sort=Склад:Главный:desc,Номер').sort).toEqual([
      { column: 'Склад:Главный', descending: true }, { column: 'Номер', descending: false },
    ]);
  });

  it('повторённая колонка в адресе показывается один раз', () => {
    expect(parseView('#columns=Номер,Итого,Номер').columns).toEqual(['Номер', 'Итого']);
  });
});

describe('что ложится в историю браузера', () => {
  it('смена отбора — новая запись; сортировка, колонки, страница и строка — замена текущей', () => {
    const base = view({ filter: unpaid });

    expect(filterChanged(base, withFilter(base, null))).toBe(true);
    expect(filterChanged(DEFAULT_VIEW, withFilter(DEFAULT_VIEW, unpaid))).toBe(true);
    expect(filterChanged(view({ brokenFilter: '{' }), withFilter(DEFAULT_VIEW, null))).toBe(true);

    expect(filterChanged(base, withSort(base, 'Итого', false))).toBe(false);
    expect(filterChanged(base, withColumnShown(base, all, 'Дата', false))).toBe(false);
    expect(filterChanged(base, withPage(base, 2))).toBe(false);
    expect(filterChanged(base, withRow(base, 'ключ'))).toBe(false);
    // Тот же отбор, собранный заново, — не смена.
    expect(filterChanged(base, withFilter(base, { ...unpaid }))).toBe(false);
  });
});

describe('изменения состояния', () => {
  it('новый отбор возвращает на первую страницу и закрывает открытую строку', () => {
    const next = withFilter(view({ page: 4, row: 'ключ', brokenFilter: '{' }), unpaid);
    expect(next).toMatchObject({ filter: unpaid, brokenFilter: null, page: 1, row: null });
  });

  it('щелчок по шапке: по возрастанию → по убыванию → без сортировки', () => {
    const asc = withSort(DEFAULT_VIEW, 'Итого', false);
    const desc = withSort(asc, 'Итого', false);

    expect(asc.sort).toEqual([{ column: 'Итого', descending: false }]);
    expect(desc.sort).toEqual([{ column: 'Итого', descending: true }]);
    expect(withSort(desc, 'Итого', false).sort).toEqual([]);
  });

  it('обычный щелчок оставляет одну колонку; с Shift колонка добавляется следующим ключом', () => {
    const byTotal = withSort(DEFAULT_VIEW, 'Итого', false);

    expect(withSort(byTotal, 'Номер', false).sort).toEqual([{ column: 'Номер', descending: false }]);
    const both = withSort(byTotal, 'Номер', true);
    expect(both.sort.map(s => s.column)).toEqual(['Итого', 'Номер']);
    // Shift по уже стоящей колонке меняет её направление на её же месте, а третий раз — убирает.
    const flipped = withSort(both, 'Итого', true);
    expect(flipped.sort).toEqual([{ column: 'Итого', descending: true }, { column: 'Номер', descending: false }]);
    expect(withSort(flipped, 'Итого', true).sort.map(s => s.column)).toEqual(['Номер']);
  });

  it('сортировка и размер страницы возвращают на первую страницу', () => {
    expect(withSort(view({ page: 3 }), 'Итого', false).page).toBe(1);
    expect(withSize(view({ page: 3 }), 50)).toMatchObject({ page: 1, size: 50 });
  });

  it('убрать колонку из таблицы по умолчанию — остаются остальные в её порядке', () => {
    expect(withColumnShown(DEFAULT_VIEW, all, 'Дата', false).columns).toEqual(['Номер', 'Поставщик', 'Итого']);
  });

  it('возвращённая колонка встаёт в конец либо на названное место; уже показанная не удваивается', () => {
    const without = view({ columns: ['Номер', 'Итого'] });
    expect(withColumnShown(without, all, 'Дата', true).columns).toEqual(['Номер', 'Итого', 'Дата']);
    expect(withColumnShown(without, all, 'Дата', true, 1).columns).toEqual(['Номер', 'Дата', 'Итого']);
    expect(withColumnShown(without, all, 'Итого', true)).toBe(without);
  });

  it('в окошке выбора снятая галочка строку не уносит, а возвращённая колонка встаёт на прежнее место', () => {
    const snapshot = [...all];
    const hidden = withColumnShown(DEFAULT_VIEW, all, 'Дата', false);
    // Колонка убрана — строка списка осталась, где была.
    const order = chooserOrder(snapshot, hidden.columns!);
    expect(order).toEqual(all);

    // Вернули — на место среди показанных, которое строка занимает в списке: это отмена, а не «в конец».
    const above = order.slice(0, order.indexOf('Дата')).filter(k => hidden.columns!.includes(k)).length;
    expect(withColumnShown(hidden, all, 'Дата', true, above).columns).toEqual(all);
  });

  it('в окошке выбора перестановка видна сразу: места показанных заняты ими в новом порядке', () => {
    const moved = withColumnMoved(view({ columns: ['Номер', 'Дата', 'Итого'] }), all, 'Итого', -1);
    expect(chooserOrder(['Номер', 'Дата', 'Итого', 'Поставщик'], moved.columns!))
      .toEqual(['Номер', 'Итого', 'Дата', 'Поставщик']);
  });

  it('убранная колонка уносит свой итог; закреплено не больше, чем показано', () => {
    const start = view({
      columns: ['Номер', 'Итого'], pinned: 2,
      totals: [{ column: 'Итого', aggregate: 'sum' }, { column: 'Номер', aggregate: 'count' }],
    });
    const next = withColumnShown(start, all, 'Итого', false);

    expect(next.totals).toEqual([{ column: 'Номер', aggregate: 'count' }]);
    expect(next.pinned).toBe(1);
  });

  it('колонка сдвигается на одно место; с края не уходит', () => {
    expect(withColumnMoved(DEFAULT_VIEW, all, 'Дата', -1).columns).toEqual(['Дата', 'Номер', 'Поставщик', 'Итого']);
    expect(withColumnMoved(DEFAULT_VIEW, all, 'Номер', -1)).toBe(DEFAULT_VIEW);
    expect(withColumnMoved(DEFAULT_VIEW, all, 'Итого', 1)).toBe(DEFAULT_VIEW);
  });

  it('итог у колонки один: новый заменяет прежний, «без итога» убирает', () => {
    const sum = withTotal(DEFAULT_VIEW, 'Итого', 'sum');
    expect(withTotal(sum, 'Итого', 'avg').totals).toEqual([{ column: 'Итого', aggregate: 'avg' }]);
    expect(withTotal(sum, 'Итого', null).totals).toEqual([]);
  });

  it('«как у таблицы» возвращает колонки, итоги и закрепление — отбор и сортировка остаются', () => {
    const custom = view({
      columns: ['Номер'], totals: [{ column: 'Номер', aggregate: 'count' }], pinned: 1,
      filter: unpaid, sort: [{ column: 'Номер', descending: true }],
    });
    expect(withColumnsReset(custom)).toEqual(view({ filter: unpaid, sort: [{ column: 'Номер', descending: true }] }));
    expect(withPinned(DEFAULT_VIEW, -3).pinned).toBe(0);
  });
});
