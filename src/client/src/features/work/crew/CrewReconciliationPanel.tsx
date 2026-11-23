import { useId, useState } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { useNavigate, useSearchParams } from 'react-router';
import type { CrewGroup, CrewGroupsPage, CrewLine } from '@/shared/api/work';
import { CREW_GROUPS_KEY, useCrewGroups } from '@/shared/api/workCrew';
import { REVIEW_CARD_KEY, REVIEW_JOURNAL_KEY, REVIEW_QUEUE_KEY } from '@/shared/api/workReview';
import { formatCount, formatDate, formatDateTime, formatNumber } from '@/shared/format/format';
import { Banner } from '@/shared/ui/Banner';
import { Button } from '@/shared/ui/Button';
import { DataGrid, type DataGridColumn } from '@/shared/ui/DataGrid';
import { DateRange } from '@/shared/ui/DateRange';
import { EmptyState } from '@/shared/ui/EmptyState';
import { Icon } from '@/shared/ui/Icon';
import { Group } from '@/shared/ui/icons';
import { Select, SelectItem } from '@/shared/ui/Select';
import { StatusText, type StatusTone } from '@/shared/ui/StatusText';
import { apiError } from '@/shared/utils/apiError';
import { CrewApprovalDialog } from './CrewApprovalDialog';

const states: Record<CrewGroup['state'], { text: string; tone: StatusTone }> = {
  pending: { text: 'Ожидает утверждения', tone: 'neutral' },
  approved: { text: 'Утверждено', tone: 'success' },
  stale: { text: 'Требует решения', tone: 'warning' },
};

function CrewGroupSection({ group, page, title, shift, onApprove }: {
  group: CrewGroup;
  page: CrewGroupsPage;
  title: string;
  shift: string;
  onApprove: () => void;
}) {
  const label = useId();
  const navigate = useNavigate();
  const [params] = useSearchParams();
  const reportPath = (line: CrewLine) => `/work-review/${encodeURIComponent(line.reportId)}?${params}`;
  const status = states[group.state];
  const unit = group.unitId && page.units[group.unitId];
  const columns: DataGridColumn<CrewLine>[] = [
    { key: 'fullName', label: 'Сотрудник', rowHeader: true, wrap: true },
    { key: 'crewRole', label: 'Роль', cell: line => line.crewRole === 'lead' ? 'Бригадир' : 'Участник' },
    { key: 'variantId', label: 'Вариант работы', wrap: true,
      cell: line => line.variantId ? page.workVariants[line.variantId] ?? 'Вариант недоступен' : '—' },
    { key: 'volume', label: 'Объём', align: 'right', cell: line => formatNumber(line.volume, 4) },
    { key: 'unitId', label: 'Ед.', cell: line => page.units[line.unitId] ?? '—' },
  ];

  return <section aria-labelledby={label} className="min-w-0 space-y-3 border-t border-outline-variant pt-4">
    <div className="flex flex-wrap items-start justify-between gap-3">
      <div className="min-w-0 space-y-1">
        <h2 id={label} className="text-title-medium">{title}</h2>
        <p className="text-body-medium text-on-surface-variant">{shift}</p>
        <div className="flex flex-wrap gap-x-4 gap-y-1">
          <StatusText tone={status.tone}>{status.text}</StatusText>
          {group.mismatch && <StatusText tone="warning" icon={Group}>Есть расхождение в объёмах</StatusText>}
        </div>
        {group.unsubmitted > 0 && <p className="text-body-medium">Не подали отчёт · {formatCount(group.unsubmitted)}</p>}
        {group.total != null && <p className="text-body-medium">
          {group.state === 'stale' ? 'Прежний итог' : 'Итог'} · {formatNumber(group.total, 4)}{unit && ` ${unit}`}
          {group.approvedByName && ` · ${group.approvedByName}`}
          {group.approvedAt && ` · ${formatDateTime(group.approvedAt)}`}
        </p>}
      </div>
      {group.actions.includes('approve') && <Button variant="filled" onClick={onApprove}>Утвердить итог</Button>}
    </div>
    {group.lines.length > 0 ? <DataGrid columns={columns} rows={group.lines} rowKey={line => line.id} density="compact"
      onRowOpen={line => navigate(reportPath(line))}
      rowLink={line => ({ to: reportPath(line), label: `Открыть отчёт · ${line.fullName}` })} />
      : <p className="text-body-medium text-on-surface-variant">Нет поданных строк</p>}
  </section>;
}

export function CrewReconciliationPanel() {
  const [params, setParams] = useSearchParams();
  const [selected, setSelected] = useState<CrewGroup | null>(null);
  const filters = {
    constructionId: params.get('crewConstructionId') || undefined,
    from: params.get('crewFrom') || undefined,
    to: params.get('crewTo') || undefined,
  };
  const query = useCrewGroups(filters);
  const client = useQueryClient();
  const page = query.data;
  const loading = query.isPending || query.isPlaceholderData;
  const options = new Map(page?.constructions.map(site => [site.id, site.name]));
  if (filters.constructionId && !options.has(filters.constructionId)) options.set(filters.constructionId, 'Выбранная стройка');
  const title = (group: CrewGroup) => page?.workTypes[group.workTypeId] ?? 'Вид работы недоступен';
  const shift = (group: CrewGroup) =>
    `${options.get(group.constructionId) ?? 'Стройка недоступна'} · ${formatDate(group.date)} · ${group.shift === 'night' ? '☾ ночь' : 'день'}`;

  function update(changes: Partial<Record<'crewConstructionId' | 'crewFrom' | 'crewTo', string>>) {
    const next = new URLSearchParams(params);
    for (const [key, value] of Object.entries(changes)) {
      if (value) next.set(key, value); else next.delete(key);
    }
    setParams(next, { replace: true });
  }

  function onApproved(group: CrewGroup) {
    client.setQueriesData<CrewGroupsPage>({ queryKey: CREW_GROUPS_KEY }, current => current && ({
      ...current, items: current.items.map(item => item.id === group.id ? group : item),
    }));
    void client.invalidateQueries({ queryKey: CREW_GROUPS_KEY });
    void client.invalidateQueries({ queryKey: REVIEW_QUEUE_KEY });
    for (const reportId of new Set(group.lines.map(line => line.reportId))) {
      void client.invalidateQueries({ queryKey: [...REVIEW_CARD_KEY, reportId] });
      void client.invalidateQueries({ queryKey: [...REVIEW_JOURNAL_KEY, reportId] });
    }
  }

  return <div className="space-y-4">
    <div className="flex flex-wrap items-end gap-3">
      <div className="w-56 min-w-0 max-w-full">
        <Select label="Стройка" density="dense" value={filters.constructionId ?? ''} emptyItem="Все стройки в охвате"
          onValueChange={value => update({ crewConstructionId: value })}>
          {[...options].map(([id, name]) => <SelectItem key={id} value={id}>{name}</SelectItem>)}
        </Select>
      </div>
      <DateRange label="Дата смены" from={filters.from ?? ''} to={filters.to ?? ''} ordered className="w-100 max-w-full"
        onChange={(from, to) => update({ crewFrom: from, crewTo: to })} />
      {(filters.constructionId || filters.from || filters.to) && <Button variant="text"
        onClick={() => update({ crewConstructionId: '', crewFrom: '', crewTo: '' })}>Сбросить отбор</Button>}
    </div>
    {loading && <p role="status" className="text-body-medium text-on-surface-variant">Загрузка групп сверки…</p>}
    {query.isError && <Banner tone="error" actions={[{ label: 'Повторить', onClick: () => void query.refetch() }]}>
      {apiError(query.error, 'Не удалось загрузить группы сверки.')}
    </Banner>}
    {!loading && !query.isError && page && (page.items.length > 0
      ? page.items.map(group => <CrewGroupSection key={group.id} group={group} page={page} title={title(group)}
        shift={shift(group)} onApprove={() => setSelected(group)} />)
      : <EmptyState icon={<Icon icon={Group} size={24} />} title="Нет групп сверки"
        description={page.constructions.length > 0 ? 'Измените стройку или период.' : undefined} />)}
    {selected && page && <CrewApprovalDialog group={selected} work={title(selected)} shift={shift(selected)}
      units={page.units} onClose={() => setSelected(null)} onApproved={onApproved}
      onReread={() => query.refetch({ throwOnError: true })} />}
  </div>;
}
