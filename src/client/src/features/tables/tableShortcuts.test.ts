import { describe, it, expect } from 'vitest';
import type { FilterNode } from '@/shared/api/types';
import type { TableShortcut } from '@/shared/api/tables';
import { shortcutCondition, shortcutCount, shortcutShown, shortcutState, shortcutTitle, withShortcut } from './tableShortcuts';
import { DEFAULT_VIEW, type TableView } from './tableViewState';

/** Готовые отборы таблицы — строка «Навести порядок» (issue #1186). */

const lost: TableShortcut = {
  code: 'lost', title: 'Ссылки на удалённые записи', hint: 'Замените запись.', column: 'Ссылки', op: 'eq',
  value: 'есть', count: 3, unchecked: null, quiet: false,
};
const all = ['Номер', 'Поставщик'];
const view = (patch: Partial<TableView>): TableView => ({ ...DEFAULT_VIEW, ...patch });
const supplier: FilterNode = { type: 'condition', column: 'Поставщик', op: 'eq', value: 'Ромашка' };
const and = (...children: FilterNode[]): FilterNode => ({ type: 'group', logic: 'and', children });

describe('нажатие на готовый отбор', () => {
  it('ставит условие обычным чипом, рядом с уже стоящими, и показывает колонку', () => {
    const next = withShortcut(view({ filter: and(supplier), columns: ['Номер'], page: 3 }), all, lost);

    expect(next.filter).toEqual(and(supplier, shortcutCondition(lost)));
    expect(next.columns).toEqual(['Номер', 'Ссылки']);
    // Отбор сменился — страница первая: третьей под новым отбором может не быть.
    expect(next.page).toBe(1);
    expect(shortcutState(next.filter, lost)).toBe('on');
  });

  it('второе нажатие снимает условие, а колонку оставляет', () => {
    const on = withShortcut(view({ filter: and(supplier), columns: ['Номер'] }), all, lost);
    const off = withShortcut(on, all, lost);

    expect(off.filter).toEqual(and(supplier));
    expect(off.columns).toEqual(['Номер', 'Ссылки']);
    expect(withShortcut(withShortcut(view({}), all, lost), all, lost).filter).toBeNull();
  });

  it('заменяет другое условие по той же колонке, а не пересекается с ним', () => {
    const locked: FilterNode = { type: 'condition', column: 'Ссылки', op: 'eq', value: 'есть, период закрыт' };

    expect(withShortcut(view({ filter: and(locked, supplier) }), all, lost).filter)
      .toEqual(and(supplier, shortcutCondition(lost)));
  });

  it('под колонками по умолчанию добавляет колонку к ним, а не оставляет её одну', () => {
    expect(withShortcut(view({}), all, lost).columns).toEqual(['Номер', 'Поставщик', 'Ссылки']);
  });

  it('сложный отбор не трогает: куда поставить условие, ряд чипов не скажет', () => {
    const complex: FilterNode = { type: 'group', logic: 'or', children: [supplier, shortcutCondition(lost)] };
    const before = view({ filter: complex });

    expect(shortcutState(complex, lost)).toBe('blocked');
    expect(withShortcut(before, all, lost)).toBe(before);
    expect(shortcutTitle(lost, 'blocked')).toContain('расширенном режиме');
  });
});

describe('что показывает чип', () => {
  it('ноль не показывается, а «не проверено» и стоящий отбор — да', () => {
    const none = { ...lost, count: 0 };

    expect(shortcutShown(null, lost)).toBe(true);
    expect(shortcutShown(null, none)).toBe(false);
    expect(shortcutShown(null, { ...none, unchecked: 'проверено не всё' })).toBe(true);
    // Исправили последний счёт: нажатый чип остаётся, иначе его пропажа читалась бы как сбой.
    expect(shortcutShown(and(shortcutCondition(none)), none)).toBe(true);
  });

  it('непроверенный ноль числом не пишется', () => {
    expect(shortcutCount(lost)).toBe('3');
    expect(shortcutCount({ ...lost, count: 0, unchecked: 'проверено не всё: …' })).toBe('не проверено');
    expect(shortcutCount({ ...lost, unchecked: 'проверено не всё: …' })).toBe('3, проверено не всё');
    expect(shortcutTitle({ ...lost, unchecked: 'проверено не всё: колонок — 1' }, 'off'))
      .toBe('Замените запись. Число неполное — проверено не всё: колонок — 1.');
  });
});
