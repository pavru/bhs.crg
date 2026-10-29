/**
 * Общий MD3-стиль таблиц данных (issue #154): карточка со скруглением, sticky-шапка на
 * surface-container, тонкие бордеры (outline-variant), hover-строки, числовые колонки
 * с tabular-nums. Применяется подстановкой классов в существующую разметку `<table>`.
 *
 * Важно: sticky-шапка требует `border-separate border-spacing-0` на таблице (иначе граница
 * шапки уезжает при скролле) — это уже в `dtTable`.
 */

/** Обёртка-карточка со скроллом. */
export const dtCard = 'overflow-auto rounded-xl border border-outline-variant bg-surface';

/**
 * Сама таблица (border-separate — для корректной sticky-шапки). Высоту шапки она держит переменной
 * `--dt-head`: по ней же второй этаж двухэтажной шапки липнет под первым (DataGrid), и число не
 * повторяется в двух местах.
 */
export const dtTable = 'w-full border-separate border-spacing-0 text-sm [--dt-head:42px]';

const dtThBase =
  'sticky top-0 z-10 bg-surface-container text-left text-xs font-medium text-on-surface-variant ' +
  'align-middle border-b border-outline-variant whitespace-nowrap';

/** Ячейка шапки: липкая, на surface-container, вторичный текст. */
export const dtTh = `${dtThBase} px-3 h-(--dt-head)`;

/** Ячейка тела: тонкая нижняя граница, средняя плотность. */
export const dtTd = 'px-3 py-2 border-b border-outline-variant align-middle';

/**
 * Плотная сетка — диалоги и боковые панели (#1354): там сетка одна из нескольких частей, и шапка
 * в 42 px забирала бы место у соседей.
 */
export const dtTableCompact = 'w-full border-separate border-spacing-0 text-xs [--dt-head:2rem]';
export const dtThCompact = `${dtThBase} px-2 h-(--dt-head)`;
export const dtTdCompact = 'px-2 py-1 border-b border-outline-variant align-middle';

/**
 * Клетка сетки правки (EditGrid): плотная, но прижата к верху — рядом с полем стоит то, что
 * растёт вниз (поле с пометкой, сумма с примечанием), и поля строки обязаны стоять на одной линии.
 */
export const dtTdEdit = 'px-2 py-1 border-b border-outline-variant align-top';

/**
 * Сетка правки «листом» (EditGrid, `sheet`): поле во всю клетку без отступов и линии между колонками —
 * как в электронной таблице. Клетка с фокусом подсвечена: у поля без рамки другого признака нет.
 */
export const dtTdSheet = 'p-0 h-7 border-b border-r border-outline-variant align-middle focus-within:bg-primary-container';

/**
 * Ширина таблицы с ширинами колонок, заданными числом (EditGrid, `size`): таблица шириной по сумме
 * колонок, но не уже карточки. При `w-full` фиксированная раскладка делила бы остаток между
 * колонками, и заданная руками ширина не держалась бы.
 */
export const dtTableSized = 'table-fixed w-max! min-w-full';

/**
 * Класс строки: слой состояния MD3 ([UI-64]) — под курсором 8 % цвета текста, перетаскиваемая
 * (`data-dragging`) — 16 %. Своей прозрачности у таблиц нет (выбор владельца 29.10.2026).
 */
export const dtRow = 'state-layer';

/** Числовая колонка: выравнивание вправо + tabular-nums. */
export const dtNum = 'text-right tabular-nums';
