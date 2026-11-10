import type { PointerEvent } from 'react';

/** Подсказка с полным текстом — только у обрезанной подписи. Атрибут ставится на наведении мимо React: он его
 *  не ведёт, и подпись не перерисовывается ради замера.
 *
 *  `cut` — подпись, которую мерить, когда наведение получает не она: строка-итог части счёта лежит под
 *  растянутой целью шапки (costs-invoice-part-header-toggle, D4), и подсказку держит сама цель. */
export const fullTextIfCut = (full: string, cut?: HTMLElement | null) => (e: PointerEvent<HTMLElement>) => {
  const el = e.currentTarget;
  const measured = cut === undefined ? el : cut;
  if (measured && measured.scrollWidth > measured.clientWidth) el.title = full;
  else el.removeAttribute('title');
};
