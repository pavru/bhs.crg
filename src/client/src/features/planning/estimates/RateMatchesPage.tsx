import { useState } from 'react';
import { Icon } from '@/shared/ui/Icon';
import { Bookmark, Delete, MyLocation } from '@/shared/ui/icons';
import { useCan } from '@/shared/api/access';
import {
  useForgetMatch, useMatchList, type MatchIssue, type MatchListKind, type RateMatch, type ResourceMatch,
} from '@/shared/api/planningMatches';
import { Button } from '@/shared/ui/Button';
import { ConfirmDialog } from '@/shared/ui/ConfirmDialog';
import { DataGrid, type DataGridColumn } from '@/shared/ui/DataGrid';
import { PageHeader } from '@/shared/ui/PageHeader';
import { RowActionsMenu } from '@/shared/ui/RowActionsMenu';
import { SearchInput } from '@/shared/ui/SearchInput';
import { StatusBadge } from '@/shared/ui/StatusBadge';
import { TabPanel, Tabs } from '@/shared/ui/Tabs';
import { useToast } from '@/shared/ui/Toast';
import { apiError } from '@/shared/utils/apiError';
import { formatCount, formatDate, formatQuantity } from '@/shared/format/format';
import { useMedia, WIDE } from '@/shared/hooks/useMedia';
import { PointRateDialog, PointResourceDialog } from './PointMatchDialogs';

/**
 * «Запомненные сопоставления» (ТЗ PLAN-6.1, ответ П1: пункт «Планирование › Справочники»): шифры → виды работ и
 * ресурсы → номенклатура, с «перенацелить» и «забыть».
 *
 * Зачем он помимо удобства: запомненное держит вид работы, единицу и позицию от удаления (CORE-34.1), а держатель,
 * которого нечем снять, делает запись неудаляемой. И один шифр в разных сметах может значить разные работы.
 * Правка — с `plan.estimate.edit` на широком экране; без права список только читается.
 */
export function RateMatchesPage() {
  const [tab, setTab] = useState<MatchListKind>('rate');
  return (
    <div className="h-full overflow-auto">
      <div className="mx-auto max-w-5xl px-6 py-5 space-y-4">
        <PageHeader icon={<Icon icon={Bookmark} size={18} aria-hidden />} title="Запомненные сопоставления"
          subtitle="Что импорт смет подставляет сам: шифры — видами работ, ресурсы — позициями номенклатуры" />
        <Tabs id="match-lists" value={tab} onChange={setTab}
          items={[{ value: 'rate', label: 'Шифры расценок' }, { value: 'resource', label: 'Материалы' }]} />
        <TabPanel id="match-lists" value={tab}>
          {tab === 'rate' ? <RateList /> : <ResourceList />}
        </TabPanel>
      </div>
    </div>
  );
}

const ISSUE_TEXT: Record<Exclude<MatchIssue, null>, string> = {
  lost: 'не подставится: цели больше нет',
  archived: 'не подставится: цель в архиве',
};

function Issue({ issue }: { issue: MatchIssue }) {
  return issue ? <StatusBadge tone="warning">{ISSUE_TEXT[issue]}</StatusBadge> : null;
}

/** Правка списка: право и широкий экран — как у смет (на телефоне сметы только читают). */
function useListEdit() {
  const can = useCan();
  const wide = useMedia(WIDE);
  return can.permission('plan.estimate.edit') && wide;
}

function RateList() {
  const [query, setQuery] = useState('');
  const list = useMatchList<RateMatch>('rate', query);
  const editable = useListEdit();
  const [pointing, setPointing] = useState<string | null>(null);
  const [forgetting, setForgetting] = useState<string | null>(null);
  const items = list.data?.pages.flatMap(p => p.items) ?? [];
  // Строка окна — из перечитанного списка: после отказа 409 повтор идёт по новой версии.
  const pointed = items.find(i => i.id === pointing) ?? null;

  const columns: DataGridColumn<RateMatch>[] = [
    { key: 'rateCode', label: 'Шифр', width: 'w-44', rowHeader: true, cell: m => <span className="font-mono text-body-small">{m.rateCode}</span> },
    {
      key: 'workType', label: 'Вид работы', wrap: true,
      cell: m => (
        <div className="flex flex-col items-start gap-0.5">
          <span className="text-body-medium">{m.workTypeName ?? 'вид работы удалён'}</span>
          <Issue issue={m.issue} />
        </div>
      ),
    },
    {
      key: 'factor', label: 'Пересчёт', width: 'w-40',
      cell: m => <span className="text-body-small text-on-surface-variant tabular-nums">{m.sourceUnit ?? 'без единицы'} → {m.unitName ?? '?'} × {formatQuantity(m.factor)}</span>,
    },
    { key: 'updated', label: 'Изменено', width: 'w-36', cell: m => <Updated at={m.updatedAt} by={m.updatedBy} /> },
    ...(editable ? [actionsColumn<RateMatch>(m => setPointing(m.id), m => setForgetting(m.id))] : []),
  ];

  return (
    <MatchListBody list={list} query={query} onQuery={setQuery} placeholder="Шифр расценки" empty="Запомненных шифров нет"
      grid={<DataGrid columns={columns} rows={items} rowKey={m => m.id} fixed />}>
      {pointed && <PointRateDialog item={pointed} onClose={() => setPointing(null)} />}
      <Forget kind="rate" item={items.find(i => i.id === forgetting) ?? null} what={m => `шифр «${(m as RateMatch).rateCode}»`}
        onClose={() => setForgetting(null)} />
    </MatchListBody>
  );
}

function ResourceList() {
  const [query, setQuery] = useState('');
  const list = useMatchList<ResourceMatch>('resource', query);
  const editable = useListEdit();
  const [pointing, setPointing] = useState<string | null>(null);
  const [forgetting, setForgetting] = useState<string | null>(null);
  const items = list.data?.pages.flatMap(p => p.items) ?? [];
  const pointed = items.find(i => i.id === pointing) ?? null;

  const columns: DataGridColumn<ResourceMatch>[] = [
    {
      key: 'source', label: 'Материал в смете', wrap: true, rowHeader: true,
      cell: m => (
        <div className="flex flex-col">
          <span className="text-body-medium">{m.sourceText}</span>
          <span className="text-label-small text-on-surface-variant">{m.kind === 'code' ? 'по коду ресурса' : 'по наименованию'}</span>
        </div>
      ),
    },
    {
      key: 'position', label: 'Позиция номенклатуры', wrap: true,
      cell: m => (
        <div className="flex flex-col items-start gap-0.5">
          <span className="text-body-medium">{m.nomenclatureName ?? (m.issue === 'lost' ? 'позиция удалена' : 'позиция без названия')}</span>
          <Issue issue={m.issue} />
        </div>
      ),
    },
    { key: 'updated', label: 'Изменено', width: 'w-36', cell: m => <Updated at={m.updatedAt} by={m.updatedBy} /> },
    ...(editable ? [actionsColumn<ResourceMatch>(m => setPointing(m.id), m => setForgetting(m.id))] : []),
  ];

  return (
    <MatchListBody list={list} query={query} onQuery={setQuery} placeholder="Код или наименование ресурса"
      empty="Запомненных материалов нет" grid={<DataGrid columns={columns} rows={items} rowKey={m => m.id} fixed />}>
      {pointed && <PointResourceDialog item={pointed} onClose={() => setPointing(null)} />}
      <Forget kind="resource" item={items.find(i => i.id === forgetting) ?? null}
        what={m => `материал «${(m as ResourceMatch).sourceText}»`} onClose={() => setForgetting(null)} />
    </MatchListBody>
  );
}

function actionsColumn<T>(onPoint: (m: T) => void, onForget: (m: T) => void): DataGridColumn<T> {
  return {
    key: 'actions', label: 'Действия', width: 'w-12',
    cell: m => (
      <RowActionsMenu actions={[
        { key: 'point', label: 'Перенацелить…', icon: <Icon icon={MyLocation} size={18} />, onSelect: () => onPoint(m) },
        { key: 'forget', label: 'Забыть…', icon: <Icon icon={Delete} size={18} />, danger: true, onSelect: () => onForget(m) },
      ]} />
    ),
  };
}

function Updated({ at, by }: { at: string; by: string | null }) {
  return <span className="text-body-small text-on-surface-variant">{formatDate(at)}{by ? `, ${by}` : ''}</span>;
}

function MatchListBody({ list, query, onQuery, placeholder, empty, grid, children }: {
  list: ReturnType<typeof useMatchList>;
  query: string;
  onQuery: (q: string) => void;
  placeholder: string;
  empty: string;
  grid: React.ReactNode;
  children: React.ReactNode;
}) {
  const pages = list.data?.pages ?? [];
  const total = pages[0]?.total ?? 0;
  const read = pages.reduce((n, p) => n + p.items.length, 0);
  return (
    <div className="space-y-3 pt-3">
      <SearchInput value={query} onChange={onQuery} placeholder={placeholder} ariaLabel={placeholder} className="w-80 max-w-full" />
      {list.isError ? (
        <p role="alert" className="text-body-medium text-error">{apiError(list.error, 'Список не прочитался')}</p>
      ) : !list.data ? (
        <p className="text-body-medium text-on-surface-variant">Читаем запомненное…</p>
      ) : total === 0 ? (
        <p className="text-body-medium text-on-surface-variant">{query.trim() ? 'Под этот отбор ничего не запомнено.' : empty}</p>
      ) : (
        <>
          {grid}
          {/* Порция — не весь список: без числа она читалась бы как всё запомненное. */}
          <div className="flex items-center gap-3 text-body-small text-on-surface-variant">
            <span>Показано {formatCount(read)} из {formatCount(total)}</span>
            {list.hasNextPage && (
              <Button variant="text" loading={list.isFetchingNextPage} onClick={() => void list.fetchNextPage()}>Показать ещё</Button>
            )}
          </div>
        </>
      )}
      {children}
    </div>
  );
}

/**
 * «Забыть» — с подтверждением: запомненное уходит безвозвратно. Строки, уже сопоставленные им, остаются, и их
 * пометка скажет «с тех пор забыли». Отказ 409 называет окно подтверждения словами сервера.
 */
function Forget({ kind, item, what, onClose }: {
  kind: MatchListKind;
  item: { id: string; version: string } | null;
  what: (m: { id: string; version: string }) => string;
  onClose: () => void;
}) {
  const forget = useForgetMatch(kind);
  const toast = useToast();
  return (
    <ConfirmDialog open={item !== null} onOpenChange={o => { if (!o) onClose(); }}
      title="Забыть запомненное?"
      description={item ? `Следующие импорты перестанут подставлять ${what(item)} сами. Строки, уже сопоставленные им, останутся как есть.` : undefined}
      confirmLabel="Забыть" confirmDanger errorTitle="Не забыто"
      onConfirm={async () => {
        if (!item) throw new Error('Этого запомненного в списке уже нет — его забыли или отбор изменился.');
        await forget.mutateAsync(item);
        toast.success(`Забыто: ${what(item)}`);
      }} />
  );
}
