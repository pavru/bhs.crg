/**
 * Обрезан ли текст в клетке таблицы (sharedui-truncated-cell-tooltip, [UI-52] [UI-53]) — измеритель для
 * показа полного текста сеткой (`TruncatedTips`). Чистые функции над DOM: зовутся один раз, при показе, а не
 * на каждое движение указателя, и наблюдателей за размером не держат — «колонку расширили» работает само,
 * потому что признак меряется заново при каждом показе.
 *
 * <p>Обрезан элемент, у которого два признака сразу (разбор Архитектора, А4): автор ОБЪЯВИЛ обрезку —
 * `overflow` не `visible` и многоточие (`text-overflow: ellipsis`) либо `-webkit-line-clamp`, — и она
 * СЛУЧИЛАСЬ: содержимое шире или выше места. По вычисленному стилю, а не по классам: классы теряют
 * `overflow-hidden text-ellipsis` и произвольные `[…]`. Фиксированная раскладка сама не обрезает — у клетки
 * нет `overflow-hidden`, текст без `truncate` наезжает на соседа, и показывать тут нечего.</p>
 */

/** То из вычисленного стиля, что нужно измерителю: отдельным типом — чтобы проверять подделками. */
export interface ClipStyle {
  overflow: string;
  overflowX: string;
  overflowY: string;
  textOverflow: string;
  webkitLineClamp: string;
}

/** Как обрезает элемент: многоточием в строку, `line-clamp` по высоте, никак. */
export function clipKind(style: ClipStyle): 'ellipsis' | 'clamp' | null {
  // Сокращение `overflow` и обе оси: тестовый DOM оси из сокращения не выводит, браузер — выводит.
  const hidden = [style.overflow, style.overflowX, style.overflowY].some(v => !!v && v !== 'visible');
  if (!hidden) return null;
  if (style.webkitLineClamp && style.webkitLineClamp !== 'none') return 'clamp';
  if (style.textOverflow === 'ellipsis') return 'ellipsis';
  return null;
}

/** Обрезка объявлена у элемента. */
function declares(el: Element): 'ellipsis' | 'clamp' | null {
  return clipKind(getComputedStyle(el) as unknown as ClipStyle);
}

/** Обрезка случилась: содержимое не поместилось. */
function clipped(el: HTMLElement, kind: 'ellipsis' | 'clamp'): boolean {
  return kind === 'clamp' ? el.scrollHeight > el.clientHeight : el.scrollWidth > el.clientWidth;
}

/**
 * Поле ввода и его подобия: у поля `scrollWidth > clientWidth` выходит само, а текст в нём прокручивается
 * при фокусе — показ ему не нужен (Non-Goals). Список ролей — те, что носят наши поля.
 */
const FIELD = 'input, textarea, select, [contenteditable=""], [contenteditable="true"], [role="combobox"], [role="textbox"], [role="spinbutton"]';

/**
 * Свой показ у элемента: `Tooltip`, `RichTooltip`, `title` у `Button` ставят метку `data-tooltip`. Сетка
 * уступает элементу с меткой — у него самого или у предка в клетке: двух подсказок не бывает никогда, выбор
 * остаётся за экраном (разбор, А7 и Д4).
 */
const OWN_TIP = '[data-tooltip]';

/** Метка внутри клетки — у элемента или у предка до клетки включительно. */
function inside(el: Element, selector: string, cell: Element): boolean {
  const found = el.closest(selector);
  return !!found && cell.contains(found);
}

/**
 * Обрезанный элемент под указателем: от `target` вверх до клетки — ближайший, кто объявил обрезку; он и
 * решает (обрезан — показ, нет — ничего). Указатель на отступе клетки, по дороге никого — запасной поиск
 * в потомках, но только если обрезающий потомок в клетке ОДИН: из нескольких неясно, чей текст показывать.
 * null — показывать нечего: не обрезано, поле, свой показ.
 */
export function truncatedAt(target: Element, cell: HTMLElement): HTMLElement | null {
  if (!cell.contains(target) || inside(target, FIELD, cell) || inside(target, OWN_TIP, cell)) return null;
  for (let el: Element | null = target; el && cell.contains(el); el = el.parentElement) {
    const kind = declares(el);
    if (kind) return el instanceof HTMLElement && clipped(el, kind) && accept(el, cell) ? el : null;
  }
  if (target !== cell) return null;
  const declared = [...cell.querySelectorAll<HTMLElement>('*')].filter(el => declares(el));
  if (declared.length !== 1) return null;
  const [only] = declared;
  return clipped(only, declares(only)!) && accept(only, cell) ? only : null;
}

/**
 * Обрезанный элемент у фокуса: сам фокусируемый (ссылка, кнопка) или обрезающий потомок в нём — первый
 * обрезанный. Фокус на самой строке (`<tr tabIndex=0>`) клеткой не считается — там показа нет.
 */
export function truncatedIn(focused: HTMLElement, cell: HTMLElement): HTMLElement | null {
  if (!cell.contains(focused) || inside(focused, FIELD, cell) || inside(focused, OWN_TIP, cell)) return null;
  for (const el of [focused, ...focused.querySelectorAll<HTMLElement>('*')]) {
    const kind = declares(el);
    if (kind && clipped(el, kind) && accept(el, cell)) return el;
  }
  return null;
}

/** Внутри обрезанного нет своего показа и поля — иначе подсказки две или показ у поля. */
function accept(el: HTMLElement, cell: HTMLElement): boolean {
  return !el.querySelector(`${FIELD}, ${OWN_TIP}`) && cell.contains(el);
}

/**
 * Полный текст обрезанного: `textContent`, пробелы сжаты; переносы строк остаются — в предпросмотре вставки
 * из буфера они осмысленны (Д2). Значки (svg) текста не дают.
 */
export function fullText(el: HTMLElement): string {
  return (el.textContent ?? '').replace(/[^\S\n]+/g, ' ').replace(/ *\n */g, '\n').trim();
}
