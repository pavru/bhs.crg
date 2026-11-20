import { useEffect, useState } from 'react';

type Handle = ReturnType<typeof setTimeout>;

/** Таймеры компонента — см. `useTimeouts`. */
export interface Timeouts {
  /** Поставить таймер; не сработавший снимется сам при размонтировании. */
  set(fn: () => void, ms?: number): Handle;
  /** Снять таймер; `undefined` — ничего не делать (удобно для ref, где таймера может не быть). */
  clear(handle: Handle | undefined): void;
}

/**
 * Таймеры, которые живут не дольше компонента: всё, что не сработало, снимается при размонтировании.
 * Таймер, переживший компонент, срабатывает над тем, чего уже нет, — `setState` размонтированного,
 * фокус в отсоединённый элемент, — а в тестах ещё и после того, как vitest свернул jsdom: «window is not
 * defined» и красный прогон при зелёных тестах (сторож — `shared/testing/timerLeaks.ts`).
 *
 * <p>Объект один на всю жизнь компонента — его можно звать из обработчиков и эффектов без зависимостей.
 * Снятие при размонтировании не запирает его: StrictMode размонтирует и монтирует снова, и таймеры,
 * поставленные после этого, работают как обычно.</p>
 *
 * Таймер уровня страницы, которому положено пережить компонент (модульное состояние), ставят через
 * `pageTimers` из `shared/utils`.
 */
export function useTimeouts(): Timeouts {
  const [timers] = useState(() => {
    const live = new Set<Handle>();
    return {
      live,
      set(fn: () => void, ms?: number) {
        const handle = setTimeout(() => { live.delete(handle); fn(); }, ms);
        live.add(handle);
        return handle;
      },
      clear(handle: Handle | undefined) {
        if (handle === undefined) return;
        live.delete(handle);
        clearTimeout(handle);
      },
    };
  });
  useEffect(() => () => {
    for (const handle of timers.live) clearTimeout(handle);
    timers.live.clear();
  }, [timers]);
  return timers;
}
