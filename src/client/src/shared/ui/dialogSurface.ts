/**
 * Вид окна — basic dialog MD3 (sharedui-md3-13, [UI-75]). Одно место на `Modal`, `ConfirmDialog`, вопрос о
 * несохранённом и голые `Dialog.Root` экранов: окно отличают затемнение и возвышение, а не рамка — рамки
 * у окна нет, затемнение не размыто.
 */

/**
 * Затемнение под окном — на одном слое с окнами (z-50): что открыто позже, лежит выше по порядку порталов. Так
 * окно над окном (подтверждение, вопрос, выбор типа) затемняет нижнее: поверхность у них одна и рамки нет,
 * и без затемнения верхнее читалось бы частью нижнего. Уведомления (z-100) остаются над всеми.
 */
export const DIALOG_SCRIM = 'fixed inset-0 z-50 bg-scrim/32';

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
