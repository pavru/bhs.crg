import { useEffect, useRef, useState } from 'react';

/**
 * Сколько мышь должна простоять над строкой, чтобы карточка сменилась (issue #1517). Проводя мышью к
 * «Выбрать эту позицию», человек пересекает соседние строки: без паузы карточка мигала бы каждой и
 * заводила бы по запросу на каждую.
 */
export const HOVER_REST = 150;

/**
 * Куда встаёт выделение стрелкой. Ничего не выделено — вниз встаёт первая, вверх — последняя; у края
 * останавливается, а не перескакивает на другой конец: перескок в длинном списке читается как сбой.
 * `-1` — списка нет.
 */
export function stepIndex(current: number, delta: 1 | -1, length: number): number {
  if (length === 0) return -1;
  if (current < 0 || current >= length) return delta > 0 ? 0 : length - 1;
  return Math.min(length - 1, Math.max(0, current + delta));
}

/**
 * Одно выделение на список (issue #1517): стрелки меняют его сразу, мышь — после покоя над строкой.
 * Курсор ушёл со списка — выделение остаётся: карточку уводят читать, а не терять.
 *
 * <p>Выделенной позиции может не оказаться в списке — набор сменил находки. Выделение тогда держится
 * (`index` = -1): карточка показанной остаётся, а Enter её не выберет — её больше нет среди найденного.</p>
 */
export function usePickerSelection(ids: readonly string[]) {
  const [active, setActive] = useState<string | null>(null);
  const timer = useRef<number | null>(null);

  function settle() {
    if (timer.current !== null) window.clearTimeout(timer.current);
    timer.current = null;
  }
  // Окно закрыли посреди паузы — выделять уже некому.
  useEffect(() => settle, []);

  const index = active === null ? -1 : ids.indexOf(active);
  return {
    active,
    index,
    /** Мышь двигается над строкой: пауза отсчитывается заново с каждым движением. */
    hover(id: string) {
      settle();
      timer.current = window.setTimeout(() => { timer.current = null; setActive(id); }, HOVER_REST);
    },
    /** Мышь ушла: недостоявшееся не выделяется, выделенное остаётся. */
    leave: settle,
    step(delta: 1 | -1) {
      settle();
      const next = stepIndex(index, delta, ids.length);
      if (next >= 0) setActive(ids[next]);
    },
    select(id: string | null) {
      settle();
      setActive(id);
    },
  };
}
