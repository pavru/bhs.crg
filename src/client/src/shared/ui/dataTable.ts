/**
 * Общий MD3-стиль таблиц данных (issue #154): карточка со скруглением, sticky-шапка на
 * surface-container, тонкие бордеры (outline-variant), hover-строки, числовые колонки
 * с tabular-nums. Применяется подстановкой классов в существующую разметку `<table>`.
 *
 * Важно: sticky-шапка требует `border-separate border-spacing-0` на таблице (иначе граница
 * шапки уезжает при скролле) — это уже в `dtTable`.
 */

/** Обёртка-карточка со скроллом. */
export const dtCard = 'overflow-auto rounded-xl border border-stroke bg-surface';

/** Сама таблица (border-separate — для корректной sticky-шапки). */
export const dtTable = 'w-full border-separate border-spacing-0 text-sm';

/** Ячейка шапки: липкая, на surface-container, вторичный текст. */
export const dtTh =
  'sticky top-0 z-10 bg-muted text-left text-xs font-medium text-fg3 ' +
  'px-3 h-[42px] align-middle border-b border-stroke whitespace-nowrap';

/** Ячейка тела: тонкая нижняя граница, средняя плотность. */
export const dtTd = 'px-3 py-2 border-b border-stroke align-middle';

/** Класс строки: hover-подсветка. */
export const dtRow = 'transition-colors hover:bg-black/[.03] dark:hover:bg-white/[.05]';

/** Числовая колонка: выравнивание вправо + tabular-nums. */
export const dtNum = 'text-right tabular-nums';
