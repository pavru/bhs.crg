import { describe, expect, it } from 'vitest';
import type { TablePreset } from '@/shared/api/tables';
import type { FilterNode } from '@/shared/api/types';
import type { SavedTableView } from '@/shared/api/tableViews';
import {
  basedOnOf, copyTitle, isSavedId, keeping, listActions, lookupBase, placeHash, rebasedHash, rowsWantedFor, saveActions,
  savedView, stateOf,
  viewGroups, viewLookup, viewModified, withViewReset,
} from './savedViews';
import { DEFAULT_VIEW, addressChange, parseView, viewHash, withFilter, withSort, type TableView } from './tableViewState';

const ID = '3f2a0c1e-5b7d-4e8f-9a01-23456789abcd';
const condition: FilterNode = { type: 'condition', column: 'Поставщик', op: 'eq', value: 'Ромашка' };

const registry: TablePreset = {
  code: 'registry', title: 'Реестр счетов', columns: ['Номер', 'Итого'], sort: [], totals: [], pinned: 1, filters: [],
};

function saved(over: Partial<SavedTableView> = {}): SavedTableView {
  return {
    id: ID, title: 'Счета Ромашки', shared: false, mine: true, author: 'Анна', canEdit: true, basedOn: null,
    state: {
      columns: ['Номер', 'Итого'], filter: JSON.stringify(condition), sort: [{ column: 'Итого', descending: true }],
      totals: [{ column: 'Итого', aggregate: 'sum' }], pinned: 1,
    },
    version: '7', ...over,
  };
}

const declared = { state: null, views: [registry] };

describe('что стоит под адресом', () => {
  it('идентификатор отличается от кода представления модуля', () => {
    expect(isSavedId(ID)).toBe(true);
    expect(isSavedId('registry')).toBe(false);
    expect(isSavedId(undefined)).toBe(false);
  });

  it('код модуля ищется среди готовых, как и раньше', () => {
    expect(viewLookup('registry', declared, [saved()])).toEqual({ state: 'found', preset: registry });
    expect(viewLookup(undefined, declared, undefined)).toEqual({ state: 'table' });
  });

  it('идентификатор ждёт и описания таблицы, и списка представлений', () => {
    expect(viewLookup(ID, undefined, [saved()])).toEqual({ state: 'pending' });
    expect(viewLookup(ID, declared, undefined)).toEqual({ state: 'pending' });
    expect(rowsWantedFor({ state: 'pending' })).toBe(false);
  });

  it('идентификатор из списка — сохранённое представление, без учёта регистра', () => {
    const mine = saved();
    expect(viewLookup(ID.toUpperCase(), declared, [mine])).toEqual({ state: 'saved', saved: mine });
    expect(rowsWantedFor({ state: 'saved', saved: mine })).toBe(true);
  });

  it('идентификатора в списке нет — закладка устарела, и строки не запрашиваются', () => {
    expect(viewLookup(ID, declared, [])).toEqual({ state: 'gone' });
    expect(rowsWantedFor({ state: 'gone' })).toBe(false);
  });

  it('у выключенного модуля говорит таблица, а не «представление удалено»', () => {
    expect(viewLookup(ID, { state: 'module-off', views: [] }, undefined)).toEqual({ state: 'off' });
  });
});

describe('настройка сохранённого представления — основа адреса', () => {
  const base = savedView(saved().state);

  it('отбор, сортировка, итоги и закрепление приходят из настройки', () => {
    expect(base).toMatchObject({
      columns: ['Номер', 'Итого'], filter: condition, brokenFilter: null,
      sort: [{ column: 'Итого', descending: true }], totals: [{ column: 'Итого', aggregate: 'sum' }], pinned: 1,
    });
  });

  it('пока ничего не меняли, адрес пуст — и читается обратно той же настройкой', () => {
    expect(viewHash(base, base)).toBe('');
    expect(parseView('', base)).toEqual(base);
  });

  it('снятый отбор — отличие: он в адресе пустым, и перезагрузка его не возвращает', () => {
    const cleared = withFilter(base, null);
    const hash = viewHash(cleared, base);
    expect(hash).toBe('#filter=');
    expect(parseView(hash, base).filter).toBeNull();
  });

  it('отбор, который не разобрался, не выбрасывается — уезжает серверу как есть', () => {
    const broken = savedView({ ...saved().state, filter: '{"oops"' });
    expect(broken.filter).toBeNull();
    expect(broken.brokenFilter).toBe('{"oops"');
  });

  it('итог с незнакомым словом пропускается, пустой список колонок — «все колонки»', () => {
    const odd = savedView({ columns: [], totals: [{ column: 'Итого', aggregate: 'median' }], pinned: 0 });
    expect(odd.columns).toBeNull();
    expect(odd.totals).toEqual([]);
  });

  it('основа берётся по тому, что под адресом', () => {
    expect(lookupBase({ state: 'table' })).toBe(DEFAULT_VIEW);
    expect(lookupBase({ state: 'found', preset: registry }).columns).toEqual(['Номер', 'Итого']);
    expect(lookupBase({ state: 'saved', saved: saved() }).filter).toEqual(condition);
  });

  it('что сохранили, то и прочли: настройка переживает путь туда и обратно', () => {
    expect(savedView(stateOf(base))).toEqual(base);
    expect(stateOf({ ...DEFAULT_VIEW, page: 3, row: 'k' })).toEqual({ columns: null, filter: null, sort: [], totals: [], pinned: 0 });
  });
});

describe('несохранённые изменения', () => {
  const base = savedView(saved().state);

  it('помечены до сброса: сортировка, отбор, колонки — изменение', () => {
    const sorted = withSort(base, 'Номер', false);
    expect(viewModified(base, base)).toBe(false);
    expect(viewModified(sorted, base)).toBe(true);
    expect(viewModified(withFilter(base, null), base)).toBe(true);
    expect(viewModified({ ...base, columns: ['Номер'] }, base)).toBe(true);
    expect(viewModified(withViewReset(sorted, base), base)).toBe(false);
  });

  it('страница, её размер и открытая строка — не изменение', () => {
    expect(viewModified({ ...base, page: 4, size: 50, row: 'k1' }, base)).toBe(false);
  });

  it('сброс возвращает настройку, оставляя размер страницы', () => {
    const changed: TableView = { ...withSort(base, 'Номер', false), size: 50, page: 3, row: 'k1' };
    expect(withViewReset(changed, base)).toEqual({ ...base, size: 50 });
  });

  it('сброс под таблицей целиком — отдельный шаг истории, если снят отбор', () => {
    const hash = viewHash(withFilter(DEFAULT_VIEW, condition));
    expect(addressChange(hash, DEFAULT_VIEW, v => withViewReset(v, DEFAULT_VIEW))).toEqual({ hash: '', replace: false });
  });

  it('после сохранения адрес отсчитан от новой основы: сохранённое уходит, место остаётся', () => {
    const shown: TableView = { ...withSort(base, 'Номер', false), page: 2 };
    expect(rebasedHash(viewHash(shown, base), base, stateOf(shown))).toBe('#page=2');
  });

  it('правка, сделанная пока шло сохранение, остаётся в адресе — и помечена', () => {
    const sent = withSort(base, 'Номер', false);
    const meanwhile = withFilter(sent, null);
    const hash = rebasedHash(viewHash(meanwhile, base), base, stateOf(sent));
    expect(hash).toBe('#filter=');
    expect(viewModified(parseView(hash, savedView(stateOf(sent))), savedView(stateOf(sent)))).toBe(true);
  });

  it('название копии не длиннее предела и не обрастает вторым хвостом', () => {
    expect(copyTitle('Реестр счетов')).toBe('Реестр счетов — моё');
    expect(copyTitle('Реестр счетов — моё')).toBe('Реестр счетов — моё');
    expect(copyTitle('я'.repeat(119))).toHaveLength(120);
  });

  it('после сохранения в адресе остаётся только место в таблице', () => {
    expect(placeHash({ ...withSort(base, 'Номер', false), page: 2, size: 50, row: 'k1' })).toBe('#page=2&size=50&row=k1');
    expect(placeHash(base)).toBe('');
  });
});

describe('какие действия показаны', () => {
  const changed = withSort(DEFAULT_VIEW, 'Номер', false);

  it('представление модуля заперто: «Сохранить» нет, есть только копия', () => {
    expect(saveActions(changed, { state: 'found', preset: registry })).toEqual({ save: false, saveAs: true });
    expect(basedOnOf({ state: 'found', preset: registry })).toBe('registry');
  });

  it('у таблицы целиком сохранять поверх нечего', () => {
    expect(saveActions(changed, { state: 'table' })).toEqual({ save: false, saveAs: true });
    expect(basedOnOf({ state: 'table' })).toBeNull();
  });

  it('своё сохраняется поверх; чужое общее без права — только копией', () => {
    expect(saveActions(changed, { state: 'saved', saved: saved() }).save).toBe(true);
    expect(saveActions(changed, { state: 'saved', saved: saved({ shared: true, mine: false, canEdit: false }) }))
      .toEqual({ save: false, saveAs: true });
  });

  it('под скрытым отбором автора поверх не сохраняют даже с правом — правка стёрла бы его условия', () => {
    const withheld = saved({ shared: true, mine: false, canEdit: true, withheld: 'Отбор автора скрыт' });
    expect(saveActions(changed, { state: 'saved', saved: withheld })).toEqual({ save: false, saveAs: true });
    expect(listActions(withheld, true)).toEqual({ rename: false, share: false, unshare: false, remove: true });
  });

  it('отбор, который не разобрался, не сохраняется никак', () => {
    const broken = { ...changed, brokenFilter: '{"oops"' };
    expect(saveActions(broken, { state: 'saved', saved: saved() })).toEqual({ save: false, saveAs: false });
  });

  it('сделать общим — только своё и только с правом', () => {
    expect(listActions(saved(), true)).toEqual({ rename: true, share: true, unshare: false, remove: true });
    expect(listActions(saved(), false).share).toBe(false);
  });

  it('вернуть в свои — только автору: держатель права чужое общее правит, но себе не забирает', () => {
    expect(listActions(saved({ shared: true }), true).unshare).toBe(true);
    expect(listActions(saved({ shared: true, mine: false }), true))
      .toEqual({ rename: true, share: false, unshare: false, remove: true });
  });

  it('чужое общее без права — действий нет', () => {
    expect(listActions(saved({ shared: true, mine: false, canEdit: false }), false))
      .toEqual({ rename: false, share: false, unshare: false, remove: false });
  });

  it('переименование и смена «общее» настройку не трогают', () => {
    const mine = saved({ basedOn: 'registry' });
    expect(keeping(mine, { title: 'Новое' })).toEqual({ title: 'Новое', shared: false, state: mine.state, basedOn: 'registry' });
    expect(keeping(mine, { shared: true }).shared).toBe(true);
  });

  it('список делится на готовые, свои и общие; своё общее стоит в общих', () => {
    const [own, common] = [saved(), saved({ id: ID.replace('3f', '4f'), shared: true })];
    expect(viewGroups([registry], [own, common])).toEqual({ ready: [registry], own: [own], shared: [common] });
    expect(viewGroups(undefined, undefined)).toEqual({ ready: [], own: [], shared: [] });
  });
});
