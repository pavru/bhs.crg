import { useLayoutEffect, type RefObject, type UIEvent } from 'react';

// Прокрутка таблиц данных — общая у DataGrid и EditGrid (sharedui-md3-14): шапка над прокрученным телом и
// отступы прокрутки под липкие шапку и итог. Отдельным модулем: из файла с компонентом хук не отдают —
// горячая замена в разработке перезагружала бы страницу целиком.

/**
 * Тело прокручено под шапку — шапка получает surface-container (`group-data-[scrolled]/dt`). Атрибутом
 * прямо на обёртке, а не состоянием: перерисовывать всю сетку на каждый кадр прокрутки незачем.
 */
export function markScrolled(event: UIEvent<HTMLDivElement>) {
  const element = event.currentTarget;
  element.toggleAttribute('data-scrolled', element.scrollTop > 0);
}

/**
 * Отступы прокрутки обёртки под липкие шапку и итог: строка, прокрученная в поле зрения (фокус клавишей,
 * `scrollIntoView` открытой), не встаёт под них. Меряем, а не считаем из `--dt-head` / `--dt-row`: шапка
 * бывает в два этажа, итог — в две строки. Стилем на обёртку, как и отступы закреплённых колонок.
 */
export function useStickyPadding(table: RefObject<HTMLTableElement | null>, layout: string) {
  useLayoutEffect(() => {
    const element = table.current;
    const scroller = element?.parentElement;
    if (!element || !scroller) return;
    const parts = [element.tHead, element.tFoot].filter(part => part !== null);
    const measure = () => {
      scroller.style.setProperty('scroll-padding-top', `${element.tHead?.offsetHeight ?? 0}px`);
      scroller.style.setProperty('scroll-padding-bottom', `${element.tFoot?.offsetHeight ?? 0}px`);
    };
    measure();
    if (typeof ResizeObserver === 'undefined') return;
    const observer = new ResizeObserver(measure);
    parts.forEach(part => observer.observe(part));
    return () => observer.disconnect();
  }, [table, layout]);
}
