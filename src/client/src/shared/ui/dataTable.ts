/**
 * Общий стиль таблиц данных (issue #154; data table — расширение из Material 2 на токенах MD3, [UI-78]):
 * карточка outlined 12, липкая шапка без своей заливки, черты outline-variant, слой состояния у строк,
 * числовые колонки с tabular-nums. Применяется подстановкой классов в разметку `<table>`.
 *
 * Важно: sticky-шапка требует `border-separate border-spacing-0` на таблице (иначе граница
 * шапки уезжает при скролле) — это уже в `dtTable`.
 *
 * Плотность и фон — переменными, а не числами в классах клеток:
 * - `--dt-head` / `--dt-row` — высота шапки и НИЖНЯЯ граница высоты строки. Стоят на обёртке с прокруткой
 *   (`dtDensity*`): по ним же её `scroll-padding` — открытая строка, прокрученная в поле зрения, не уходит
 *   под липкую шапку, — и второй этаж двухэтажной шапки липнет под первым (DataGrid);
 * - `--dt-bg` — непрозрачный фон контейнера таблицы: страница (`index.css`), карточка (`dtCard`), окно и
 *   лист (Modal, SideSheet). Шапка липкая и стоит над строками, поэтому прозрачной быть не может;
 * - `--dt-row-bg` / `--dt-row-fg` — фон и текст строки (`dtRowTone`): закреплённая клетка берёт фон строки
 *   отсюда, а слой состояния — у самой строки (`background-image: inherit`).
 */
import { ICON_BUTTON_XS } from './buttonClasses';

/** Обёртка-карточка со скроллом: outlined card 12, свой фон — фон таблицы в ней. */
export const dtCard = 'overflow-auto rounded-md border border-outline-variant [--dt-bg:var(--color-surface)] bg-(--dt-bg)';

/**
 * Плотность таблицы данных по таблице D7 — классом на обёртку с прокруткой. Страница: шапка 56, строка
 * не ниже 48. Окно и боковой лист (`compact`, #1354): шапка 48, строка не ниже 40. Ниже нельзя (В5).
 * `group/dt` — по ней шапка узнаёт, что тело прокручено (`data-scrolled` у обёртки, ставит DataGrid).
 */
export const dtDensity = 'group/dt [--dt-head:56px] [--dt-row:48px] scroll-pt-(--dt-head)';
export const dtDensityCompact = 'group/dt [--dt-head:48px] [--dt-row:40px] scroll-pt-(--dt-head)';

/** Сама таблица (border-separate — для корректной sticky-шапки). */
export const dtTable = 'w-full border-separate border-spacing-0 text-body-medium';

/**
 * Шапка — без своей заливки: фон контейнера (`--dt-bg`), а над прокрученным телом — surface-container,
 * чтобы строки под ней не читались продолжением шапки. Подпись — title-small роли on-surface.
 */
const dtThBase =
  'sticky top-0 z-10 bg-(--dt-bg) group-data-[scrolled]/dt:bg-surface-container text-left text-title-small text-on-surface ' +
  'align-middle border-b border-outline-variant whitespace-nowrap h-(--dt-head)';

/** Ячейка шапки. */
export const dtTh = `${dtThBase} px-3`;

// Клетка — место XS кнопки-иконки ([UI-68], В7 = Б): класс места — в buttonClasses.ts.

/**
 * Ячейка тела: высота строки — минимум (`h-` у клетки таблицы не обрезает, а растит), отступ 4 px — кнопка
 * XS 32 и поле 40 дают ровно 40 / 48, длинное содержимое растёт вниз.
 */
export const dtTd = `px-3 py-1 h-(--dt-row) border-b border-outline-variant align-middle ${ICON_BUTTON_XS}`;

/** Плотная сетка — окна и боковые листы (#1354): теснее по горизонтали, высоты — от `dtDensityCompact`. */
export const dtThCompact = `${dtThBase} px-2`;
export const dtTdCompact = `px-2 py-1 h-(--dt-row) border-b border-outline-variant align-middle ${ICON_BUTTON_XS}`;

/**
 * «Линия» клетки, прижатой к верху (DataGrid с переносом): высотой со строку без отступов и черты,
 * содержимое по её центру. Однострочный текст стоит посередине строки, а не у верхней черты; длинное
 * растёт вниз, и строка вместе с ним. Внутри — блок `min-w-0 flex-1`, иначе обрезка и выравнивание
 * вправо потерялись бы в ширине по содержимому.
 */
export const dtLine = 'flex min-h-[calc(var(--dt-row)-9px)] items-center';

/**
 * Сетка правки (EditGrid) — до PR 2 sharedui-md3-14-data своя плотность: таблица с мелким текстом и
 * шапкой 2rem. Её классы ниже переходят на шкалу D7 вместе с полем `density="cell"`.
 */
export const dtTableCompact = 'w-full border-separate border-spacing-0 text-body-small [--dt-head:2rem]';

/**
 * Клетка сетки правки (EditGrid): плотная, но прижата к верху — рядом с полем стоит то, что
 * растёт вниз (поле с пометкой, сумма с примечанием), и поля строки обязаны стоять на одной линии.
 */
export const dtTdEdit = `px-2 py-1 border-b border-outline-variant align-top ${ICON_BUTTON_XS}`;

/**
 * Сетка правки «листом» (EditGrid, `sheet`): поле во всю клетку без отступов и линии между колонками —
 * как в электронной таблице. Клетка с фокусом подсвечена: у поля без рамки другого признака нет.
 */
export const dtTdSheet = `p-0 h-7 border-b border-r border-outline-variant align-middle focus-within:bg-primary-container ${ICON_BUTTON_XS}`;

/**
 * Ширина таблицы с ширинами колонок, заданными числом (EditGrid, `size`): таблица шириной по сумме
 * колонок, но не уже карточки. При `w-full` фиксированная раскладка делила бы остаток между
 * колонками, и заданная руками ширина не держалась бы.
 */
export const dtTableSized = 'table-fixed w-max! min-w-full';

/**
 * Класс строки: слой состояния MD3 ([UI-64]) — под курсором 8 % цвета текста, перетаскиваемая
 * (`data-dragging`) — 16 %. Своей прозрачности у таблиц нет (выбор владельца 29.10.2026). Фон и текст —
 * переменными тона строки (`dtRowTone`).
 */
export const dtRow = 'state-layer bg-(--dt-row-bg) text-(--dt-row-fg)';

/**
 * Тон строки — ровно один класс на строку: два класса на одну переменную спорили бы порядком в CSS.
 * Открытая — secondary-container (вариант А, 02.11.2026), помеченная — warning-container.
 */
export const dtRowTone = {
  plain: '[--dt-row-bg:var(--dt-bg)] [--dt-row-fg:var(--color-on-surface)]',
  open: '[--dt-row-bg:var(--color-secondary-container)] [--dt-row-fg:var(--color-on-secondary-container)]',
  warning: '[--dt-row-bg:var(--color-warning-container)] [--dt-row-fg:var(--color-on-warning-container)]',
} as const;

/**
 * Закреплённая клетка: непрозрачна (под ней уезжают соседние), фон — строки, а слой состояния строки
 * повторяет у себя — иначе наведение на закреплённых колонках не видно.
 */
export const dtPinnedCell = 'bg-(--dt-row-bg) [background-image:inherit]';

/** Числовая колонка: выравнивание вправо + tabular-nums. */
export const dtNum = 'text-right tabular-nums';
