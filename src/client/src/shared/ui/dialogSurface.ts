/**
 * Вид окна — basic dialog MD3 (sharedui-md3-13, [UI-75]). Одно место на `Modal`, `ConfirmDialog`, вопрос о
 * несохранённом и голые `Dialog.Root` экранов: окно отличают затемнение и возвышение, а не рамка — рамки
 * у окна нет, затемнение не размыто.
 */

/** Затемнение под окном. Слой (`z-*`) ставит окно: вопрос над окном лежит выше его самого. */
export const DIALOG_SCRIM = 'fixed inset-0 bg-scrim/32';

/** Поверхность окна: `surface-container-high`, радиус 28, уровень 3. */
export const DIALOG_SURFACE = 'rounded-xl bg-surface-container-high shadow-elevation-3';

/** Заголовок окна. */
export const DIALOG_TITLE = 'text-headline-small text-on-surface';

/**
 * Окно «да / нет» (384) по центру с отступами 24 от краёв экрана: на телефоне оно остаётся окном, а не
 * растягивается во весь экран.
 */
export const COMPACT_DIALOG_PLACE = 'fixed inset-x-6 top-1/2 -translate-y-1/2 mx-auto max-w-sm max-h-[calc(100dvh-3rem)]';

/** Лист снизу (bottom sheet): верх 28, `surface-container-low`, уровень 1. */
export const BOTTOM_SHEET_SURFACE = 'rounded-t-xl bg-surface-container-low shadow-elevation-1';

/** Заголовок листа. */
export const SHEET_TITLE = 'text-title-large text-on-surface';
