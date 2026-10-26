import { Route, Routes, useNavigate, useParams } from 'react-router';
import { FileSpreadsheet } from 'lucide-react';
import { useGetConstruction } from '@/shared/api/constructions';
import { costHidden, useConstructionEstimates, type Estimate } from '@/shared/api/planningEstimates';
import { DataGrid, type DataGridColumn } from '@/shared/ui/DataGrid';
import { EmptyState } from '@/shared/ui/EmptyState';
import { StatusBadge } from '@/shared/ui/StatusBadge';
import { apiError } from '@/shared/utils/apiError';
import { formatAmount, formatCount } from '@/shared/format/format';
import { KIND_LABEL, latestRevision } from './estimateRows';
import { EstimateCard } from './EstimateCard';

/**
 * Панель «Сметы» карточки стройки и страницы раздела (change `plan-09-estimate-ui`, ТЗ PLAN-20, макет П1 1а):
 * сметы этой стройки, а на странице раздела — сметы раздела (смета привязана к разделу `sectionId`). Смета
 * открывается на месте, адресом панели: `…/estimates/{id}`.
 */
export function EstimatesPanel({ constructionId, sectionId }: { constructionId: string; sectionId?: string }) {
  const base = sectionId
    ? `/document-sets/${constructionId}/sections/${sectionId}/estimates`
    : `/document-sets/${constructionId}/estimates`;
  return (
    <div className="mx-auto max-w-5xl">
      <Routes>
        <Route index element={<EstimateList constructionId={constructionId} sectionId={sectionId} base={base} />} />
        <Route path=":estimateId" element={<OpenedEstimate base={base} />} />
      </Routes>
    </div>
  );
}

function OpenedEstimate({ base }: { base: string }) {
  const { estimateId } = useParams<{ estimateId: string }>();
  return <EstimateCard key={estimateId} estimateId={estimateId!} back={{ to: base, label: 'Сметы' }} />;
}

function EstimateList({ constructionId, sectionId, base }: { constructionId: string; sectionId?: string; base: string }) {
  const estimates = useConstructionEstimates(constructionId);
  const navigate = useNavigate();
  const construction = useGetConstruction(constructionId);
  const sections = new Map((construction.data?.sections ?? []).map(s => [s.id, s.name]));

  if (estimates.isError) return <p role="alert" className="text-sm text-danger">{apiError(estimates.error, 'Сметы не прочитались')}</p>;
  if (!estimates.data) return <p className="text-sm text-fg4">Читаем сметы…</p>;

  const items = sectionId ? estimates.data.items.filter(e => e.sectionId === sectionId) : estimates.data.items;
  return (
    <div className="space-y-3">
      <div>
        <h2 className="text-base font-semibold text-fg1">Сметы</h2>
        <p className="text-xs text-fg3">План {sectionId ? 'раздела' : 'стройки'} считается по действующим редакциям смет</p>
      </div>
      {items.length === 0 ? (
        <EmptyState icon={<FileSpreadsheet size={28} aria-hidden />}
          title={sectionId ? 'У раздела смет нет' : 'У стройки смет нет'}
          description="Смета появляется загрузкой файла ЛСР из ГРАНД-Сметы." />
      ) : (
        <DataGrid columns={listColumns(estimates.data.hidden, sectionId ? null : sections)} rows={items} rowKey={e => e.id}
          onRowOpen={e => navigate(`${base}/${e.id}`)} rowLink={e => ({ to: `${base}/${e.id}`, label: `Открыть смету «${e.name}»` })} />
      )}
    </div>
  );
}

/**
 * Колонки списка смет стройки (макет П1 1а): действующая и последняя — отдельно, расхождение видно в списке.
 * «Ждут» — несопоставленные строки последней редакции: её и разбирают. Себестоимость — действующей редакции,
 * без права колонка в состоянии «нет права».
 */
function listColumns(hidden: Estimate['hidden'], sections: ReadonlyMap<string, string> | null): DataGridColumn<Estimate>[] {
  const closed = costHidden(hidden);
  return [
    {
      key: 'name', label: 'Смета', wrap: true, rowHeader: true,
      cell: e => (
        <span>
          <span className="font-medium text-fg1">{e.name}</span>
          <span className="text-fg3"> · {KIND_LABEL[e.kind]}</span>
          {sections && e.sectionId && sections.get(e.sectionId) && <span className="block text-xs text-fg3">раздел «{sections.get(e.sectionId)}»</span>}
        </span>
      ),
    },
    {
      key: 'current', label: 'Действующая', width: 'w-44',
      cell: e => {
        const current = e.revisions.find(r => r.current);
        if (!current) return <span className="text-fg3">{e.revisions.length === 0 ? 'редакций нет' : 'не выбрана'}</span>;
        return (
          <span className="inline-flex flex-wrap items-center gap-1">
            ред. {current.number}
            {current.frozen && <StatusBadge tone="neutral">{current.frozen.label}</StatusBadge>}
          </span>
        );
      },
    },
    {
      key: 'latest', label: 'Последняя', width: 'w-40',
      cell: e => {
        const latest = latestRevision(e);
        if (!latest) return <span className="text-fg4">—</span>;
        return (
          <span className="inline-flex flex-wrap items-center gap-1">
            ред. {latest.number}
            {!latest.current && <StatusBadge tone="neutral">не действующая</StatusBadge>}
          </span>
        );
      },
    },
    {
      key: 'rows', label: 'Строк', align: 'right', width: 'w-20',
      cell: e => { const latest = latestRevision(e); return latest ? <span className="tabular-nums">{formatCount(latest.rows)}</span> : <span className="text-fg4">—</span>; },
    },
    {
      key: 'waiting', label: 'Ждут', align: 'right', width: 'w-20',
      cell: e => {
        const latest = latestRevision(e);
        return latest && latest.unmatched > 0
          ? <StatusBadge tone="warning" title="Строк последней редакции не сопоставлено — в план они не входят">{formatCount(latest.unmatched)}</StatusBadge>
          : <span className="text-fg4">—</span>;
      },
    },
    {
      key: 'cost', label: 'Себестоимость', align: 'right', width: 'w-36',
      unavailable: closed ? 'no-right' : null,
      unavailableHint: closed ? `Себестоимость видна с правом «${closed.name}» — его выдаёт администратор.` : undefined,
      cell: e => {
        const total = e.revisions.find(r => r.current)?.cost?.total;
        return total == null ? <span className="text-fg4">—</span> : <span className="tabular-nums">{formatAmount(total)}</span>;
      },
    },
  ];
}
