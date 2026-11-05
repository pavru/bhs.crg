import type { PointerEvent } from 'react';

/** Подсказка с полным текстом — только у обрезанной подписи. Атрибут ставится на наведении мимо React: он его
 *  не ведёт, и подпись не перерисовывается ради замера. */
export const fullTextIfCut = (full: string) => (e: PointerEvent<HTMLElement>) => {
  const el = e.currentTarget;
  if (el.scrollWidth > el.clientWidth) el.title = full;
  else el.removeAttribute('title');
};
