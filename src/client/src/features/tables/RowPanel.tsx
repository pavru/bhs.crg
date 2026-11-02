import { Link } from 'react-router';
import { DataGridValue } from '@/shared/ui/DataGrid';
import { columnUnavailable } from '@/shared/ui/dataGridStates';
import { useTable } from '@/shared/api/tables';
import type { FilterNode } from '@/shared/api/types';
import { apiError } from '@/shared/utils/apiError';
import { cellText } from './tableCells';
import { RowBreakdown } from './RowBreakdown';
import { SideSheet } from '@/shared/ui/SideSheet';

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

  const data = row.data && !row.isPlaceholderData ? row.data : null;
  const values = data?.rows[0];

  // Standard side sheet ([UI-77]): таблица сдвинута, затемнения нет; фокус — на крестик при каждой новой
  // строке, на телефоне — во весь экран, и «Назад» закрывает панель, а не таблицу.
  return (
    <SideSheet variant="standard" open onClose={onClose} title={`Строка: ${grain}`} label={`Строка: ${grain}`}
      closeLabel="Закрыть строку" focusKey={rowKey}
      actions={link && values && <Link to={link.to} className="px-2 text-body-small text-primary underline hover:text-on-surface">{link.label}</Link>}>
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
    </SideSheet>
  );
}
