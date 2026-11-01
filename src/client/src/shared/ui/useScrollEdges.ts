import { useEffect, useState } from 'react';

export interface ScrollEdges {
  /** Часовой у верха прокрутки — первым ребёнком прокручиваемого блока. */
  start: (el: HTMLElement | null) => void;
  /** Часовой у низа — последним ребёнком. */
  end: (el: HTMLElement | null) => void;
  /** Содержимое ушло под шапку: прокручено вниз. */
  above: boolean;
  /** Под подвалом ещё есть содержимое. */
  below: boolean;
}

const NONE = { above: false, below: false };

/**
 * Где черта у окна ([UI-75]): под шапкой — когда содержимое ушло под неё, над подвалом — когда под ним ещё
 * есть что прокручивать. Не обработчиком `scroll`: прокрутки может не быть вовсе, а смена шага меняет высоту
 * тела без единого события. Наблюдатель пересечения отвечает на любое из этого — часовой у края либо виден
 * в прокрутке, либо нет.
 */
export function useScrollEdges(): ScrollEdges {
  const [start, setStart] = useState<HTMLElement | null>(null);
  const [end, setEnd] = useState<HTMLElement | null>(null);
  const [edges, setEdges] = useState(NONE);

  useEffect(() => {
    if (!start || !end) return;
    const watch = new IntersectionObserver(entries => {
      setEdges(was => {
        let next = was;
        for (const e of entries) {
          next = e.target === start ? { ...next, above: !e.isIntersecting } : { ...next, below: !e.isIntersecting };
        }
        return next.above === was.above && next.below === was.below ? was : next;
      });
    }, { root: start.parentElement });
    watch.observe(start);
    watch.observe(end);
    return () => watch.disconnect();
  }, [start, end]);

  // Часовых нет — черт нет: прошлое наблюдение к снятому телу не относится.
  return { start: setStart, end: setEnd, ...(start && end ? edges : NONE) };
}
