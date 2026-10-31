import { useRef, type KeyboardEvent } from 'react';

const HORIZONTAL: Record<string, number> = { ArrowRight: 1, ArrowLeft: -1 };
const BOTH: Record<string, number> = { ...HORIZONTAL, ArrowDown: 1, ArrowUp: -1 };

/**
 * Клавиатура ряда с одной остановкой Tab (APG, «roving tabindex») — общая у вкладок и радиогруппы
 * ([UI-68]): прежде было три копии, и запасная остановка и пропуск отключённых жили не во всех.
 *
 * - `select: false` — вкладки: стрелки, Home и End двигают только фокус, переключает Enter (`Tabs`).
 * - `select: (value) => …` — радио: стрелка выбирает соседний вариант и переводит на него фокус.
 *
 * У обоих Home и End ведут к крайним, отключённый вариант пропускается. Значение вне списка (старое,
 * заданное другим клиентом) группу не запирает: остановкой Tab становится первый доступный вариант, а
 * стрелка идёт от края (ревью #1704).
 *
 * `onKeyDown` можно повесить и на группу, и на каждый вариант; `item(i)` — у каждого варианта.
 */
export function useRoving<T>(values: readonly T[], { value, disabled, select, axis = 'both' }: {
  value: T | undefined;
  disabled?: (value: T, index: number) => boolean;
  select: false | ((value: T) => void);
  /** Вкладки — только влево-вправо: вверх-вниз прокручивают страницу. */
  axis?: 'horizontal' | 'both';
}) {
  const refs = useRef<(HTMLElement | null)[]>([]);
  const enabled = values.flatMap((v, i) => (disabled?.(v, i) ? [] : [i]));
  const at = values.indexOf(value as T);
  const stop = enabled.includes(at) ? at : enabled[0] ?? -1;

  const onKeyDown = (e: KeyboardEvent) => {
    const step = (axis === 'both' ? BOTH : HORIZONTAL)[e.key];
    const edge = e.key === 'Home' ? enabled[0] : e.key === 'End' ? enabled[enabled.length - 1] : undefined;
    if ((!step && edge === undefined) || enabled.length === 0) return;
    e.preventDefault();
    // У радио шаг — от выбранного: фокус стоит на нём, а без выбора (значение вне списка) стрелка идёт
    // от края. У вкладок — от того, на чём фокус: с выбранным они расходятся.
    const focused = refs.current.indexOf(e.target as HTMLElement);
    const from = enabled.indexOf(select ? at : focused >= 0 ? focused : at);
    const next = edge ?? (from < 0
      ? (step > 0 ? enabled[0] : enabled[enabled.length - 1])
      : enabled[(from + step + enabled.length) % enabled.length]);
    if (select) select(values[next]);
    refs.current[next]?.focus();
  };

  const item = (i: number) => ({
    ref: (el: HTMLElement | null) => { refs.current[i] = el; },
    tabIndex: i === stop ? 0 : -1,
  });

  return { onKeyDown, item };
}
