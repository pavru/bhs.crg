import { useRoving } from './rovingFocus';

/**
 * Клавиатура радиогруппы по шаблону APG: в группу ведёт одна остановка Tab — выбранный вариант, стрелки
 * выбирают соседний и переводят на него фокус, по кругу; Home и End — крайние. Обработчик — у
 * `role="radiogroup"`, `item(i)` — у каждой `role="radio"`. Обёртка над `useRoving` ([UI-68]): значение вне
 * списка группу не запирает (ревью #1704) — там же.
 */
export function useRadioGroup<T>(values: readonly T[], value: T | undefined, onPick: (value: T) => void) {
  return useRoving(values, { value, select: onPick });
}
