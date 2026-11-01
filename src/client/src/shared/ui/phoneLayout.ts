/**
 * Размеры того, что на телефоне стоит у нижнего края (issue #1434), — одно место для тех, кто должен
 * их обходить: уведомления и конец прокручиваемых списков (ревью PR #1502).
 */

/** Нижняя навигация: `h-20` у `BottomNav`. Под ней — безопасная зона (полоска «домой» iPhone). */
export const BOTTOM_NAV_HEIGHT = 'calc(5rem + env(safe-area-inset-bottom))';

/** Плавающая кнопка с отступом от края: `h-14` и `bottom-4` у `PrimaryFab`. */
export const FAB_CLEARANCE = '4.5rem';

/**
 * Насколько поднять уведомления над краем: над нижней навигацией и плавающей кнопкой — из тех, что
 * сейчас есть. Ничего нет (вход, рабочий стол) — `null`, и уведомление у края.
 */
export function snackbarLift({ bottomNav, fab }: { bottomNav: boolean; fab: boolean }): string | null {
  const parts = [bottomNav && BOTTOM_NAV_HEIGHT, fab && FAB_CLEARANCE].filter((p): p is string => !!p);
  return parts.length ? `calc(${parts.join(' + ')})` : null;
}
