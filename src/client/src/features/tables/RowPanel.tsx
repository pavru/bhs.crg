import { useEffect, useRef } from 'react';
import { Link } from 'react-router';
import { Icon } from '@/shared/ui/Icon';
import { Close } from '@/shared/ui/icons';
import { DataGridValue } from '@/shared/ui/DataGrid';
import { columnUnavailable } from '@/shared/ui/dataGridStates';
import { useTable } from '@/shared/api/tables';
import type { FilterNode } from '@/shared/api/types';
import { apiError } from '@/shared/utils/apiError';
import { cellText } from './tableCells';
import { RowBreakdown } from './RowBreakdown';

/**
 * Строка таблицы в боковой панели (ТЗ CORE-33: «строка открывается в боковой панели»; задача G1e,
 * issue #1092): все её колонки, а не только показанные в таблице, — «поле — значение».
 *
 * Строку панель читает сама, по ключу и ПОД ТЕМ ЖЕ ОТБОРОМ, что и таблица. Взять её из страницы
 * таблицы нельзя: там только показанные колонки, а после перезагрузки страницы открытая строка могла
 * оказаться на другой странице. И отбор нужен тот же: «Сумма» под отбором по стройке — доля, и в
 * панели она обязана значить то же, что в клетке.
 */
export function RowPanel({ address, rowKey, filter, grain, link, onClose }: {
  address: string;
  rowKey: string;
  filter: FilterNode | string | null;
  /** Зерно таблицы — что считается строкой: «счёт». */
  grain: string;
  /**
   * Куда ведёт строка — форма записи; null — вести некуда либо экран записи человеку закрыт.
   * Показана только у ПРОЧИТАННОЙ строки: ключ пришёл из адреса страницы, и пока строка не пришла,
   * не известно, есть ли за ним запись, — ссылка вела бы в отказ.
   */
  link: { to: string; label: string } | null;
  onClose: () => void;
}) {
  const row = useTable(address, { filter, row: rowKey, limit: 1 });
  const close = useRef<HTMLButtonElement>(null);

  // Фокус — в панель: Esc закрывает её с клавиатуры, и читалка экрана узнаёт, что панель открылась.
  useEffect(() => { close.current?.focus(); }, [rowKey]);

  const data = row.data && !row.isPlaceholderData ? row.data : null;
  const values = data?.rows[0];

  return (
    <aside aria-label={`Строка: ${grain}`}
      onKeyDown={e => { if (e.key === 'Escape') { e.stopPropagation(); onClose(); } }}
      className="w-[360px] shrink-0 border-l border-outline-variant bg-surface flex flex-col min-h-0">
      <header className="h-11 shrink-0 flex items-center gap-2 px-4 border-b border-outline-variant">
        <h2 className="flex-1 text-body-medium font-medium text-on-surface">Строка: {grain}</h2>
        {link && values && <Link to={link.to} className="text-body-small text-primary underline hover:text-on-surface">{link.label}</Link>}
        <button ref={close} type="button" onClick={onClose} aria-label="Закрыть строку"
          className="p-1 rounded-sm text-on-surface-variant hover:text-on-surface state-layer">
          <Icon icon={Close} size={18} aria-hidden />
        </button>
      </header>

      <div className="flex-1 min-h-0 overflow-auto px-4 py-3">
        {row.isError ? (
          <p role="alert" className="text-body-medium text-error">{apiError(row.error, 'Строка не открылась')}</p>
        ) : !data ? (
          <p className="text-body-medium text-on-surface-variant">Строка загружается…</p>
        ) : !values ? (
          // Три причины, и какая из них — отсюда не узнать; назвать одну значило бы угадать.
          <p className="text-body-medium text-on-surface-variant">
            Под этим отбором такой строки нет: её удалили, изменили так, что она перестала подходить под
            отбор, либо отбор сменился.
          </p>
        ) : (
          <>
          {/* Расшифровка — первой: полей в панели два десятка, и под ними она ушла бы за прокрутку. */}
          {data.breakdown && <RowBreakdown breakdown={data.breakdown} grain={grain} />}
          <dl className="space-y-2.5">
            {data.columns.map(c => (
              <div key={c.key}>
                <dt className="text-body-small text-on-surface-variant">{c.note ? `${c.label} (${c.note})` : c.label}</dt>
                <dd className="text-body-medium text-on-surface break-words min-h-5">
                  <DataGridValue row={values}
                    column={{ key: c.key, label: c.label, unavailable: columnUnavailable(c.unavailable) }}
                    renderValue={v => cellText(v, c.kind)} />
                </dd>
              </div>
            ))}
          </dl>
          </>
        )}
      </div>
    </aside>
  );
}
