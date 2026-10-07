import type { FilterCondition, FilterOp } from '@/shared/api/types';
import type { TableShortcut } from '@/shared/api/tables';
import { chipsView, fromChips } from '@/shared/filter/chipsModel';
import { withColumnShown, withFilterChange, type TableView } from './tableViewState';

/**
 * Готовые отборы таблицы (issue #1186) — правила строки «Навести порядок». Чистые: экран их только
 * рисует, а проверяет тест.
 *
 * Готовый отбор ничего не прячет и не зашивает: нажатие кладёт его условие в ОБЫЧНЫЙ отбор. Дальше
 * это чип как любой другой — виден, снимается крестиком, сочетается с остальными, едет в адресе.
 */

/** Условие, которое готовый отбор ставит. */
export function shortcutCondition(shortcut: TableShortcut): FilterCondition {
  return { type: 'condition', column: shortcut.column, op: shortcut.op as FilterOp, value: shortcut.value };
}

function same(cond: FilterCondition, shortcut: TableShortcut): boolean {
  return cond.column === shortcut.column && cond.op === shortcut.op && cond.value === shortcut.value;
}

export type ShortcutState = 'on' | 'off' | 'complex' | 'broken';

/** Отбор экрана — то, что из него нужно готовому отбору. */
export type ShortcutFilter = Pick<TableView, 'filter' | 'brokenFilter'>;

/**
 * Что с готовым отбором под текущим отбором экрана:
 * `on` — его условие стоит; `off` — не стоит, поставить можно; `complex` — отбор сложный (с «ИЛИ»
 * или группами), и добавить к нему условие чипом нельзя: куда именно, ряд чипов не скажет;
 * `broken` — в адресе стоит отбор, который не разобрался. Его условий экран не знает, и нажатие
 * заменило бы их молча — человек потерял бы отбор, о котором сервер ещё только говорит отказом.
 */
export function shortcutState(view: ShortcutFilter, shortcut: TableShortcut): ShortcutState {
  if (view.brokenFilter !== null) return 'broken';
  const chips = chipsView(view.filter);
  if (chips.mode !== 'chips') return 'complex';
  return chips.conditions.some(c => same(c, shortcut)) ? 'on' : 'off';
}

/**
 * Нажатие на готовый отбор: стоит — снять, не стоит — поставить и показать его колонку.
 *
 * Ставя, снимаем прочие условия по ТОЙ ЖЕ колонке: у выбора значение одно, и «равно „есть“» рядом с
 * «равно „есть, период закрыт“» — пересечение, то есть пустая таблица под видом «исправлять нечего».
 * Колонка включается в показ: без неё человек видит счета и не видит, почему они здесь. Снимая отбор,
 * колонку не убираем — её убирают как любую, из окошка «Колонки».
 *
 * Снимая — убираем только СВОЁ условие: прочие по той же колонке человек ставил сам.
 */
export function withShortcut(view: TableView, all: string[], shortcut: TableShortcut): TableView {
  const state = shortcutState(view, shortcut);
  if (state !== 'on' && state !== 'off') return view;

  const next = withFilterChange(view, current => {
    const chips = chipsView(current);
    if (chips.mode !== 'chips') return current;
    return fromChips(state === 'on'
      ? chips.conditions.filter(c => !same(c, shortcut))
      : [...chips.conditions.filter(c => c.column !== shortcut.column), shortcutCondition(shortcut)]);
  });
  return state === 'on' ? next : withColumnShown(next, all, shortcut.column, true);
}

/**
 * Показывать ли готовый отбор. Ноль — не показываем: звать нечем. Но «не проверено» — не ноль, и
 * отбор, который стоит, тоже остаётся: нажатый чип, пропавший вместе с последней строкой, читался бы
 * как сбой.
 */
export function shortcutShown(view: ShortcutFilter, shortcut: TableShortcut): boolean {
  return shortcut.count > 0 || shortcut.unchecked !== null || shortcutState(view, shortcut) === 'on';
}

/**
 * Число на чипе. Непроверенный ноль числом не пишется: «0» значило бы «таких строк нет», а на деле
 * посчитать не удалось.
 */
export function shortcutCount(shortcut: TableShortcut): string {
  if (shortcut.unchecked === null) return String(shortcut.count);
  return shortcut.count === 0 ? 'не проверено' : `${shortcut.count}, проверено не всё`;
}

/** Подсказка чипа: что это за строки, почему числу нельзя верить и почему чип не нажимается. */
export function shortcutTitle(shortcut: TableShortcut, state: ShortcutState): string {
  return [
    shortcut.hint,
    shortcut.unchecked && `Число неполное — ${shortcut.unchecked}.`,
    state === 'complex' && 'Отбор сложный: условие добавляется в расширенном режиме.',
    state === 'broken' && 'Отбор из адреса не применён: сначала снимите его.',
    state === 'on' && 'Нажмите, чтобы снять отбор.',
  ].filter(Boolean).join(' ');
}
