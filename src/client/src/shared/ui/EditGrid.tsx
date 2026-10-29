import { useLayoutEffect, useRef, type CSSProperties, type DragEvent, type KeyboardEvent, type ReactNode } from 'react';
import { Icon } from './Icon';
import { Add, Delete } from './icons';
import { Button } from './Button';
import { dtCard, dtNum, dtRow, dtTableCompact, dtTableSized, dtTdEdit, dtTdSheet, dtThCompact } from './dataTable';
import type { DataGridDrag } from './DataGrid';
import { Tooltip } from './Tooltip';

/**
 * Сетка правки (#1354): черновик строк, которые правят вместе и сохраняют целиком, — строки
 * накладной, части разноски, строки счёта. Вторая сетка рядом с `DataGrid`, а не его режим: модель
 * правки другая (фокус в клетке, переход по клеткам, черновик), и в одном компоненте щелчок «открыть
 * строку» спорил бы со щелчком в поле (docs/UI.md, «Таблицы»; решение владельца 16.10.2026).
 *
 * Колонки объявляются так же, как у `DataGrid`: `key`, `label`, `width`, `align`. Клетку рисует
 * экран (`cell`) — сетка не знает, что в ней: поле, выбор, текст запертой строки. Черновик тоже
 * держит экран: сетка только сообщает «добавить» и «удалить» и рисует итог, который он посчитал.
 *
 * Что сетка делает сама:
 * - **переход по клеткам с клавиатуры**: ↑↓ — та же колонка соседней строки, ←→ — соседняя клетка
 *   с полем, у текстового поля — только с края текста (иначе стрелка двигает каретку). Стрелки
 *   уступаются открытому списку (`role="combobox"` с `aria-expanded="true"`: ими листают пункты) и
 *   контролу, который уже забрал клавишу себе (`preventDefault`, как триггер `Select`). У закрытого
 *   списка подсказки (`SuggestField`) ↑↓ — переход по строкам;
 * - **фокус после добавления и удаления**: новая строка получает фокус в первом поле, после удаления
 *   фокус встаёт на ту же кнопку соседней строки — иначе он падал бы на `body`, и с клавиатуры
 *   приходилось бы идти от начала страницы;
 * - **пустоту**: строк нет — сетка говорит это строкой во всю ширину, и кнопка «добавить» остаётся
 *   под ней, на своём месте.
 */

export interface EditGridColumn<Row> {
  key: string;
  /** Подпись в шапке. Подпись поля в клетке — `aria-label` самого поля: над полем её нет. */
  label: string;
  /** Что стоит в шапке вместо подписи — когда рядом с ней нужно ещё что-то. */
  header?: ReactNode;
  /** Ширина колонки классом Tailwind (`w-28`); нет — колонка делит остаток. */
  width?: string;
  /**
   * Ширина в пикселях — когда её задаёт человек (растягивает колонку руками). Есть хоть у одной
   * колонки — раскладка фиксированная, и таблица шириной по сумме колонок.
   */
  size?: number;
  /** Клетка — заголовок строки (`<th scope="row">`): по ней читалка экрана называет строку. */
  rowHeader?: boolean;
  /**
   * Колонка закреплена у края и не уезжает при прокрутке вбок: `start` — первая (название строки),
   * `end` — последняя (остаток, ради которого сетку открыли). Закрепляется только крайняя колонка.
   */
  pin?: 'start' | 'end';
  align?: 'left' | 'right';
  /** Подсказка шапки. */
  hint?: string;
  /** Клетка строки: поле правки, выбор или текст. */
  cell: (row: Row, index: number) => ReactNode;
  /**
   * Что стоит под колонкой в итоговой строке. Итоговая строка есть, когда итог есть хоть у одной
   * колонки: остаток «не разнесено», сумма строк.
   */
  total?: () => ReactNode;
}

export interface EditGridAdd {
  /** Подпись кнопки: «Добавить строку», «Добавить часть». */
  label: string;
  onAdd: () => void;
}

/** Поле, которое принимает фокус: то, до чего дошёл бы Tab. */
const FOCUSABLE = 'input:not([disabled]), textarea:not([disabled]), button:not([disabled]), '
  + '[role="combobox"]:not([aria-disabled="true"]), [tabindex]:not([tabindex="-1"])';

export function EditGrid<Row>({
  columns, rows, rowKey, label, numbered = false, add, onRemove, removeLabel, empty = 'Строк нет',
  framed = true, className = '', minWidth = '', rowData, sheet = false, selected, drag,
}: {
  columns: EditGridColumn<Row>[];
  rows: Row[];
  /** Ключ строки черновика — устойчивый, не номер: строки удаляют из середины. */
  rowKey: (row: Row, index: number) => string;
  /** Имя сетки для читалки экрана: «Строки накладной», «Части разноски». */
  label?: string;
  /** Первая колонка — номер строки («№»). По нему строку называют подписи полей и отказы. */
  numbered?: boolean;
  /** Добавить строку — кнопкой под сеткой. Нет — добавлять нельзя (нет права, строки заперты). */
  add?: EditGridAdd;
  /** Удалить строку — кнопкой в её конце. Нет — колонки удаления нет вовсе. */
  onRemove?: (row: Row, index: number) => void;
  /** Подпись кнопки удаления; по умолчанию «Удалить строку N». */
  removeLabel?: (row: Row, index: number) => string;
  /** Что сказать, когда строк нет. */
  empty?: ReactNode;
  /** Своя рамка-карточка; false — сетка внутри чужой карточки (иначе рамка в рамке). */
  framed?: boolean;
  /** Классы прокручиваемой карточки — например, её наибольшая высота (`max-h-[65vh]`). */
  className?: string;
  /**
   * «Лист»: поле во всю клетку, без отступов, с линиями между колонками — для сетки, где клетка и
   * есть поле (строки массива в документе). Поля такой сетки рисуются без рамки.
   */
  sheet?: boolean;
  /** Строка выбрана (флажком в своей колонке) — она подсвечена. */
  selected?: (row: Row, index: number) => boolean;
  /** Строки переставляют перетаскиванием — как у `DataGrid`: что тащат и куда ляжет, знает экран. */
  drag?: DataGridDrag;
  /**
   * Наименьшая ширина таблицы классом (`min-w-[70rem]`): колонок много, и в узком окне сетка
   * прокручивается вбок, а не сжимает поля до нечитаемых.
   */
  minWidth?: string;
  /** Атрибуты `data-*` строки — якорь, по которому к строке ведёт ссылка из перечня («строка 3»). */
  rowData?: (row: Row, index: number) => Record<`data-${string}`, string>;
}) {
  const table = useRef<HTMLTableElement>(null);
  const addButton = useRef<HTMLButtonElement>(null);
  // Куда поставить фокус, когда строки перерисуются: добавленная строка либо место удалённой.
  const pendingFocus = useRef<{ kind: 'added' } | { kind: 'removed'; index: number } | null>(null);

  const removable = onRemove !== undefined;
  const hasTotal = columns.some(c => c.total);
  const span = columns.length + (numbered ? 1 : 0) + (removable ? 1 : 0);

  useLayoutEffect(() => {
    const wanted = pendingFocus.current;
    if (!wanted || !table.current) return;
    pendingFocus.current = null;
    if (wanted.kind === 'added') {
      const row = table.current.querySelector<HTMLElement>(`tbody tr[data-row="${rows.length - 1}"]`);
      row?.querySelector<HTMLElement>(`[data-cell] :is(${FOCUSABLE})`)?.focus();
      return;
    }
    const next = Math.min(wanted.index, rows.length - 1);
    const button = next >= 0
      ? table.current.querySelector<HTMLElement>(`button[data-remove="${next}"]`)
      : null;
    (button ?? addButton.current)?.focus();
  }, [rows.length]);

  function focusCell(r: number, c: number): boolean {
    const cell = table.current?.querySelector<HTMLElement>(`[data-cell][data-r="${r}"][data-c="${c}"]`);
    const control = cell?.querySelector<HTMLElement>(FOCUSABLE);
    if (!control) return false;
    control.focus();
    if (control instanceof HTMLInputElement && control.type !== 'checkbox') control.select();
    return true;
  }

  function onKeyDown(e: KeyboardEvent) {
    if (e.defaultPrevented || e.altKey || e.ctrlKey || e.metaKey || e.shiftKey) return;
    const target = e.target as HTMLElement;
    // Событие из выпадающего списка, вынесенного порталом, всплывает сюда по дереву React — но в
    // клетке он не лежит, и его стрелки сетке не принадлежат.
    const cell = target.closest<HTMLElement>('[data-cell]');
    if (!cell || !table.current?.contains(cell)) return;
    const r = Number(cell.dataset.r);
    const c = Number(cell.dataset.c);

    if (e.key === 'ArrowUp' || e.key === 'ArrowDown') {
      // Уступаем только ОТКРЫТОМУ списку: у поля с подсказкой (`SuggestField`) список закрыт почти
      // всегда, и уступка по одной роли отняла бы у ячейки переход по строкам. Триггер `Select`
      // открывается на ↑↓ сам и зовёт preventDefault — его отсекает проверка выше.
      if (target.getAttribute('role') === 'combobox' && target.getAttribute('aria-expanded') === 'true') return;
      if (target instanceof HTMLTextAreaElement) return;
      const next = e.key === 'ArrowUp' ? r - 1 : r + 1;
      if (next >= 0 && next < rows.length && focusCell(next, c)) e.preventDefault();
      return;
    }
    if (e.key !== 'ArrowLeft' && e.key !== 'ArrowRight') return;

    const back = e.key === 'ArrowLeft';
    if (!atEdge(target, back)) return;
    // Соседняя клетка без поля (сумма, посчитанная формой) пропускается: стрелка ведёт к следующему полю.
    for (let next = back ? c - 1 : c + 1; next >= 0 && next < columns.length; next += back ? -1 : 1) {
      if (focusCell(r, next)) { e.preventDefault(); return; }
    }
  }

  const sized = columns.some(c => c.size !== undefined);
  const td = sheet ? dtTdSheet : dtTdEdit;
  const th = (extra = '') => `${dtThCompact} ${sheet ? 'border-r' : ''} ${extra}`;
  // Закреплённая клетка липнет к своему краю и лежит НАД соседями: слой задан стилем, а не классом —
  // у шапки уже есть свой класс слоя, и два класса на одно свойство спорили бы порядком в CSS.
  const pinClass = (c: EditGridColumn<Row>) => (c.pin === 'start' ? 'sticky border-r'
    : c.pin === 'end' ? 'sticky border-l' : '');
  const pinStyle = (c: EditGridColumn<Row>, layer: number): CSSProperties | undefined => (c.pin === 'start'
    ? { left: 0, zIndex: layer } : c.pin === 'end' ? { right: 0, zIndex: layer } : undefined);
  const sizeStyle = (c: EditGridColumn<Row>): CSSProperties | undefined => (c.size === undefined ? undefined : { width: c.size });

  return (
    <div className="space-y-2">
      <div className={`${framed ? dtCard : 'overflow-auto'} ${className}`}>
        <table ref={table} aria-label={label} onKeyDown={onKeyDown}
          className={`${dtTableCompact} ${sized ? dtTableSized : ''} ${minWidth}`}>
          <thead>
            <tr>
              {numbered && <th className={th('w-8')}>№</th>}
              {columns.map(c => (
                <Tooltip key={c.key} content={c.hint}>
                  <th style={{ ...sizeStyle(c), ...pinStyle(c, 30) }}
                    className={th(`${c.width ?? ''} ${c.align === 'right' ? 'text-right' : ''} ${pinClass(c)}`)}>
                    {c.header ?? c.label}
                  </th>
                </Tooltip>
              ))}
              {removable && <th className={th('w-8')}><span className="sr-only">Удалить</span></th>}
            </tr>
          </thead>
          <tbody>
            {rows.length === 0 ? (
              <tr>
                <td colSpan={span} className={`${dtTdEdit} py-3 text-center text-on-surface-variant`}>{empty}</td>
              </tr>
            ) : rows.map((row, i) => {
              const chosen = selected?.(row, i) ?? false;
              // Черта там, куда ляжет тащимая строка: над строкой либо под последней. Тенью (утилита
              // drop-mark-*, index.css), а не рамкой — рамка сдвинула бы строки на её толщину, и строка
              // уехала бы из-под курсора.
              const dropMark = !drag || drag.dragging === null ? ''
                : drag.target === i ? 'drop-mark-before'
                : drag.target === rows.length && i === rows.length - 1 ? 'drop-mark-after'
                : '';
              return (
                <tr key={rowKey(row, i)} data-row={i} {...rowData?.(row, i)}
                  className={`${dtRow} ${chosen ? 'bg-primary-container' : ''} ${dropMark}`}
                  data-dragging={drag?.dragging === i || undefined}
                    {...(drag ? {
                    onDragOver: (e: DragEvent) => drag.onOver(e, i),
                    onDrop: drag.onDrop,
                  } : {})}>
                  {numbered && <th scope="row" className={`${td} text-left font-normal text-on-surface-variant tabular-nums`}>{i + 1}</th>}
                  {columns.map((c, ci) => {
                    const Cell = c.rowHeader ? 'th' : 'td';
                    return (
                      <Cell key={c.key} data-cell data-r={i} data-c={ci} scope={c.rowHeader ? 'row' : undefined}
                        style={pinStyle(c, 5)}
                        className={`${td} text-on-surface ${c.rowHeader ? 'text-left font-normal' : ''} `
                          + `${c.align === 'right' ? dtNum : ''} ${pinClass(c)} ${c.pin ? (chosen ? 'bg-primary-container' : 'bg-surface') : ''}`}>
                        {c.cell(row, i)}
                      </Cell>
                    );
                  })}
                  {removable && (
                    <td className={`${td} text-center`}>
                      <RemoveButton index={i} label={removeLabel?.(row, i) ?? `Удалить строку ${i + 1}`}
                        onClick={() => {
                          pendingFocus.current = { kind: 'removed', index: i };
                          onRemove(row, i);
                        }} />
                    </td>
                  )}
                </tr>
              );
            })}
          </tbody>
          {hasTotal && (
            <tfoot>
              <tr>
                {numbered && <td className={FOOT} />}
                {columns.map(c => (
                  <td key={c.key} style={pinStyle(c, 30)}
                    className={`${FOOT} ${c.align === 'right' ? dtNum : ''} ${pinClass(c)}`}>{c.total?.()}</td>
                ))}
                {removable && <td className={FOOT} />}
              </tr>
            </tfoot>
          )}
        </table>
      </div>
      {add && (
        <Button ref={addButton} size="sm" variant="outlined" icon={<Icon icon={Add} size={18} />}
          onClick={() => {
            pendingFocus.current = { kind: 'added' };
            add.onAdd();
          }}>
          {add.label}
        </Button>
      )}
    </div>
  );
}

/** Итоговая строка: липнет к низу, как у `DataGrid`, — итог виден при любой длине черновика. */
const FOOT = 'sticky bottom-0 z-10 bg-surface-container border-t border-outline-variant px-2 py-1.5 text-xs text-on-surface-variant align-top';

function RemoveButton({ index, label, onClick }: { index: number; label: string; onClick: () => void }) {
  return (
    <Tooltip content={label}>
      <button type="button" data-remove={index} onClick={onClick} aria-label={label}
        className="rounded-xs p-0.5 text-on-surface-variant state-layer [--_state-color:var(--color-error)] focus-ring">
        <Icon icon={Delete} size={18} aria-hidden />
      </button>
    </Tooltip>
  );
}

/**
 * Каретка у края текста — стрелка уходит из поля, а не двигает её. Поле без каретки (кнопка, флажок)
 * всегда «у края». Выделенный текст — не край: первая стрелка снимает выделение, как в любом поле,
 * и только вторая уводит в соседнюю клетку.
 */
function atEdge(target: HTMLElement, back: boolean): boolean {
  if (!(target instanceof HTMLInputElement || target instanceof HTMLTextAreaElement)) return true;
  const { selectionStart: start, selectionEnd: end, value } = target;
  if (start === null || end === null) return true;
  return back ? start === 0 && end === 0 : start === value.length && end === value.length;
}
