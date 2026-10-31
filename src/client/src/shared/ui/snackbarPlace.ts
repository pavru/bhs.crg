import { useEffect, useLayoutEffect, type RefObject } from 'react';

/**
 * Место уведомлений ([UI-71]) — переменными на корне документа: поле уведомлений (`RT.Viewport`) стоит
 * вне оболочки и её раскладки не видит, а оболочка одна знает, что у краёв.
 *
 * - `--snackbar-inset` — ширина рейла с подпанелью: на рабочем столе уведомление стоит у ведущего края
 *   СОДЕРЖИМОГО, а не окна. Меряется сам рейл, а не складываются ширины: подпанель скрывают, а на
 *   телефоне рейла нет (ширина 0).
 * - `--snackbar-lift` — подъём над нижней навигацией и плавающей кнопкой на телефоне (`snackbarLift`).
 */
export function useSnackbarPlace(rail: RefObject<HTMLElement | null>, lift: string | null) {
  useLayoutEffect(() => {
    const el = rail.current;
    if (!el) return;
    const root = document.documentElement.style;
    const set = () => root.setProperty('--snackbar-inset', `${el.getBoundingClientRect().width}px`);
    set();
    if (typeof ResizeObserver === 'undefined') return () => root.removeProperty('--snackbar-inset');
    const ro = new ResizeObserver(set);
    ro.observe(el);
    return () => { ro.disconnect(); root.removeProperty('--snackbar-inset'); };
  }, [rail]);

  useEffect(() => {
    if (!lift) return;
    const root = document.documentElement.style;
    root.setProperty('--snackbar-lift', lift);
    return () => { root.removeProperty('--snackbar-lift'); };
  }, [lift]);
}
