import type { PointerEvent } from 'react';

/** Подсказка с полным текстом — только у обрезанной подписи. Атрибут ставится на наведении мимо React: он его
 *  не ведёт, и подпись не перерисовывается ради замера.
 *
 *  `measure` — какую подпись мерить, когда наведение получает не она сама: строка-итог части счёта лежит под
 *  растянутой целью шапки (costs-invoice-part-header-toggle, D4), и подсказку держит цель. Без текста (`full`
 *  пуст) подсказка снимается: у раскрытой части не должна оставаться подсказка свёрнутой. */
export const fullTextIfCut = (full: string | undefined, measure: (target: HTMLElement) => HTMLElement | null = t => t) =>
  (e: PointerEvent<HTMLElement>) => {
    const el = e.currentTarget;
    const cut = full ? measure(el) : null;
    if (full && cut && cut.scrollWidth > cut.clientWidth) el.title = full;
    else el.removeAttribute('title');
  };
