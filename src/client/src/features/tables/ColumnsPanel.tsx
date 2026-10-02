import { useState } from 'react';
import * as Popover from '@radix-ui/react-popover';
import { Columns3, EyeOff, Unlink } from 'lucide-react';
import { MoveButtons } from '@/shared/ui/MoveButtons';
import type { TableColumn } from '@/shared/api/tables';
import { AGGREGATES, aggregatesFor } from './tableTotals';
import {
  chooserOrder, withColumnMoved, withColumnShown, withColumnsReset, withPinned, withTotal,
  type Aggregate, type TableView,
} from './tableViewState';

/**
 * Выбор колонок таблицы (ТЗ CORE-33; задача G1e, issue #1092): какие показаны и в каком порядке,
 * какой итог стоит под каждой и сколько первых закреплено слева.
 *
 * Список — ВСЕ колонки таблицы, а не показанные: убранную колонку возвращают отсюда же. Колонка,
 * закрытая правом, и колонка, которой в типе больше нет, из списка не пропадают — каждая стоит со
 * своей причиной, иначе «куда делась колонка» было бы нечем объяснить.
 */
export function ColumnsPanel({ columns, view, gridColumns, onChange }: {
  /** Все колонки таблицы — из её описания. */
  columns: TableColumn[];
  view: TableView;
  /** Сколько колонок сейчас в сетке: закрепить можно не больше. */
  gridColumns: number;
  onChange: (next: TableView) => void;
}) {
  const all = columns.map(c => c.key);
  const byKey = new Map(columns.map(c => [c.key, c]));
  const shown = view.columns ?? all;
  const customised = view.columns !== null || view.totals.length > 0 || view.pinned > 0;

  // Порядок строк списка запоминается, когда окошко открывают: показанные колонки, за ними
  // остальные. Пока оно открыто, снятая галочка строку не уносит — иначе колонка убегала бы из-под
  // руки, а с клавиатуры вместе с ней терялось бы место в списке.
  const [open, setOpen] = useState(false);
  const [snapshot, setSnapshot] = useState<string[]>([]);
  const order = chooserOrder(snapshot, shown);

  function toggle(next: boolean) {
    if (next) setSnapshot([...shown, ...all.filter(key => !shown.includes(key))]);
    setOpen(next);
  }

  return (
    <Popover.Root open={open} onOpenChange={toggle}>
      <Popover.Trigger asChild>
        <button type="button"
          className="inline-flex items-center gap-1.5 rounded-md border border-stroke px-2.5 py-1 text-xs text-fg2 hover:text-fg1 hover:bg-muted">
          <Columns3 size={13} aria-hidden />
          {view.columns ? `Колонки: ${shown.length} из ${all.length}` : 'Колонки'}
        </button>
      </Popover.Trigger>
      <Popover.Portal>
        <Popover.Content align="end" sideOffset={6} aria-label="Колонки и итоги"
          className="z-50 w-[26rem] max-h-[70vh] overflow-auto rounded-lg bg-surface border border-stroke p-3 focus:outline-none"
          style={{ boxShadow: 'var(--f-shadow16)' }}>
          <ul className="space-y-0.5">
            {order.map((key, i) => {
              const place = shown.indexOf(key);
              const column = byKey.get(key) ?? null;
              // Возвращённая колонка встаёт туда, где стоит в списке, — среди показанных выше неё.
              const above = order.slice(0, i).filter(k => shown.includes(k)).length;
              return place < 0 ? (
                <ColumnRow key={key} column={column} label={column?.label ?? key} checked={false} total={null}
                  onShown={() => onChange(withColumnShown(view, all, key, true, above))} />
              ) : (
                <ColumnRow key={key} column={column} label={column?.label ?? key} checked
                  total={view.totals.find(t => t.column === key)?.aggregate ?? null}
                  onShown={() => onChange(withColumnShown(view, all, key, false))}
                  onTotal={aggregate => onChange(withTotal(view, key, aggregate))}
                  move={{
                    isFirst: place === 0, isLast: place === shown.length - 1,
                    onUp: () => onChange(withColumnMoved(view, all, key, -1)),
                    onDown: () => onChange(withColumnMoved(view, all, key, 1)),
                  }} />
              );
            })}
          </ul>

          <div className="mt-3 pt-2.5 border-t border-stroke flex items-center gap-2 text-xs text-fg2">
            <label htmlFor="table-pinned">Закрепить слева</label>
            <select id="table-pinned" value={Math.min(view.pinned, gridColumns)}
              onChange={e => onChange(withPinned(view, Number(e.target.value)))}
              className="rounded-md border border-stroke bg-surface px-1.5 py-1 text-xs">
              <option value={0}>не закреплять</option>
              {[1, 2, 3].filter(n => n <= gridColumns).map(n => (
                <option key={n} value={n}>{n === 1 ? 'первую колонку' : `первые ${n} колонки`}</option>
              ))}
            </select>
            <div className="flex-1" />
            <button type="button" disabled={!customised} onClick={() => onChange(withColumnsReset(view))}
              className="text-fg3 hover:text-fg1 disabled:opacity-40 disabled:hover:text-fg3">
              Как у таблицы
            </button>
          </div>
        </Popover.Content>
      </Popover.Portal>
    </Popover.Root>
  );
}

/** Строка списка: показана ли колонка, её место и итог под ней. */
function ColumnRow({ column, label, checked, total, onShown, onTotal, move }: {
  /** null — колонки в таблице больше нет, а в представлении она осталась. */
  column: TableColumn | null;
  label: string;
  checked: boolean;
  total: Aggregate | null;
  onShown: () => void;
  onTotal?: (aggregate: Aggregate | null) => void;
  move?: { isFirst: boolean; isLast: boolean; onUp: () => void; onDown: () => void };
}) {
  const closed = column?.unavailable === 'no-right';
  // Итог есть у чего считать: колонка в таблице есть и её значения человеку видны.
  const totals = column && !column.unavailable ? aggregatesFor(column.kind) : [];

  return (
    <li className="flex items-center gap-2 rounded-md px-1.5 py-1 hover:bg-muted">
      <label className="flex-1 min-w-0 flex items-center gap-2 text-sm text-fg1 cursor-pointer">
        <input type="checkbox" checked={checked} onChange={onShown} />
        <span className={`truncate ${column ? '' : 'line-through text-fg4'}`}>{label}</span>
        {closed && (
          <span className="shrink-0 inline-flex items-center gap-1 text-xs text-fg4">
            <EyeOff size={11} aria-hidden /> {column?.reason ?? 'нет права'}
          </span>
        )}
        {!column && (
          <span className="shrink-0 inline-flex items-center gap-1 text-xs text-fg4">
            <Unlink size={11} aria-hidden /> поля нет в типе
          </span>
        )}
      </label>

      {onTotal && totals.length > 0 && (
        <select value={total ?? ''} aria-label={`Итог по колонке «${label}»`}
          onChange={e => onTotal(e.target.value ? e.target.value as Aggregate : null)}
          className="shrink-0 rounded-md border border-stroke bg-surface px-1 py-0.5 text-xs text-fg2">
          <option value="">без итога</option>
          {totals.map(a => <option key={a} value={a}>{AGGREGATES[a].label}</option>)}
        </select>
      )}

      {move && (
        <span className="shrink-0 inline-flex">
          <MoveButtons {...move} upTitle="Левее" downTitle="Правее" />
        </span>
      )}
    </li>
  );
}
