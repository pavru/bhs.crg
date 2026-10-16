import { useLayoutEffect, useRef, type KeyboardEvent, type ReactNode } from 'react';
import { Plus, Trash2 } from 'lucide-react';
import { Button } from './Button';
import { dtCard, dtNum, dtRow, dtTableCompact, dtTdEdit, dtThCompact } from './dataTable';

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
 *   с полем, у текстового поля — только с края текста (иначе стрелка двигает каретку). Выпадающие
 *   списки (`role="combobox"`) свои стрелки оставляют себе: ими листают пункты;
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
  framed = true, className = '', minWidth = '', rowData,
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
  className?: string;
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
      row?.querySelector<HTMLElement>(`td[data-cell] :is(${FOCUSABLE})`)?.focus();
      return;
    }
    const next = Math.min(wanted.index, rows.length - 1);
    const button = next >= 0
      ? table.current.querySelector<HTMLElement>(`button[data-remove="${next}"]`)
      : null;
    (button ?? addButton.current)?.focus();
  }, [rows.length]);

  function focusCell(r: number, c: number): boolean {
    const cell = table.current?.querySelector<HTMLElement>(`td[data-r="${r}"][data-c="${c}"]`);
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
    const cell = target.closest<HTMLElement>('td[data-cell]');
    if (!cell || !table.current?.contains(cell)) return;
    const r = Number(cell.dataset.r);
    const c = Number(cell.dataset.c);

    if (e.key === 'ArrowUp' || e.key === 'ArrowDown') {
      if (target.getAttribute('role') === 'combobox' || target instanceof HTMLTextAreaElement) return;
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

  const th = (extra = '') => `${dtThCompact} ${extra}`;

  return (
    <div className={`space-y-2 ${className}`}>
      <div className={framed ? dtCard : 'overflow-auto'}>
        <table ref={table} aria-label={label} onKeyDown={onKeyDown} className={`${dtTableCompact} ${minWidth}`}>
          <thead>
            <tr>
              {numbered && <th className={th('w-8')}>№</th>}
              {columns.map(c => (
                <th key={c.key} title={c.hint}
                  className={th(`${c.width ?? ''} ${c.align === 'right' ? 'text-right' : ''}`)}>
                  {c.header ?? c.label}
                </th>
              ))}
              {removable && <th className={th('w-8')}><span className="sr-only">Удалить</span></th>}
            </tr>
          </thead>
          <tbody>
            {rows.length === 0 ? (
              <tr>
                <td colSpan={span} className={`${dtTdEdit} py-3 text-center text-fg4`}>{empty}</td>
              </tr>
            ) : rows.map((row, i) => (
              <tr key={rowKey(row, i)} data-row={i} className={dtRow} {...rowData?.(row, i)}>
                {numbered && <th scope="row" className={`${dtTdEdit} text-left font-normal text-fg4 tabular-nums`}>{i + 1}</th>}
                {columns.map((c, ci) => (
                  <td key={c.key} data-cell data-r={i} data-c={ci}
                    className={`${dtTdEdit} text-fg1 ${c.align === 'right' ? dtNum : ''}`}>
                    {c.cell(row, i)}
                  </td>
                ))}
                {removable && (
                  <td className={`${dtTdEdit} text-center`}>
                    <RemoveButton index={i} label={removeLabel?.(row, i) ?? `Удалить строку ${i + 1}`}
                      onClick={() => {
                        pendingFocus.current = { kind: 'removed', index: i };
                        onRemove(row, i);
                      }} />
                  </td>
                )}
              </tr>
            ))}
          </tbody>
          {hasTotal && (
            <tfoot>
              <tr>
                {numbered && <td className={FOOT} />}
                {columns.map(c => (
                  <td key={c.key} className={`${FOOT} ${c.align === 'right' ? dtNum : ''}`}>{c.total?.()}</td>
                ))}
                {removable && <td className={FOOT} />}
              </tr>
            </tfoot>
          )}
        </table>
      </div>
      {add && (
        <Button ref={addButton} size="sm" variant="outlined" icon={<Plus size={13} />}
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
const FOOT = 'sticky bottom-0 z-10 bg-muted border-t border-stroke px-2 py-1.5 text-xs text-fg2 align-top';

function RemoveButton({ index, label, onClick }: { index: number; label: string; onClick: () => void }) {
  return (
    <button type="button" data-remove={index} onClick={onClick} aria-label={label} title={label}
      className="rounded-sm p-0.5 text-fg4 hover:text-danger focus-visible:outline-2">
      <Trash2 size={13} aria-hidden />
    </button>
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
