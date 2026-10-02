import { useState } from 'react';
import { useParams } from 'react-router';
import { Table2 } from 'lucide-react';
import { DataGrid } from '@/shared/ui/DataGrid';
import { useDocumentTitle } from '@/shared/ui/DocumentTitle';
import { FilterChips } from '@/shared/filter/FilterChips';
import { tableFilterColumns, useTable, type TableData } from '@/shared/api/tables';
import type { FilterGroup, FilterNode } from '@/shared/api/types';
import { apiError } from '@/shared/utils/apiError';
import { RowFilterDialog } from '@/features/datasets/RowFilterDialog';
import { cellText, gridColumns, gridState, shownOf } from './tableCells';

/**
 * Таблица модуля с отбором чипами (ТЗ CORE-33; задача G1d, issue #1091).
 *
 * <p>⚠️ Это ещё не экран представления: состояние в адресе, выбор колонок, сортировка по шапке и
 * панель строки приезжают задачей G1e, готовый «Реестр счетов» — G4. Здесь ровно то, без чего отбор
 * чипами нечем проверить: таблица по адресу, чипы над ней и расширенный режим — то же дерево
 * условий. Назвать это реестром значило бы объявить сделанным то, что не начато.</p>
 */
export function TablePage() {
  const { address = '' } = useParams();
  const [filter, setFilter] = useState<FilterNode | null>(null);
  const [advanced, setAdvanced] = useState(false);
  const table = useTable(address, { filter });

  // Отказ отбора приходит БЕЗ таблицы: ни колонок, ни названия. Экрану они нужны и тогда — иначе
  // негодное условие нечем было бы назвать и нечем исправить, а отказ занял бы место всего экрана
  // вместе с чипами, о которых он говорит. Поэтому помним последний удачный ответ.
  const [last, setLast] = useState<TableData | null>(null);
  if (table.data && !table.isPlaceholderData && table.data !== last) setLast(table.data);
  const data = table.data ?? last;
  useDocumentTitle(data?.title);

  // Первый ответ ещё не пришёл и отказа нет — сказать пока нечего: ни «строк нет», ни причину.
  if (!data && !table.isError) return <div className="px-6 py-10 text-sm text-fg4">Загрузка…</div>;

  // Таблицы нет или она не открыта этому человеку — отказ без единой колонки. Удачного ответа не
  // было ни разу, значит дело не в отборе: показываем причину сервера вместо экрана.
  if (!data)
    return (
      <div className="px-6 py-10 text-sm text-danger" role="alert">
        {apiError(table.error, 'Таблица не открылась')}
      </div>
    );

  const filterColumns = tableFilterColumns(data.columns);
  const kinds = new Map(data.columns.map(c => [c.key, c.kind]));
  return (
    <div className="px-6 py-4">
      <div className="mb-3">
        <h1 className="text-xl font-semibold text-fg1 flex items-center gap-2">
          <Table2 size={18} className="text-fg3" aria-hidden />
          {data.title}
        </h1>
        <p className="mt-0.5 text-xs text-fg4">Строка — {data.grain}. {data.boundary.replace(/\.$/, '')}.</p>
      </div>

      <FilterChips columns={filterColumns} filter={filter} onChange={setFilter}
        onAdvanced={() => setAdvanced(true)} />

      {/* Отказ отбора — на месте строк и НАД прежней выдачей: строки под ним получены под другим
          отбором, и молча оставить их значило бы выдать за ответ на этот. Чипы остаются — отказ
          говорит про них. */}
      {table.isError ? (
        <p role="alert" className="mt-3 rounded-lg border border-danger px-3 py-2 text-sm text-danger">
          {apiError(table.error, 'Отбор не применён')}
        </p>
      ) : (
        <>
          <DataGrid className={`mt-3 ${table.isFetching ? 'opacity-60' : ''}`}
            columns={gridColumns(data.columns)} rows={data.rows}
            state={gridState(data, filter !== null)}
            renderValue={(value, column) => cellText(value, kinds.get(column.key))} />
          <p className="mt-2 text-xs text-fg4">{shownOf(data)}</p>
        </>
      )}

      {advanced && (
        <RowFilterDialog columns={filterColumns} initial={asRoot(filter)} wording={ADVANCED_WORDING}
          onSave={setFilter} onClose={() => setAdvanced(false)} />
      )}
    </div>
  );
}

const ADVANCED_WORDING = {
  title: 'Отбор: расширенный режим',
  note: 'То же дерево условий, что показывают чипы над таблицей. Условия можно связывать «ИЛИ» и '
    + 'вкладывать группами; такой отбор над таблицей назван сложным и правится здесь.',
  save: 'Применить',
  saving: 'Применение…',
  reset: 'Снять отбор',
};

/** Диалогу нужен корень-группа; одиночное условие — та же группа из одного. */
function asRoot(filter: FilterNode | null): FilterGroup | null {
  if (!filter) return null;
  return filter.type === 'group' ? filter : { type: 'group', logic: 'and', children: [filter] };
}
