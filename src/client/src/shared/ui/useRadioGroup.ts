import { useRef, type KeyboardEvent } from 'react';

const STEP: Record<string, number> = { ArrowDown: 1, ArrowRight: 1, ArrowUp: -1, ArrowLeft: -1 };

/**
 * Клавиатура радиогруппы по шаблону APG: в группу ведёт одна остановка Tab — выбранный вариант, стрелки
 * выбирают соседний и переводят на него фокус, по кругу. Обработчик — у `role="radiogroup"`, `item(i)`
 * — у каждой `role="radio"`.
 *
 * Значение вне списка (старое, заданное другим клиентом) не запирает группу: остановкой Tab становится
 * первый вариант, а стрелка идёт от края (ревью #1704). Прежде было три копии этой логики, и запасная
 * остановка жила только в одной.
 */
export function useRadioGroup<T>(values: readonly T[], value: T | undefined, onPick: (value: T) => void) {
  const refs = useRef<(HTMLElement | null)[]>([]);
  const at = values.indexOf(value as T);

  const onKeyDown = (e: KeyboardEvent) => {
    const step = STEP[e.key];
    if (!step || values.length === 0) return;
    e.preventDefault();
    const next = at < 0
      ? (step > 0 ? 0 : values.length - 1)
      : (at + step + values.length) % values.length;
    onPick(values[next]);
    refs.current[next]?.focus();
  };

  const item = (i: number) => ({
    ref: (el: HTMLElement | null) => { refs.current[i] = el; },
    tabIndex: i === at || (at < 0 && i === 0) ? 0 : -1,
  });

  return { onKeyDown, item };
}
