import { useRoving } from './rovingFocus';

/**
 * Клавиатура радиогруппы по шаблону APG: в группу ведёт одна остановка Tab — выбранный вариант, стрелки
 * выбирают соседний и переводят на него фокус, по кругу; Home и End — крайние. Обработчик — у
 * `role="radiogroup"`, `item(i)` — у каждой `role="radio"`. Обёртка над `useRoving` ([UI-68]): значение вне
 * списка группу не запирает (ревью #1704) — там же.
 *
 * `follow: false` — выбор ведёт дальше (окно закрывается): стрелки только двигают фокус, выбирает Enter или
 * пробел, иначе первая же стрелка закрыла бы окно. `disabled` — вариант, который стрелки пропускают.
 */
export function useRadioGroup<T>(values: readonly T[], value: T | undefined, onPick: (value: T) => void,
  { follow = true, disabled }: { follow?: boolean; disabled?: (value: T, index: number) => boolean } = {}) {
  return useRoving(values, { value, disabled, select: follow && onPick });
}
