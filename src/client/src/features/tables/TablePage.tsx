import { useState } from 'react';
import { useParams } from 'react-router';
import { ChevronLeft, ChevronRight, Table2 } from 'lucide-react';
import { DataGrid, type DataGridColumn } from '@/shared/ui/DataGrid';
import { useDocumentTitle } from '@/shared/ui/DocumentTitle';
import { FilterChips } from '@/shared/filter/FilterChips';
import { tableFilterColumns, useTable, useTableDeclaration, type TableData } from '@/shared/api/tables';
import type { FilterGroup, FilterNode } from '@/shared/api/types';
import { apiError } from '@/shared/utils/apiError';
import { RowFilterDialog } from '@/features/datasets/RowFilterDialog';
import { ColumnsPanel } from './ColumnsPanel';
import { RowPanel } from './RowPanel';
import { cellText, gridColumns, gridState, hiddenByRight, hiddenCountText, pageCount, shownOf } from './tableCells';
import { totalText } from './tableTotals';
import {
  PAGE_SIZES, withColumnShown, withFilter, withPage, withRow, withSize, withSort, type TableView,
} from './tableViewState';
import { useTableView } from './useTableView';

/**
 * Экран таблицы модуля (ТЗ CORE-33; задачи G1d и G1e, issue #1091, #1092): отбор чипами, сортировка
 * по шапке, выбор колонок, итоговая строка, закреплённые колонки и строка в боковой панели.
 *
 * Своего состояния у экрана нет — оно в адресе страницы (`useTableView`): перезагрузка и «назад» не
 * теряют ни отбор, ни состав колонок.
 *
 * <p>⚠️ Это механизм, а не «Реестр счетов»: готовое представление со своим составом колонок и
 * пунктом в навигации приезжает задачей G4, сохранённые представления — G3a.</p>
 */
export function TablePage() {
  const { address = '' } = useParams();
  const [view, setView] = useTableView();
  const [advanced, setAdvanced] = useState(false);

  // Описание и строки — двумя запросами. Отказ отбора приходит БЕЗ таблицы: ни колонок, ни названия.
  // Экрану они нужны и тогда — иначе негодное условие нечем было бы назвать и нечем исправить, а
  // отказ занял бы место всего экрана вместе с чипами, о которых он говорит.
  const declaration = useTableDeclaration(address);
  const filter = view.filter ?? view.brokenFilter;
  const table = useTable(address, {
    filter, columns: view.columns, sort: view.sort, totals: view.totals.map(t => t.column),
    offset: (view.page - 1) * view.size, limit: view.size,
  });
  useDocumentTitle(declaration.data?.title);

  // Описание ещё не пришло и отказа нет — сказать пока нечего: ни «строк нет», ни причину.
  if (declaration.isPending) return <div className="px-6 py-10 text-sm text-fg4">Загрузка…</div>;

  // Таблицы нет или она не открыта этому человеку — показываем причину сервера вместо экрана.
  if (declaration.isError)
    return (
      <div className="px-6 py-10 text-sm text-danger" role="alert">
        {apiError(declaration.error, 'Таблица не открылась')}
      </div>
    );

  const decl = declaration.data;
  const off = decl.state === 'module-off';
  const allKeys = decl.columns.map(c => c.key);
  const kinds = new Map(decl.columns.map(c => [c.key, c.kind]));
  const filterColumns = tableFilterColumns(decl.columns);
  // Сортируют по колонке с постоянным смыслом и видимыми значениями — остальным сервер откажет.
  const sortable = new Set(decl.columns.filter(c => !c.unavailable && !c.dependsOnFilter).map(c => c.key));
  const data = table.data;
  const grid = data ? gridColumns(data.columns) : [];

  return (
    <div className="h-full flex min-h-0">
      <div className="flex-1 min-w-0 flex flex-col px-6 py-4">
        <div className="mb-3">
          <h1 className="text-xl font-semibold text-fg1 flex items-center gap-2">
            <Table2 size={18} className="text-fg3" aria-hidden />
            {decl.title}
          </h1>
          <p className="mt-0.5 text-xs text-fg4">Строка — {decl.grain}. {decl.boundary.replace(/\.$/, '')}.</p>
        </div>

        <div className="flex items-start gap-3">
          <div className="flex-1 min-w-0">
            <FilterChips columns={filterColumns} filter={view.filter}
              onChange={next => setView(withFilter(view, next))} onAdvanced={() => setAdvanced(true)} />
          </div>
          {!off && <ColumnsPanel columns={decl.columns} view={view} gridColumns={grid.length} onChange={setView} />}
        </div>

        {/* Колонка, закрытая правом, в таблицу не идёт — о ней говорит эта строка: сколько колонок,
            чего не хватает и КОДОМ какого права. Со словами человек пойдёт к администратору, а тот
            ищет право по коду. У кого прав хватает, строки нет вовсе. */}
        {!off && hiddenByRight(decl.columns, view.columns).map(group => (
          <p key={`${group.requires}|${group.reason}`} className="mt-2 text-xs text-fg3" title={group.labels.join(', ')}>
            {hiddenCountText(group.count)}: {group.reason}
            {group.requires && <> (<code className="text-fg2">{group.requires}</code>)</>}
          </p>
        ))}

        {/* Отказ — на месте строк, а не поверх прежней выдачи: строки под ним получены под другим
            отбором, и молча оставить их значило бы выдать за ответ на этот. И это НЕ «отбор ничего не
            нашёл»: строк не ноль — их не отдали. Чипы остаются — отказ говорит про них. */}
        {table.isError ? (
          <div role="alert" className="mt-3 rounded-lg border border-danger px-3 py-2 text-sm text-danger">
            <p>{apiError(table.error, 'Таблица не применила отбор или сортировку')}</p>
            <div className="mt-1.5 flex gap-3 text-xs">
              {filter !== null && (
                <button type="button" className="underline" onClick={() => setView(withFilter(view, null))}>
                  Снять отбор
                </button>
              )}
              {view.sort.length > 0 && (
                <button type="button" className="underline" onClick={() => setView({ ...view, sort: [] })}>
                  Снять сортировку
                </button>
              )}
            </div>
          </div>
        ) : !data ? (
          <p className="mt-3 text-sm text-fg4">Строки загружаются…</p>
        ) : (
          <>
            <DataGrid className={`mt-3 flex-1 min-h-0 ${table.isFetching ? 'opacity-60' : ''}`}
              columns={grid} rows={data.rows}
              state={gridState(decl, filter !== null)}
              renderValue={(value, column) => cellText(value, kinds.get(column.key))}
              sort={view.sort} sortable={c => sortable.has(c.key)}
              onSort={(c, additive) => setView(withSort(view, c.key, additive))}
              pinned={view.pinned}
              footer={view.totals.length > 0 ? c => <Total view={view} data={data} column={c} kind={kinds.get(c.key)} /> : undefined}
              rowKey={data.keys ? (_, i) => data.keys![i] : undefined}
              onRowOpen={data.keys ? (_, i) => setView(withRow(view, data.keys![i])) : undefined}
              rowOpen={(_, i) => view.row !== null && data.keys?.[i] === view.row}
              onRemoveColumn={c => setView(withColumnShown(view, allKeys, c.key, false))} />
            {!off && <Pager view={view} data={data} onChange={setView} />}
          </>
        )}

        {advanced && (
          <RowFilterDialog columns={filterColumns} initial={asRoot(view.filter)} wording={ADVANCED_WORDING}
            onSave={next => setView(withFilter(view, next))} onClose={() => setAdvanced(false)} />
        )}
      </div>

      {view.row && !off && (
        <RowPanel address={address} rowKey={view.row} filter={filter} grain={decl.grain}
          onClose={() => setView(withRow(view, null))} />
      )}
    </div>
  );
}

/** Итог под колонкой — по всему отбору, а не по странице; оговорка о неучтённых значениях видна сразу. */
function Total({ view, data, column, kind }: {
  view: TableView; data: TableData; column: DataGridColumn; kind: string | undefined;
}) {
  const chosen = view.totals.find(t => t.column === column.key);
  const total = chosen ? totalText(data.totals?.[column.key], chosen.aggregate, kind ?? 'text') : null;
  if (!total) return null;
  return (
    <div title="Итог по всему отбору, а не по странице">
      <div className="font-medium text-fg1">{total.text}</div>
      {total.note && <div className="text-warning">{total.note}</div>}
    </div>
  );
}

/** Страницы: какие строки показаны, сколько их в отборе, и переход между страницами. */
function Pager({ view, data, onChange }: {
  view: TableView; data: TableData; onChange: (next: TableView) => void;
}) {
  const pages = pageCount(data.count, view.size);
  const button = 'p-1 rounded-md text-fg3 hover:text-fg1 hover:bg-muted disabled:opacity-30 disabled:hover:bg-transparent';
  return (
    <div className="mt-2 flex items-center gap-3 text-xs text-fg4">
      <span>{shownOf(data)}</span>
      <div className="flex-1" />
      <label className="flex items-center gap-1.5">
        Строк на странице
        <select value={view.size} onChange={e => onChange(withSize(view, Number(e.target.value)))}
          className="rounded-md border border-stroke bg-surface px-1 py-0.5 text-xs text-fg2">
          {PAGE_SIZES.map(size => <option key={size} value={size}>{size}</option>)}
        </select>
      </label>
      {(pages > 1 || view.page > 1) && (
        <div className="flex items-center gap-1">
          <button type="button" className={button} disabled={view.page <= 1} aria-label="Предыдущая страница"
            onClick={() => onChange(withPage(view, view.page - 1))}>
            <ChevronLeft size={15} aria-hidden />
          </button>
          <span>Страница {view.page} из {pages}</span>
          <button type="button" className={button} disabled={view.page >= pages} aria-label="Следующая страница"
            onClick={() => onChange(withPage(view, view.page + 1))}>
            <ChevronRight size={15} aria-hidden />
          </button>
        </div>
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
