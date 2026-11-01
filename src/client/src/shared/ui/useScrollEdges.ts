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
  /** Часовые стоят и наблюдаются. Нет — о прокрутке неизвестно, а не «прокручивать нечего». */
  known: boolean;
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
  // Чьи это края — запомнено вместе с ними: сменились часовые (снят или открыт шаг), и ответ прежнего
  // наблюдателя к новому телу не относится, пока новый не ответит сам.
  const [edges, setEdges] = useState<{ of: HTMLElement | null; above: boolean; below: boolean }>({ of: null, ...NONE });

  useEffect(() => {
    if (!start || !end) return;
    const watch = new IntersectionObserver(entries => {
      setEdges(was => {
        let next = was.of === start ? was : { of: start, ...NONE };
        for (const e of entries) {
          next = e.target === start ? { ...next, above: !e.isIntersecting } : { ...next, below: !e.isIntersecting };
        }
        return next.of === was.of && next.above === was.above && next.below === was.below ? was : next;
      });
    }, { root: start.parentElement });
    watch.observe(start);
    watch.observe(end);
    return () => watch.disconnect();
  }, [start, end]);

  const known = !!start && !!end;
  const own = known && edges.of === start;
  return { start: setStart, end: setEnd, above: own && edges.above, below: own && edges.below, known };
}
