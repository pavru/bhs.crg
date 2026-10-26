import { useMemo, useState } from 'react';
import { Route, Routes, useNavigate, useParams } from 'react-router';
import { FileSpreadsheet } from 'lucide-react';
import { useConstructionDirectory } from '@/shared/api/constructions';
import { useAllEstimates, type Estimate } from '@/shared/api/planningEstimates';
import { Button } from '@/shared/ui/Button';
import { DataGrid, type DataGridColumn } from '@/shared/ui/DataGrid';
import { EmptyState } from '@/shared/ui/EmptyState';
import { PageHeader } from '@/shared/ui/PageHeader';
import { SearchInput } from '@/shared/ui/SearchInput';
import { StatusBadge } from '@/shared/ui/StatusBadge';
import { apiError } from '@/shared/utils/apiError';
import { formatCount, formatDate } from '@/shared/format/format';
import { SitesFilter } from '@/shared/siteFilter/SitesFilter';
import { useSiteFilter } from '@/shared/siteFilter/siteFilterContext';
import { KIND_LABEL, latestRevision } from './estimateRows';
import { EstimateCard } from './EstimateCard';

/**
 * Раздел «Сметы» модуля (change `plan-09-estimate-ui`, ТЗ PLAN-20, макет П1 1ж): сметы всех строек,
 * сгруппированные по стройкам. Смета открывается здесь же, `/estimates/{id}` — раздел рейла остаётся «Планы».
 */
export function EstimatesPage() {
  return (
    <div className="h-full overflow-auto">
      <div className="mx-auto max-w-5xl px-6 py-5 space-y-4">
        <Routes>
          <Route index element={<AllEstimates />} />
          <Route path=":estimateId" element={<OpenedEstimate />} />
        </Routes>
      </div>
    </div>
  );
}

function OpenedEstimate() {
  const { estimateId } = useParams<{ estimateId: string }>();
  return <EstimateCard key={estimateId} estimateId={estimateId!} back={{ to: '/estimates', label: 'Сметы' }} />;
}

/** Строка сетки: заголовок стройки либо смета под ним. */
type Line = { kind: 'site'; key: string; name: string } | { kind: 'estimate'; key: string; estimate: Estimate };

function AllEstimates() {
  const pages = useAllEstimates();
  const sites = useConstructionDirectory();
  const navigate = useNavigate();
  const [query, setQuery] = useState('');
  // Избранные стройки (#1432) урезают список, как любой список по стройкам; пока фильтр не известен, смет
  // не показываем вовсе — ни полный список, ни урезанный не были бы правдой (ниже — «Читаем сметы…», а не
  // пустая сетка «ничего не подходит» со «скрыто N»).
  const filter = useSiteFilter();

  const loaded = useMemo(() => pages.data?.pages.flatMap(p => p.items) ?? [], [pages.data]);
  const shown = useMemo(() => (filter.status === 'loading' ? [] : loaded.filter(e => filter.visible(e.constructionId))),
    [loaded, filter]);
  const lines = useMemo(() => {
    const names = new Map((sites.data ?? []).map(s => [s.id, s.name]));
    return group(shown, id => names.get(id) ?? '…', query);
  }, [shown, sites.data, query]);

  return (
    <>
      <PageHeader icon={<FileSpreadsheet size={18} aria-hidden />} title="Сметы" subtitle="Сметы всех строек, по стройкам" />
      {pages.isError ? (
        <p role="alert" className="text-sm text-danger">{apiError(pages.error, 'Сметы не прочитались')}</p>
      ) : !pages.data || filter.status === 'loading' ? (
        <p className="text-sm text-fg4">Читаем сметы…</p>
      ) : loaded.length === 0 ? (
        <EmptyState icon={<FileSpreadsheet size={28} aria-hidden />} title="Смет пока нет"
          description="Смета появляется загрузкой файла ЛСР из ГРАНД-Сметы в стройку." />
      ) : (
        <>
          <div className="flex flex-wrap items-center gap-2">
            <SearchInput value={query} onChange={setQuery} placeholder="Смета или стройка" ariaLabel="Поиск сметы или стройки"
              className="w-72 max-w-full" />
            {/* Число скрытых — о прочитанных страницах: дальше списка ещё нет, и «скрыто N» было бы неполным. */}
            <SitesFilter hidden={loaded.length - shown.length} partial={pages.hasNextPage} />
          </div>
          <DataGrid columns={COLUMNS} rows={lines} rowKey={l => l.key}
            state={query.trim() || shown.length < loaded.length ? 'filtered-out' : undefined}
            section={l => (l.kind === 'site' ? <span className="text-sm font-medium text-fg1">{l.name}</span> : null)}
            onRowOpen={l => { if (l.kind === 'estimate') navigate(`/estimates/${l.estimate.id}`); }}
            rowLink={l => (l.kind === 'estimate' ? { to: `/estimates/${l.estimate.id}`, label: `Открыть смету «${l.estimate.name}»` } : null)} />
          {pages.hasNextPage && (
            <Button variant="outlined" size="sm" disabled={pages.isFetchingNextPage} onClick={() => void pages.fetchNextPage()}>
              {pages.isFetchingNextPage ? 'Читаем…' : 'Показать ещё'}
            </Button>
          )}
        </>
      )}
    </>
  );
}

/**
 * Сметы по стройкам: стройки по названию, сметы — в порядке сервера (основные первыми). Поиск — по смете и
 * стройке: стройка, совпавшая названием, показывается со всеми своими сметами.
 */
function group(items: Estimate[], siteName: (id: string) => string, query: string): Line[] {
  const q = query.trim().toLocaleLowerCase('ru');
  const bySite = new Map<string, Estimate[]>();
  for (const e of items) {
    const site = siteName(e.constructionId);
    if (q && !site.toLocaleLowerCase('ru').includes(q) && !e.name.toLocaleLowerCase('ru').includes(q)) continue;
    const list = bySite.get(e.constructionId);
    if (list) list.push(e); else bySite.set(e.constructionId, [e]);
  }
  return [...bySite.entries()]
    .sort(([a], [b]) => siteName(a).localeCompare(siteName(b), 'ru'))
    .flatMap(([site, list]): Line[] => [
      { kind: 'site', key: `site:${site}`, name: siteName(site) },
      ...list.map((estimate): Line => ({ kind: 'estimate', key: estimate.id, estimate })),
    ]);
}

const est = (l: Line) => (l as Extract<Line, { kind: 'estimate' }>).estimate;

const COLUMNS: DataGridColumn<Line>[] = [
  {
    key: 'name', label: 'Смета', wrap: true, rowHeader: true,
    cell: l => <span><span className="font-medium">{est(l).name}</span><span className="text-fg3"> · {KIND_LABEL[est(l).kind]}</span></span>,
  },
  {
    key: 'current', label: 'Действующая', width: 'w-36',
    cell: l => {
      const e = est(l);
      if (e.currentRevisionNumber === null) return <span className="text-fg3">{e.revisions.length === 0 ? 'редакций нет' : 'не выбрана'}</span>;
      return `ред. ${e.currentRevisionNumber} из ${e.revisions.length}`;
    },
  },
  {
    key: 'rows', label: 'Строк', align: 'right', width: 'w-20',
    cell: l => { const r = latestRevision(est(l)); return r ? <span className="tabular-nums">{formatCount(r.rows)}</span> : <span className="text-fg4">—</span>; },
  },
  {
    key: 'waiting', label: 'Ждут', align: 'right', width: 'w-20',
    cell: l => {
      const r = latestRevision(est(l));
      return r && r.unmatched > 0
        ? <StatusBadge tone="warning" title="Строк последней редакции не сопоставлено — в план они не входят">{formatCount(r.unmatched)}</StatusBadge>
        : <span className="text-fg4">—</span>;
    },
  },
  {
    key: 'changed', label: 'Изменена', width: 'w-28',
    cell: l => { const r = latestRevision(est(l)); return r ? formatDate(r.importedAt) : <span className="text-fg4">—</span>; },
  },
];
