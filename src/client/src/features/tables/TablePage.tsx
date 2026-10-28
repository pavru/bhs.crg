import { useMemo, useState } from 'react';
import { Link, useParams } from 'react-router';
import { ChevronLeft, ChevronRight } from 'lucide-react';
import { DataGrid, type DataGridColumn } from '@/shared/ui/DataGrid';
import { useDocumentTitle } from '@/shared/ui/DocumentTitle';
import { FilterChips } from '@/shared/filter/FilterChips';
import { NO_ACCESS, hasPermission, useAccess } from '@/shared/api/access';
import {
  defaultColumnKeys, tableExportBody, tableFilterColumns, useTable, useTableDeclaration, type TableColumn, type TableData,
} from '@/shared/api/tables';
import { useSavedTableViews } from '@/shared/api/tableViews';
import type { FilterGroup, FilterNode } from '@/shared/api/types';
import { recordLink, recordScreen } from '@/shared/ui/recordRoutes';
import { apiError } from '@/shared/utils/apiError';
import { RowFilterDialog } from '@/features/datasets/RowFilterDialog';
import { ColumnsPanel } from './ColumnsPanel';
import { ExportMenu } from './ExportMenu';
import { RowPanel } from './RowPanel';
import { ShortcutChips } from './ShortcutChips';
import { SitesChip } from './SitesChip';
import { TableViewHeader } from './TableViewHeader';
import { isSavedId, lookupBase, lookupTitle, rowsWantedFor, viewLookup, viewModified } from './savedViews';
import { withShortcut } from './tableShortcuts';
import { withSitesChip } from './tableSites';
import { useTableKnown } from './tableKnown';
import { cellText, gridColumns, gridState, hiddenByRight, hiddenCountText, pageCount, shownOf } from './tableCells';
import { hasShownTotals, totalText } from './tableTotals';
import {
  PAGE_SIZES, wholeTableHash, withColumnShown, withFilter,
  withFilterChange, withPageStep, withRow, withSize, withSort, type TableView, type ViewChange,
} from './tableViewState';
import { useTableView } from './useTableView';
import { Select, SelectItem } from '@/shared/ui/Select';
import { Tooltip } from '@/shared/ui/Tooltip';

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
 * неё, поэтому пуст, пока человек ничего не менял.</p>
 *
 * <p><b>Сохранённое представление</b> (задача G3a, issue #1095) стоит в адресе на том же месте, но
 * идентификатором: `/tables/costs.invoices/3f2a…`. Его настройка — с отбором — приходит списком
 * представлений, и адрес отсчитан от неё так же. Выбор, пометка «изменено» и сохранение — в шапке
 * (`TableViewHeader`).</p>
 *
 * <p><b>Из строки — в форму записи</b> (G4, issue #1097). Про счета экран не знает: таблица называет
 * тип записи за строкой, ключ строки — её идентификатор, а где запись открывается и кому, решает
 * `recordLink`. Ссылки нет у того, кому экран записи закрыт. «Назад» возвращает сюда с тем же
 * отбором и сортировкой — они в адресе.</p>
 */
export function TablePage() {
  const { address = '', view: presetCode } = useParams();
  const [advanced, setAdvanced] = useState(false);
  // Представление, которое человек только что удалил сам и с которого экран уходит (см. ниже).
  const [leaving, setLeaving] = useState<string | null>(null);
  // Пока доступ не известен, ссылок нет: обещать переход раньше, чем известно право, нельзя.
  const { data: access = NO_ACCESS } = useAccess();

  // Описание и строки — двумя запросами. Отказ отбора приходит БЕЗ таблицы: ни колонок, ни названия.
  // Экрану они нужны и тогда — иначе негодное условие нечем было бы назвать и нечем исправить, а
  // отказ занял бы место всего экрана вместе с чипами, о которых он говорит.
  const declaration = useTableDeclaration(address);
  // Список сохранённых нужен и шапке, и адресу; у выключенного модуля его нет — и спрашивать незачем.
  const saved = useSavedTableViews(address, !!declaration.data && declaration.data.state !== 'module-off');
  const lookup = viewLookup(presetCode, declaration.data, saved.data);
  const [wanted, missing] = [rowsWantedFor(lookup), lookup.state === 'missing'];
  const preset = lookup.state === 'found' ? lookup.preset : undefined;
  const stored = lookup.state === 'saved' ? lookup.saved : undefined;
  // Основа — один и тот же объект, пока настройка под адресом та же: от неё считается адрес.
  // eslint-disable-next-line react-hooks/exhaustive-deps -- `lookup` новый на каждой отрисовке; основу определяют эти двое
  const base = useMemo(() => lookupBase(lookup), [preset, stored]);
  const [view, setView] = useTableView(base);
  // Перечни «Объекта» и «Раздела» — подсказка значения в условии отбора (UI-56). Хук — до ранних выходов;
  // у выключенного модуля справочников не спрашиваем: его адреса закрыты.
  const known = useTableKnown(address,
    declaration.data && declaration.data.state !== 'module-off' ? declaration.data.columns : NO_COLUMNS);

  const filter = view.filter ?? view.brokenFilter;
  // ⚠️ Под готовым представлением строки ждут описания: настройка приходит в нём, и запрос, ушедший
  // раньше, прочитал бы таблицу ВСЕМИ колонками — чтобы тут же выбросить ответ.
  const table = useTable(address, {
    filter, columns: view.columns, sort: view.sort, totals: view.totals.map(t => t.column),
    offset: (view.page - 1) * view.size, limit: view.size,
  }, wanted);
  useDocumentTitle(declaration.data && lookupTitle(lookup, declaration.data.title));

  // Описание ещё не пришло и отказа нет — сказать пока нечего: ни «строк нет», ни причину.
  if (declaration.isPending) return <div className="px-6 py-10 text-sm text-fg4">Загрузка…</div>;

  // Таблицы нет или она не открыта этому человеку — показываем причину сервера вместо экрана.
  if (declaration.isError)
    return (
      <div className="px-6 py-10 text-sm text-error" role="alert">
        {apiError(declaration.error, 'Таблица не открылась')}
      </div>
    );

  // Назван идентификатор, а список представлений не пришёл: сказать «удалено» было бы неправдой.
  // ⚠️ Только если списка НЕТ: неудачное перечитывание оставляет прежний список, и снимать из-за
  // него рабочий экран нельзя (ревью PR #1324).
  if (saved.isError && !saved.data && isSavedId(presetCode))
    return (
      <div className="px-6 py-10 text-sm text-error" role="alert">
        {apiError(saved.error, 'Представления таблицы не открылись')}
      </div>
    );

  // Список представлений в пути, а под адресом — одно из них: основа ещё не известна. Нарисовать
  // экран от умолчаний таблицы значило бы выдать «ещё не знаем» за «таблица целиком» — с чужим
  // названием, ложным «изменено» и кнопкой, которая сохранила бы не то (ревью PR #1324).
  // То же — миг между удалением открытого представления и переходом к таблице: из списка оно уже
  // пропало, а адрес ещё прежний. «Представление удалено» тут было бы ложным объявлением о том, что
  // человек сделал сам. Устаревшая закладка сюда не попадает: её идентификатор никто не удалял.
  if (lookup.state === 'pending' || (lookup.state === 'gone' && leaving === presetCode))
    return <div className="px-6 py-10 text-sm text-fg4">Загрузка…</div>;

  const decl = declaration.data;
  const off = decl.state === 'module-off';
  // Название представления в шапке и имени файла. Изменённое представление — уже не оно: файл,
  // названный «Реестр счетов», через неделю читали бы как выгрузку реестра (ревью PR #1326).
  const viewTitle = preset?.title ?? stored?.title;
  const exportTitle = viewTitle && viewModified(view, base) ? `${viewTitle} (изменено)` : viewTitle;

  // Устаревшая закладка: представление удалено либо автор вернул его в свои — сервер эти случаи не
  // различает, и экран тоже. Отбор из адреса едет к таблице целиком: с закладкой пришли за строками.
  if (lookup.state === 'gone')
    return (
      <div className="px-6 py-10 text-sm" role="alert">
        <p className="text-error">Представление удалено или больше не общее.</p>
        <p className="mt-2 text-fg3">
          <Link className="underline" to={`/tables/${encodeURIComponent(address)}${wholeTableHash(view)}`}>Открыть таблицу целиком</Link>
        </p>
      </div>
    );

  // Представление названо, а у таблицы его нет (опечатка в адресе, модуль его убрал). Открыть вместо
  // него таблицу целиком значило бы выдать её за то, о чём просили, — человек искал «Реестр счетов».
  // У выключенного модуля представлений нет вовсе, и там говорит сама таблица: «модуль выключен».
  if (missing)
    return (
      <div className="px-6 py-10 text-sm" role="alert">
        <p className="text-error">У таблицы «{decl.title}» нет представления «{presetCode}».</p>
        <p className="mt-2 text-fg3">
          <Link className="underline" to={`/tables/${encodeURIComponent(address)}`}>Открыть таблицу целиком</Link>
        </p>
      </div>
    );
  const allKeys = defaultColumnKeys(decl.columns);
  const kinds = new Map(decl.columns.map(c => [c.key, c.kind]));
  const filterColumns = tableFilterColumns(decl.columns, known);
  // Сортируют по колонке с постоянным смыслом и видимыми значениями — остальным сервер откажет.
  const sortable = new Set(decl.columns.filter(c => !c.unavailable && !c.dependsOnFilter).map(c => c.key));
  const data = table.data;
  const grid = data ? gridColumns(data.columns) : [];
  const screen = recordScreen(decl.recordType, access);
  const canXlsx = !!data && data.count <= (decl.exportMax ?? 0);
  // Колонки моста — показанные экраном, по порядку: у источника они те же, что у таблицы перед глазами.
  // Пункта нет и тогда, когда переносить нечего: на экране только колонки доли — сервер откажет.
  const byKey = new Map(decl.columns.map(c => [c.key, c]));
  const bridgeColumns = (view.columns?.length ? view.columns : allKeys).flatMap(k => byKey.get(k) ?? []);
  const bridge = hasPermission(access, 'core.datasets.edit') && !stored?.withheld
    && bridgeColumns.some(c => !c.dependsOnFilter)
    ? { name: exportTitle ?? decl.title, columns: bridgeColumns, all: decl.columns }
    : undefined;
  // Подпись ссылки называет строку значением первой показанной колонки — ею строки и различают.
  const nameOf = (row: Record<string, unknown>) => (grid[0] ? cellText(row[grid[0].key], kinds.get(grid[0].key)) : '');

  return (
    <div className="h-full flex min-h-0">
      <div className="flex-1 min-w-0 flex flex-col px-6 py-4">
        <TableViewHeader address={address} decl={decl} lookup={lookup} view={view} base={base} saved={saved.data}
          savedProblem={saved.isError && !saved.data ? apiError(saved.error, 'Сохранённые представления не загрузились') : null}
          onChange={setView} onLeaving={setLeaving} />

        {/* Отбор автора читателю не отдан: он стоит на колонке, которая ему закрыта. Сказать это
            обязательно — иначе представление с чужим именем молча показало бы другие строки. */}
        {stored?.withheld && (
          <p role="status" className="mb-2 text-xs text-warning">{stored.withheld} Представление открыто без него.</p>
        )}

        {/* Готовые отборы модуля — над рядом условий: нажатие кладёт условие в этот ряд, обычным чипом. */}
        <ShortcutChips address={address} filter={view} enabled={wanted && !off}
          onToggle={shortcut => setView(v => withShortcut(v, allKeys, shortcut))} />

        {/* Избранные стройки таблицу не урезают — только предлагают условие, которое ставит человек (#1223). */}
        {!off && (
          <SitesChip columns={decl.columns} filter={view}
            onToggle={(column, names) => setView(v => withSitesChip(v, allKeys, column, names))} />
        )}

        <div className="flex items-start gap-3">
          <div className="flex-1 min-w-0">
            <FilterChips columns={filterColumns} filter={view.filter} suggested={preset?.filters}
              onChange={change => setView(v => withFilterChange(v, change))} onAdvanced={() => setAdvanced(true)} />
          </div>
          {!off && (
            <ColumnsPanel columns={decl.columns} view={view} base={base} baseTitle={preset?.title ?? stored?.title}
              gridColumns={grid.length} onChange={setView} />
          )}
          {/* Выгружать есть что, только когда пришли строки ЭТОГО отбора. При отказе файл повторил бы
              отказ, а прежние строки, оставленные на экране на время запроса, назвали бы в пункте меню
              число чужого отбора (ревью PR #1326). Невыполнимые пункты скрыты (решение владельца
              11.10.2026): XLSX — сверх потолка выгрузки, мост — без права настройки наборов и при
              отборе автора, который читателю не отдан: источник получил бы не тот отбор. */}
          {!off && data && !table.isError && !table.isPlaceholderData && (canXlsx || bridge) && (
            <ExportMenu address={address} count={data.count} xlsx={canXlsx} dataset={bridge} body={tableExportBody(
              { filter, columns: view.columns, sort: view.sort, totals: view.totals }, exportTitle)} />
          )}
        </div>

        {/* Колонка, закрытая правом, в таблицу не идёт — о ней говорит эта строка: сколько колонок,
            чего не хватает и КОДОМ какого права. Со словами человек пойдёт к администратору, а тот
            ищет право по коду. У кого прав хватает, строки нет вовсе. */}
        {!off && hiddenByRight(decl.columns, view.columns).map(group => (
          <Tooltip key={`${group.requires}|${group.reason}`} content={group.labels.join(', ')}>
            <p className="mt-2 text-xs text-fg3">
              {hiddenCountText(group.count)}: {group.reason}
              {group.requires && <> (<code className="text-on-surface-variant">{group.requires}</code>)</>}
            </p>
          </Tooltip>
        ))}

        {/* Отказ — на месте строк, а не поверх прежней выдачи: строки под ним получены под другим
            отбором, и молча оставить их значило бы выдать за ответ на этот. И это НЕ «отбор ничего не
            нашёл»: строк не ноль — их не отдали. Чипы остаются — отказ говорит про них. */}
        {table.isError ? (
          <div role="alert" className="mt-3 rounded-lg border border-error px-3 py-2 text-sm text-error">
            <p>{apiError(table.error, 'Таблица не применила отбор или сортировку')}</p>
            <div className="mt-1.5 flex gap-3 text-xs">
              {filter !== null && (
                <button type="button" className="underline" onClick={() => setView(v => withFilter(v, null))}>
                  Снять отбор
                </button>
              )}
              {view.sort.length > 0 && (
                <button type="button" className="underline" onClick={() => setView(v => ({ ...v, sort: [] }))}>
                  Снять сортировку
                </button>
              )}
            </div>
          </div>
        ) : !data ? (
          <p className="mt-3 text-sm text-fg4">Строки загружаются…</p>
        ) : (
          <>
            {/* Ответ неполон — говорим это НАД строками, а не подписью колонки: колонки может не быть
                на экране, а отбор по ней стоит, и пустая таблица читалась бы как «таких строк нет». */}
            {data.doubts?.map(doubt => (
              <p key={doubt.column} role="status" className="mt-2 text-xs text-warning">
                «{doubt.label}» — {doubt.reason}. Строк под отбором по этой колонке может быть больше, чем показано.
              </p>
            ))}
            <DataGrid className={`mt-3 flex-1 min-h-0 ${table.isFetching ? 'opacity-60' : ''}`}
              columns={grid} rows={data.rows}
              state={gridState(decl, filter !== null)}
              renderValue={(value, column) => cellText(value, kinds.get(column.key))}
              sort={view.sort} sortable={c => sortable.has(c.key)}
              onSort={(c, additive) => setView(v => withSort(v, c.key, additive))}
              pinned={view.pinned}
              footer={hasShownTotals(view.totals, data.totals, grid)
                ? c => <Total view={view} data={data} column={c} kind={kinds.get(c.key)} />
                : undefined}
              rowKey={data.keys ? (_, i) => data.keys![i] : undefined}
              onRowOpen={data.keys ? (_, i) => setView(v => withRow(v, data.keys![i])) : undefined}
              rowOpen={(_, i) => view.row !== null && data.keys?.[i] === view.row}
              // Ключи — этой таблицы: пока новый ответ в пути, на экране прежний, и ключи чужой
              // таблицы с типом записи этой дали бы ссылки не туда.
              rowLink={data.keys && data.address === decl.address
                ? (row, i) => recordLink(screen, data.keys![i], nameOf(row)) : undefined}
              onRemoveColumn={c => setView(v => withColumnShown(v, allKeys, c.key, false))} />
            {!off && <Pager view={view} data={data} onChange={setView} />}
          </>
        )}

        {advanced && (
          <RowFilterDialog columns={filterColumns} initial={asRoot(view.filter)} wording={ADVANCED_WORDING}
            onSave={next => setView(v => withFilter(v, next))} onClose={() => setAdvanced(false)} />
        )}
      </div>

      {view.row && !off && (
        <RowPanel address={address} rowKey={view.row} filter={filter} grain={decl.grain} link={recordLink(screen, view.row)}
          onClose={() => setView(v => withRow(v, null))} />
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
    <Tooltip content="Итог по всему отбору, а не по странице">
      <div>
        <div className="font-medium text-on-surface">{total.text}</div>
        {total.note && <div className="text-warning">{total.note}</div>}
        {total.meaning && <div className="text-fg4 font-normal whitespace-normal">{total.meaning}</div>}
      </div>
    </Tooltip>
  );
}

/** Страницы: какие строки показаны, сколько их в отборе, и переход между страницами. */
function Pager({ view, data, onChange }: {
  view: TableView; data: TableData; onChange: (change: ViewChange) => void;
}) {
  const pages = pageCount(data.count, view.size);
  const button = 'p-1 rounded-md text-fg3 hover:text-on-surface hover:bg-surface-container disabled:opacity-30 disabled:hover:bg-transparent';
  return (
    <div className="mt-2 flex items-center gap-3 text-xs text-fg4">
      <span>{shownOf(data)}</span>
      <div className="flex-1" />
      <span className="flex items-center gap-1.5">
        <span aria-hidden>Строк на странице</span>
        <Select size="sm" className="w-auto" aria-label="Строк на странице"
          value={String(view.size)} onValueChange={size => onChange(v => withSize(v, Number(size)))}>
          {PAGE_SIZES.map(size => <SelectItem key={size} value={String(size)}>{size}</SelectItem>)}
        </Select>
      </span>
      {(pages > 1 || view.page > 1) && (
        <div className="flex items-center gap-1">
          <button type="button" className={button} disabled={view.page <= 1} aria-label="Предыдущая страница"
            onClick={() => onChange(v => withPageStep(v, view, -1))}>
            <ChevronLeft size={15} aria-hidden />
          </button>
          <span>Страница {view.page} из {pages}</span>
          <button type="button" className={button} disabled={view.page >= pages} aria-label="Следующая страница"
            onClick={() => onChange(v => withPageStep(v, view, 1))}>
            <ChevronRight size={15} aria-hidden />
          </button>
        </div>
      )}
    </div>
  );
}

const NO_COLUMNS: TableColumn[] = [];

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
