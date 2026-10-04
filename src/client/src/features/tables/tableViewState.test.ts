import { describe, it, expect } from 'vitest';
import type { FilterNode } from '@/shared/api/types';
import type { TablePreset } from '@/shared/api/tables';
import { chipAdded, chipRemoved } from '@/shared/filter/chipsModel';
import {
  DEFAULT_VIEW, addressChange, chooserOrder, columnsCustomised, filterChanged, parseView, presetLookup, presetView, rowsWanted,
  viewHash, wholeTableHash, withColumnMoved, withColumnReturned, withColumnShown, withColumnsReset,
  withFilter, withFilterChange, withPage, withPageStep, withPinned, withRow, withSize, withSort, withTotal,
  type TableView,
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

/** Готовое представление модуля — основа, от которой отсчитан адрес (задача G4, issue #1097). */
describe('изменение считается от адреса, а не от нарисованного', () => {
  const purpose: FilterNode = { type: 'condition', column: 'Назначение', op: 'eq', value: 'Посев' };
  const both: FilterNode = { type: 'group', logic: 'and', children: [purpose, unpaid] };
  /** Адрес после изменения; изменение, которому менять нечего, оставляет адрес прежним. */
  const after = (hash: string, change: (v: TableView) => TableView, base = DEFAULT_VIEW) =>
    addressChange(hash, base, change)?.hash ?? hash;

  it('щелчок по шапке сразу после нового условия условие не стирает', () => {
    // Экран нарисован под одним условием; второе уже в адресе, но перерисовка под него не пришла.
    const drawn = viewHash(view({ filter: purpose }));
    const address = after(drawn, v => withFilter(v, both));

    const sorted = addressChange(address, DEFAULT_VIEW, v => withSort(v, 'Номер', false));

    expect(parseView(sorted!.hash).filter).toEqual(both);
    expect(parseView(sorted!.hash).sort).toEqual([{ column: 'Номер', descending: false }]);
    // Отбор сортировкой не сменился — запись истории заменяется, а не добавляется.
    expect(sorted!.replace).toBe(true);
  });

  it('два действия подряд складываются: второе видит первое', () => {
    let address = viewHash(view({ filter: purpose }));
    address = after(address, v => withSort(v, 'Дата', false));
    address = after(address, v => withColumnShown(v, all, 'Поставщик', false));
    address = after(address, v => withPinned(v, 1));

    expect(parseView(address)).toEqual(view({
      filter: purpose, sort: [{ column: 'Дата', descending: false }],
      columns: ['Номер', 'Дата', 'Итого'], pinned: 1,
    }));
  });

  it('смена отбора — новая запись истории; изменение без отличий адрес не трогает', () => {
    expect(addressChange('', DEFAULT_VIEW, v => withFilter(v, unpaid))!.replace).toBe(false);
    expect(addressChange(viewHash(view({ filter: unpaid })), DEFAULT_VIEW, v => withFilter(v, unpaid))).toBeNull();
    expect(addressChange('', DEFAULT_VIEW, v => v)).toBeNull();
  });

  it('два крестика подряд снимают оба чипа: второй считается от отбора без первого', () => {
    // Ряд чипов нарисован под обоими условиями; оба щелчка сделаны по этому ряду.
    let address = viewHash(view({ filter: both }));
    address = after(address, v => withFilterChange(v, chipRemoved(purpose)));
    address = after(address, v => withFilterChange(v, chipRemoved(unpaid)));

    expect(parseView(address).filter).toBeNull();
  });

  it('чип, которого в отборе уже нет, ничего не меняет — ни страницу, ни открытую строку', () => {
    const now = view({ filter: { type: 'group', logic: 'and', children: [unpaid] }, page: 2, row: 'строка' });

    expect(withFilterChange(now, chipRemoved(purpose))).toBe(now);
    expect(withFilterChange(now, chipAdded(purpose)).page).toBe(1);
  });

  it('негодный отбор правка чипами снимает, даже когда дерево осталось пустым', () => {
    const broken = view({ brokenFilter: '{"type":"condi' });

    expect(withFilterChange(broken, () => null).brokenFilter).toBeNull();
  });

  it('шаг по страницам — от показанной: под сменившимся отбором он не делается', () => {
    const shown = view({ filter: purpose, page: 3 });

    expect(withPageStep(shown, shown, 1).page).toBe(4);
    expect(withPageStep(shown, shown, -1).page).toBe(2);
    // Отбор уже другой, и адрес стоит на первой странице: «следующая» от третьей — это страница не той выдачи.
    const refiltered = withFilter(shown, both);
    expect(withPageStep(refiltered, shown, 1)).toBe(refiltered);
    // Второй щелчок до перерисовки: страница в адресе уже четвёртая, на экране — третья.
    const stepped = withPageStep(shown, shown, 1);
    expect(withPageStep(stepped, shown, 1)).toBe(stepped);
    expect(withPageStep(withSize(shown, 50), shown, 1).page).toBe(1);
  });

  it('возвращённая колонка встаёт на своё место и после только что снятой соседней', () => {
    // Список окошка: Номер, Дата, Поставщик (скрыта), Итого. Снята «Дата» — и сразу возвращён «Поставщик».
    const hidden = view({ columns: ['Номер', 'Дата', 'Итого'] });
    const now = withColumnShown(hidden, all, 'Дата', false);

    expect(withColumnReturned(now, all, 'Поставщик', ['Номер', 'Дата']).columns).toEqual(['Номер', 'Поставщик', 'Итого']);
    expect(withColumnReturned(hidden, all, 'Поставщик', ['Номер', 'Дата']).columns).toEqual(all);
  });

  it('закрепить можно не больше колонок, чем показано сейчас', () => {
    expect(withPinned(view({ columns: ['Номер', 'Дата'] }), 5).pinned).toBe(2);
    expect(withPinned(view({}), 5).pinned).toBe(5);
  });

  it('под готовым представлением отсчёт — от его настройки', () => {
    const base = view({ columns: ['Номер', 'Итого'], sort: [{ column: 'Дата', descending: true }] });
    const address = after('', v => withFilter(v, unpaid), base);

    const next = parseView(after(address, v => withPinned(v, 1), base), base);

    expect(next).toEqual({ ...base, filter: unpaid, pinned: 1 });
  });
});

describe('готовое представление', () => {
  const registry: TablePreset = {
    code: 'registry', title: 'Реестр счетов',
    columns: ['Поставщик', 'Итого', 'Номер'],
    sort: [{ column: 'Дата', descending: true }],
    totals: [{ column: 'Итого', aggregate: 'sum' }],
    pinned: 1,
    filters: ['Дата', 'Поставщик'],
  };
  const base = presetView(registry);

  it('настройка представления становится состоянием экрана; условий отбора в ней нет', () => {
    expect(base).toEqual(view({
      columns: ['Поставщик', 'Итого', 'Номер'], sort: [{ column: 'Дата', descending: true }],
      totals: [{ column: 'Итого', aggregate: 'sum' }], pinned: 1,
    }));
    expect(base.filter).toBeNull();
  });

  it('итог с незнакомым словом в состояние не попадает — считать по нему нечего', () => {
    const odd = presetView({ ...registry, totals: [{ column: 'Итого', aggregate: 'total' }] });
    expect(odd.totals).toEqual([]);
  });

  it('пока человек ничего не менял, адрес пуст — и пустой адрес открывает представление', () => {
    expect(viewHash(base, base)).toBe('');
    expect(parseView('', base)).toEqual(base);
  });

  it('в адрес идёт только отличие от представления', () => {
    const named = (hash: string) => [...new URLSearchParams(hash.slice(1)).keys()];

    const filtered = withFilter(base, unpaid);
    expect(named(viewHash(filtered, base))).toEqual(['filter']);
    expect(parseView(viewHash(filtered, base), base)).toEqual(filtered);

    const narrower = withColumnShown(base, all, 'Номер', false);
    expect(named(viewHash(narrower, base))).toEqual(['columns']);
    expect(parseView(viewHash(narrower, base), base)).toEqual(narrower);
  });

  it('снятое остаётся снятым: пустой параметр — не «как в представлении»', () => {
    const cleared = { ...base, sort: [], totals: [], pinned: 0 };
    const hash = viewHash(cleared, base);

    expect(hash).toBe('#sort=&totals=&pin=0');
    expect(parseView(hash, base)).toEqual(cleared);
  });

  it('«все колонки таблицы» под представлением названы в адресе словом', () => {
    const everything = { ...base, columns: null };
    const hash = viewHash(everything, base);

    expect(hash).toBe('#columns=*');
    expect(parseView(hash, base).columns).toBeNull();
    // У самой таблицы «все колонки» — умолчание, и в адрес оно не идёт.
    expect(viewHash(view({ columns: null }))).toBe('');
  });

  it('один и тот же адрес под таблицей и под представлением значит разное', () => {
    expect(parseView('#page=2').columns).toBeNull();
    expect(parseView('#page=2', base).columns).toEqual(registry.columns);
  });

  it('возврат колонок ведёт к представлению, а не к таблице целиком', () => {
    const custom = withPinned(withTotal(withColumnShown(base, all, 'Номер', false), 'Итого', 'max'), 2);
    expect(columnsCustomised(custom, base)).toBe(true);

    const back = withColumnsReset({ ...custom, filter: unpaid }, base);
    expect(back).toEqual({ ...base, filter: unpaid });
    expect(columnsCustomised(back, base)).toBe(false);
    // От таблицы целиком то же состояние — настроенное: мерка зависит от основы.
    expect(columnsCustomised(back)).toBe(true);
  });

  describe('что стоит под адресом с кодом представления', () => {
    const declared = { state: null, views: [registry] };

    it('код не назван — таблица целиком, строки запрашиваются сразу', () => {
      expect(presetLookup(undefined, undefined)).toEqual({ state: 'table' });
      expect(rowsWanted({ state: 'table' })).toBe(true);
    });

    it('описание ещё не пришло — строки ждут: настройка едет в нём', () => {
      expect(presetLookup('registry', undefined)).toEqual({ state: 'pending' });
      expect(rowsWanted({ state: 'pending' })).toBe(false);
    });

    it('представление найдено, регистр кода не важен', () => {
      expect(presetLookup('Registry', declared)).toEqual({ state: 'found', preset: registry });
      expect(rowsWanted({ state: 'found', preset: registry })).toBe(true);
    });

    it('кода у таблицы нет — отказ, строки не запрашиваются', () => {
      expect(presetLookup('net-takogo', declared)).toEqual({ state: 'missing' });
      expect(rowsWanted({ state: 'missing' })).toBe(false);
    });

    it('модуль выключен — это не «представления нет»: говорит таблица, и строки запрашиваются', () => {
      const off = presetLookup('registry', { state: 'module-off', views: [] });
      expect(off).toEqual({ state: 'off' });
      // Иначе экран вечно показывал бы «Строки загружаются…» под состоянием, у которого есть название.
      expect(rowsWanted(off)).toBe(true);
    });
  });

  it('к таблице целиком уходят с тем же отбором, а настройка представления остаётся позади', () => {
    const tuned = withPage(withSort({ ...base, filter: unpaid }, 'Номер', false), 3);
    const whole = parseView(wholeTableHash(tuned));

    expect(whole).toEqual(view({ filter: unpaid }));
    expect(wholeTableHash(base)).toBe('');
  });
});
