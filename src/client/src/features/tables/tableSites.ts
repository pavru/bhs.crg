import type { FilterCondition } from '@/shared/api/types';
import type { TableColumn } from '@/shared/api/tables';
import { chipsView, fromChips } from '@/shared/filter/chipsModel';
import type { SiteEntry } from '@/shared/siteFilter/siteFilterContext';
import type { ShortcutFilter, ShortcutState } from './tableShortcuts';
import { withColumnShown, withFilterChange, type TableView } from './tableViewState';

/**
 * Чип «Объект: избранные стройки» над таблицей (issue #1223; решение владельца №7 эпика #1217). Правила —
 * чистые: экран их только рисует, а проверяет тест.
 *
 * <p>Под неявный фильтр таблица не идёт: движок запрещает отбор, который человек не ставил, а условие по
 * объекту делает «Сумму» долей — реестр молча показывал бы часть денег. Поэтому фильтр здесь только
 * ПРЕДЛАГАЕТ: нажатие кладёт в обычный отбор условие «из списка» по названиям избранных строек. Дальше это
 * чип как любой другой — виден, снимается крестиком, едет в адресе, и «Сумма» подписана долей.</p>
 *
 * <p>⚠️ Отбор идёт по НАЗВАНИЮ: неизбранная стройка с названием избранной попадёт тоже (`namesakes`).</p>
 */

/** Колонка, по которой чип предлагается: со стройками, открыта читателю и отбирается «из списка». */
export function sitesColumn(columns: readonly TableColumn[]): TableColumn | undefined {
  return columns.find(c => c.sites && !c.unavailable && c.operators.includes('in'));
}

/** Названия избранных строек — каждое по разу и по алфавиту: от порядка избранного условие не зависит. */
export function favoriteNames(sites: readonly SiteEntry[], favorites: ReadonlySet<string>): string[] {
  return [...new Set(sites.filter(s => favorites.has(s.id)).map(s => s.name))].sort((a, b) => a.localeCompare(b, 'ru'));
}

/**
 * Названия, которые носят и избранная, и неизбранная стройка: под чипом попадут счета обеих. Сказать это
 * обязательно — иначе «избранные стройки» на чипе обещали бы то, чего отбор по названию не держит.
 */
export function namesakes(sites: readonly SiteEntry[], favorites: ReadonlySet<string>): string[] {
  const favorite = new Set(favoriteNames(sites, favorites));
  return [...new Set(sites.filter(s => !favorites.has(s.id) && favorite.has(s.name)).map(s => s.name))];
}

/** Условие чипа. */
export function sitesCondition(column: string, names: readonly string[]): FilterCondition {
  return { type: 'condition', column, op: 'in', values: [...names] };
}

/** То же условие: колонка, «из списка» и тот же перечень в любом порядке. */
function same(cond: FilterCondition, column: string, names: readonly string[]): boolean {
  const values = cond.values ?? [];
  return cond.column === column && cond.op === 'in'
    && values.length === names.length && names.every(n => values.includes(n));
}

/**
 * Что с чипом под текущим отбором — те же состояния, что у готовых отборов (`shortcutState`): сложный
 * отбор чипом не дополнить, а неразобранный из адреса нажатие заменило бы молча.
 *
 * <p>Избранное сменилось, а условие осталось прежним — чип `off`: прежнее условие стоит обычным чипом со
 * своими названиями, и оно уже не «избранные стройки».</p>
 */
export function sitesChipState(view: ShortcutFilter, column: string, names: readonly string[]): ShortcutState {
  if (view.brokenFilter !== null) return 'broken';
  const chips = chipsView(view.filter);
  if (chips.mode !== 'chips') return 'complex';
  return chips.conditions.some(c => same(c, column, names)) ? 'on' : 'off';
}

/**
 * Нажатие: стоит — снять своё условие, не стоит — добавить и показать колонку (без неё не видно, почему
 * строки здесь).
 *
 * <p>Прочие условия по той же колонке остаются, в отличие от готовых отборов: «Объект: Комарова 36»
 * человек поставил сам, и молча снять его значило бы показать больше, чем он просил. Два условия — это
 * пересечение, и оба видны чипами.</p>
 */
export function withSitesChip(view: TableView, all: string[], column: string, names: readonly string[]): TableView {
  const state = sitesChipState(view, column, names);
  if (state !== 'on' && state !== 'off') return view;

  const next = withFilterChange(view, current => {
    const chips = chipsView(current);
    if (chips.mode !== 'chips') return current;
    return fromChips(state === 'on'
      ? chips.conditions.filter(c => !same(c, column, names))
      : [...chips.conditions, sitesCondition(column, names)]);
  });
  return state === 'on' ? next : withColumnShown(next, all, column, true);
}
