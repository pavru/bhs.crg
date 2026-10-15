import type { FilterCondition } from '@/shared/api/types';
import type { TableColumn } from '@/shared/api/tables';
import type { SiteEntry } from '@/shared/siteFilter/siteFilterContext';
import { toggleState, withToggle, type ShortcutFilter, type ShortcutState } from './tableShortcuts';
import type { TableView } from './tableViewState';

/**
 * Чип «Объект: избранные стройки» над таблицей (issue #1223; решение владельца №7 эпика #1217). Правила —
 * чистые: экран их только рисует, а проверяет тест.
 *
 * <p>Под неявный фильтр таблица не идёт: движок запрещает отбор, который человек не ставил, а условие по
 * объекту делает «Сумму» долей — реестр молча показывал бы часть денег. Поэтому фильтр здесь только
 * ПРЕДЛАГАЕТ: нажатие кладёт в обычный отбор условие «из списка» по названиям избранных строек. Дальше это
 * чип как любой другой — виден, снимается крестиком, едет в адресе, и «Сумма» подписана долей. Состояние
 * и нажатие — те же, что у готовых отборов (`toggleState`, `withToggle`).</p>
 *
 * <p>⚠️ Отбор идёт по НАЗВАНИЮ: другой объект колонки с названием избранной стройки попадёт тоже
 * (`namesakes`).</p>
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
 * Названия избранных, которые в колонке носит ещё кто-то: неизбранная стройка или другой объект колонки
 * (у счёта в «Объекте» рядом со стройками — статьи вне строек, ревью PR #1339). Под чипом попадут и они.
 * Сказать это обязательно — иначе «избранные стройки» на чипе обещали бы то, чего отбор по названию не держит.
 *
 * @param names названия избранных (`favoriteNames`)
 * @param others названия прочих объектов колонки — не строек
 */
export function namesakes(
  sites: readonly SiteEntry[], favorites: ReadonlySet<string>, names: readonly string[], others: readonly string[] = [],
): string[] {
  const taken = new Set([...sites.filter(s => !favorites.has(s.id)).map(s => s.name), ...others]);
  return names.filter(n => taken.has(n));
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
 * Что с чипом под текущим отбором. Избранное сменилось, а условие осталось прежним — чип `off`: прежнее
 * условие уже не «избранные стройки», и нажатие его заменит.
 */
export function sitesChipState(view: ShortcutFilter, column: string, names: readonly string[]): ShortcutState {
  return toggleState(view, c => same(c, column, names));
}

/** Стоят ли в отборе другие условия по колонке — нажатие их заменит, и чип об этом предупреждает. */
export function replacesOthers(view: ShortcutFilter, column: string, names: readonly string[]): boolean {
  const group = view.filter;
  return !!group && 'children' in group
    && group.children.some(c => c.type === 'condition' && c.column === column && !same(c, column, names));
}

/**
 * Нажатие: стоит — снять своё условие; не стоит — поставить, заменив прочие условия по колонке, и показать
 * колонку. Заменить, а не добавить (ревью PR #1339): строки отбор берёт пересечением, а долю «Суммы» сервер
 * считает на объединение названных объектов — число было бы ни про одно условие. Прежнее условие чипа
 * после смены избранного заменяется так же.
 */
export function withSitesChip(view: TableView, all: string[], column: string, names: readonly string[]): TableView {
  return withToggle(view, all, sitesCondition(column, names), c => same(c, column, names));
}
