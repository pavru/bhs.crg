import { useMemo, useState } from 'react';
import { Link, useParams } from 'react-router';
import { ChevronLeft, ChevronRight, Table2 } from 'lucide-react';
import { DataGrid, type DataGridColumn } from '@/shared/ui/DataGrid';
import { useDocumentTitle } from '@/shared/ui/DocumentTitle';
import { FilterChips } from '@/shared/filter/FilterChips';
import { NO_ACCESS, useAccess } from '@/shared/api/access';
import { tableFilterColumns, useTable, useTableDeclaration, type TableData } from '@/shared/api/tables';
import type { FilterGroup, FilterNode } from '@/shared/api/types';
import { recordLink } from '@/shared/ui/recordRoutes';
import { apiError } from '@/shared/utils/apiError';
import { RowFilterDialog } from '@/features/datasets/RowFilterDialog';
import { ColumnsPanel } from './ColumnsPanel';
import { RowPanel } from './RowPanel';
import { cellText, gridColumns, gridState, hiddenByRight, hiddenCountText, pageCount, shownOf } from './tableCells';
import { hasShownTotals, totalText } from './tableTotals';
import {
  DEFAULT_VIEW, PAGE_SIZES, presetLookup, presetView, rowsWanted, wholeTableHash, withColumnShown, withFilter,
  withPage, withRow, withSize, withSort, type TableView,
} from './tableViewState';
import { useTableView } from './useTableView';

/**
 * Экран таблицы модуля (ТЗ CORE-33; задачи G1d и G1e, issue #1091, #1092): отбор чипами, сортировка
 * по шапке, выбор колонок, итоговая строка, закреплённые колонки и строка в боковой панели.
 *
 * Своего состояния у экрана нет — оно в адресе страницы (`useTableView`): перезагрузка и «назад» не
 * теряют ни отбор, ни состав колонок.
 *
 * <p><b>Готовое представление</b> (задача G4, issue #1097) — та же таблица под названной настройкой:
 * `/tables/costs.invoices/registry` открывает «Реестр счетов» с его колонками, итогами и местами под
 * отбор. Настройку поставляет модуль, и приходит она в описании таблицы; адрес страницы отсчитан от
 * неё, поэтому пуст, пока человек ничего не менял. Сохранённые представления — G3a.</p>
 *
 * <p><b>Из строки — в форму записи</b> (G4, issue #1097). Про счета экран не знает: таблица называет
 * тип записи за строкой, ключ строки — её идентификатор, а где запись открывается и кому, решает
 * `recordLink`. Ссылки нет у того, кому экран записи закрыт. «Назад» возвращает сюда с тем же
 * отбором и сортировкой — они в адресе.</p>
 */
export function TablePage() {
  const { address = '', view: presetCode } = useParams();
  const [advanced, setAdvanced] = useState(false);
  // Пока доступ не известен, ссылок нет: обещать переход раньше, чем известно право, нельзя.
  const { data: access = NO_ACCESS } = useAccess();

  // Описание и строки — двумя запросами. Отказ отбора приходит БЕЗ таблицы: ни колонок, ни названия.
  // Экрану они нужны и тогда — иначе негодное условие нечем было бы назвать и нечем исправить, а
  // отказ занял бы место всего экрана вместе с чипами, о которых он говорит.
  const declaration = useTableDeclaration(address);
  const lookup = presetLookup(presetCode, declaration.data);
  const [wanted, missing] = [rowsWanted(lookup), lookup.state === 'missing'];
  const preset = lookup.state === 'found' ? lookup.preset : undefined;
  const base = useMemo(() => (preset ? presetView(preset) : DEFAULT_VIEW), [preset]);
  const [view, setView] = useTableView(base);

  const filter = view.filter ?? view.brokenFilter;
  // ⚠️ Под готовым представлением строки ждут описания: настройка приходит в нём, и запрос, ушедший
  // раньше, прочитал бы таблицу ВСЕМИ колонками — чтобы тут же выбросить ответ.
  const table = useTable(address, {
    filter, columns: view.columns, sort: view.sort, totals: view.totals.map(t => t.column),
    offset: (view.page - 1) * view.size, limit: view.size,
  }, wanted);
  useDocumentTitle(preset?.title ?? declaration.data?.title);

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

  // Представление названо, а у таблицы его нет (опечатка в адресе, модуль его убрал). Открыть вместо
  // него таблицу целиком значило бы выдать её за то, о чём просили, — человек искал «Реестр счетов».
  // У выключенного модуля представлений нет вовсе, и там говорит сама таблица: «модуль выключен».
  if (missing)
    return (
      <div className="px-6 py-10 text-sm" role="alert">
        <p className="text-danger">У таблицы «{decl.title}» нет представления «{presetCode}».</p>
        <p className="mt-2 text-fg3">
          <Link className="underline" to={`/tables/${encodeURIComponent(address)}`}>Открыть таблицу целиком</Link>
        </p>
      </div>
    );
  const allKeys = decl.columns.map(c => c.key);
  const kinds = new Map(decl.columns.map(c => [c.key, c.kind]));
  const filterColumns = tableFilterColumns(decl.columns);
  // Сортируют по колонке с постоянным смыслом и видимыми значениями — остальным сервер откажет.
  const sortable = new Set(decl.columns.filter(c => !c.unavailable && !c.dependsOnFilter).map(c => c.key));
  const data = table.data;
  const grid = data ? gridColumns(data.columns) : [];
  const linkOf = (key: string | null) => recordLink(decl.recordType, key, access);

  return (
    <div className="h-full flex min-h-0">
      <div className="flex-1 min-w-0 flex flex-col px-6 py-4">
        <div className="mb-3">
          <h1 className="text-xl font-semibold text-fg1 flex items-center gap-2">
            <Table2 size={18} className="text-fg3" aria-hidden />
            {preset?.title ?? decl.title}
          </h1>
          <p className="mt-0.5 text-xs text-fg4">
            Строка — {decl.grain}. {decl.boundary.replace(/\.$/, '')}.
            {/* Из представления — к таблице целиком, с тем же отбором: другого входа в неё в интерфейсе нет. */}
            {preset && (
              <>
                {' '}
                <Link className="underline hover:text-fg2" to={`/tables/${encodeURIComponent(address)}${wholeTableHash(view)}`}>
                  Таблица целиком
                </Link>
              </>
            )}
          </p>
        </div>

        <div className="flex items-start gap-3">
          <div className="flex-1 min-w-0">
            <FilterChips columns={filterColumns} filter={view.filter} suggested={preset?.filters}
              onChange={next => setView(withFilter(view, next))} onAdvanced={() => setAdvanced(true)} />
          </div>
          {!off && (
            <ColumnsPanel columns={decl.columns} view={view} base={base} baseTitle={preset?.title}
              gridColumns={grid.length} onChange={setView} />
          )}
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
              footer={hasShownTotals(view.totals, data.totals, grid)
                ? c => <Total view={view} data={data} column={c} kind={kinds.get(c.key)} />
                : undefined}
              rowKey={data.keys ? (_, i) => data.keys![i] : undefined}
              onRowOpen={data.keys ? (_, i) => setView(withRow(view, data.keys![i])) : undefined}
              rowOpen={(_, i) => view.row !== null && data.keys?.[i] === view.row}
              rowLink={data.keys ? (_, i) => linkOf(data.keys![i]) : undefined}
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
        <RowPanel address={address} rowKey={view.row} filter={filter} grain={decl.grain} link={linkOf(view.row)}
          onClose={() => setView(withRow(view, null))} />
      )}
    </div>
  );
}

/**
 * Итог под колонкой — по всему отбору, а не по странице; оговорка о неучтённых значениях видна сразу.
 *
 * Что итог значит под этим отбором, сказано ПОД НИМ (ревизия Дизайнера; G4, issue #1097): неправильно
 * читают не клетку, а нижнюю строку. Человек, отобравший по стройке, читает её как «столько потрачено
 * на стройку» — а это доля по разноске за счета, выставленные в периоде, и шапка колонки к этому
 * моменту уже уехала вверх. Подпись приходит с самим итогом, а не с колонкой: ось периода — свойство
 * отбора, и оговорка положена каждому денежному итогу, а не одной «Сумме».
 */
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
      {total.meaning && <div className="text-fg4 font-normal whitespace-normal">{total.meaning}</div>}
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
