import { useId, useState } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { useNavigate, useSearchParams } from 'react-router';
import type { CrewGroup, CrewGroupsPage, CrewLine, CrewPublication } from '@/shared/api/work';
import { CREW_GROUPS_KEY, useCrewGroups } from '@/shared/api/workCrew';
import { invalidateWorkProgress } from '@/shared/api/workReports';
import { REVIEW_CARD_KEY, REVIEW_JOURNAL_KEY, REVIEW_QUEUE_KEY } from '@/shared/api/workReview';
import { formatCount, formatDate, formatDateTime, formatNumber } from '@/shared/format/format';
import { Banner } from '@/shared/ui/Banner';
import { Button } from '@/shared/ui/Button';
import { Card } from '@/shared/ui/Card';
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

const exclusions: Record<NonNullable<CrewPublication['reason']>, string> = {
  empty: 'В группе нет текущих поданных строк.',
  'not-approved': 'Группа ждёт утверждения итога.',
  stale: 'После утверждения изменились строки или состав группы. Нужно повторное утверждение.',
  'awaiting-acceptance': 'Приёмка участвующих отчётов ещё не завершена.',
  'invalid-key': 'Позиция перечня или единица измерения не согласована со строками группы.',
  'multiple-keys': 'В строках группы разные позиции перечня.',
};

function PublicationSummary({ group, page, title }: { group: CrewGroup; page: CrewGroupsPage; title: string }) {
  const { published, reason, keys, accepted, participating } = group.publication;
  const unit = group.unitId && page.units[group.unitId];
  return <Card variant="filled">
    <div className="grid gap-3 sm:grid-cols-2">
      <div className="min-w-0 space-y-1">
        <h3 className="text-title-small">Итог сверки</h3>
        {group.total != null && <p className="text-body-medium">
          {group.state === 'stale' ? 'Прежний итог' : 'Итог'} · {formatNumber(group.total, 4)}{unit && ` ${unit}`}
          {group.approvedByName && ` · ${group.approvedByName}`}
          {group.approvedAt && ` · ${formatDateTime(group.approvedAt)}`}
        </p>}
        <p className="text-body-medium">Принято отчётов · {formatCount(accepted)} из {formatCount(participating)}</p>
        {group.unsubmitted > 0 && <p className="text-body-medium">Не подали отчёт · {formatCount(group.unsubmitted)}</p>}
      </div>
      <div className="min-w-0 space-y-1">
        <h3 className="text-title-small">Факт по перечню работ</h3>
        <StatusText tone={published ? 'success' : 'warning'}>{published ? 'Включён в факт' : 'Не включён в факт'}</StatusText>
        {reason && <p className="text-body-medium">{exclusions[reason]}</p>}
        {keys.length > 0 && <ul className="space-y-1 text-body-medium" aria-label="Позиции перечня">
          {keys.map(key => <li key={`${key.planItemId}/${key.sectionId}/${key.unitId}`}>
            {key.valid ? title : 'Негодная ссылка на позицию'} · {page.units[key.unitId] ?? 'Единица недоступна'}
            <span className="block text-on-surface-variant sm:inline">
              <span className="hidden sm:inline"> · </span>
              {key.sectionId === null ? 'Стройка в целом' : page.sections[key.sectionId] ?? 'Раздел недоступен'}
            </span>
          </li>)}
        </ul>}
      </div>
    </div>
  </Card>;
}

function CrewGroupSection({ group, page, title, shift, canReadReports, onApprove }: {
  group: CrewGroup;
  page: CrewGroupsPage;
  title: string;
  shift: string;
  canReadReports: boolean;
  onApprove: () => void;
}) {
  const label = useId();
  const navigate = useNavigate();
  const [params] = useSearchParams();
  const reportPath = (line: CrewLine) => `/work-review/${encodeURIComponent(line.reportId)}?${params}`;
  const status = states[group.state];
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
      </div>
      {group.actions.includes('approve') && <Button variant="tonal" onClick={onApprove}>Утвердить итог</Button>}
    </div>
    <PublicationSummary group={group} page={page} title={title} />
    {group.lines.length > 0 ? <DataGrid columns={columns} rows={group.lines} rowKey={line => line.id}
      onRowOpen={canReadReports ? line => navigate(reportPath(line)) : undefined}
      rowLink={canReadReports ? line => ({ to: reportPath(line), label: `Открыть отчёт · ${line.fullName}` }) : undefined} />
      : <p className="text-body-medium text-on-surface-variant">Нет поданных строк</p>}
  </section>;
}

export function CrewReconciliationPanel({ canReadReports }: { canReadReports: boolean }) {
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
    void invalidateWorkProgress(client, group.constructionId);
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
        shift={shift(group)} canReadReports={canReadReports} onApprove={() => setSelected(group)} />)
      : <EmptyState icon={<Icon icon={Group} size={24} />} title="Нет групп сверки"
        description={page.constructions.length > 0 ? 'Измените стройку или период.' : undefined} />)}
    {selected && page && <CrewApprovalDialog group={selected} work={title(selected)} shift={shift(selected)}
      units={page.units} onClose={() => setSelected(null)} onApproved={onApproved}
      onReread={() => query.refetch({ throwOnError: true })} />}
  </div>;
}
