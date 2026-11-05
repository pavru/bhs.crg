import { useLayoutEffect, useRef } from 'react';

/**
 * Активный вариант списка выбора — на виду, когда его сменили стрелки ([UI-87]): без прокрутки стрелка уводила
 * бы подсветку за край списка. Сменила мышь — не прокручивается: вариант под указателем и так виден, а
 * прокрутка подвела бы под неподвижную мышь соседний, и список полз бы сам (наведение на полувидимый край).
 *
 * <p>Возвращает отметку «сменили стрелки» — её зовёт обработчик клавиш перед сменой активного.</p>
 */
export function useKeyScroll(activeId: string | undefined): () => void {
  const byKeys = useRef(false);
  useLayoutEffect(() => {
    if (activeId && byKeys.current) document.getElementById(activeId)?.scrollIntoView?.({ block: 'nearest' });
    byKeys.current = false;
  }, [activeId]);
  return () => { byKeys.current = true; };
}
