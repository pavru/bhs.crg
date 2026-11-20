import { useState, type ReactNode } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { Add, Delete, Edit, Group } from '@/shared/ui/icons';
import { Icon } from '@/shared/ui/Icon';
import { Button } from '@/shared/ui/Button';
import { ConfirmDialog } from '@/shared/ui/ConfirmDialog';
import { DataGrid, type DataGridColumn } from '@/shared/ui/DataGrid';
import { EmptyState } from '@/shared/ui/EmptyState';
import { RowActionsMenu, type RowAction } from '@/shared/ui/RowActionsMenu';
import { Select, SelectItem } from '@/shared/ui/Select';
import { StatusText } from '@/shared/ui/StatusText';
import { Tabs, TabPanel } from '@/shared/ui/Tabs';
import { Timeline, type TimelineRow, type TimelineTone } from '@/shared/ui/Timeline';
import { formatDate } from '@/shared/format/format';
import { apiError } from '@/shared/utils/apiError';
import {
  ASSIGNMENTS_KEY, KIND_LABEL, useAssignments, useRemoveAssignment, type Assignment, type AssignmentSite,
} from '@/shared/api/workAssignments';
import { barLabel, filterAssignments, periodLabel, phaseLabel, phaseOf, type AssignmentFilter } from './assignmentRows';
import { AssignmentDialog } from './AssignmentDialog';
import { BulkAssignDialog } from './BulkAssignDialog';

/**
 * Панель «Назначения» карточки стройки (change `work-12-assignments-ui`, ТЗ WORK-21, WORK-46; макет Р2, вариант А+,
 * выбор владельца 20.11.2026): назначения стройки таблицей или лентой, «Назначить на период», правка периода и
 * снятие из меню строки.
 *
 * Что можно, решает СЕРВЕР: кнопки правки есть, только если ответ разрешает спрашивающему назначать на стройке
 * (`construction.canAssign`); иначе действий нет вовсе, а строка над списком называет причину из ответа. Пункт меню
 * строки — по `canChange` / `canRemove`: «Снять» у последнего менеджера скрыт, окно с отказом 409 — только для
 * гонки. Порядок строк — сервера (менеджеры первыми). Вид панели — удобство зрителя, запоминается в браузере.
 */
export function AssignmentsPanel({ constructionId }: { constructionId: string }) {
  const assignments = useAssignments(constructionId);
  const qc = useQueryClient();
  const [view, setView] = useState<PanelView>(storedView);
  const [filter, setFilter] = useState<AssignmentFilter>('current');
  const [bulk, setBulk] = useState(false);
  const [editing, setEditing] = useState<Assignment | null>(null);
  const [removing, setRemoving] = useState<Assignment | null>(null);
  const remove = useRemoveAssignment();

  if (assignments.isError) {
    return <p role="alert" className="text-body-medium text-error">{apiError(assignments.error, 'Назначения не прочитались')}</p>;
  }
  if (!assignments.data) return <p className="text-body-medium text-on-surface-variant">Читаем назначения…</p>;
  if (assignments.data.missing !== null) {
    return (
      <Shell>
        <Header />
        <p role="alert" className="text-body-medium text-error">{assignments.data.missing}</p>
      </Shell>
    );
  }

  const { construction: site, items } = assignments.data.page;
  const { shown, hidden } = filterAssignments(items, filter, site.today);

  const reread = async (id: string): Promise<Assignment | null> => {
    const result = await assignments.refetch({ throwOnError: true });
    const page = result.data?.page;
    return page?.items.find(a => a.id === id) ?? null;
  };

  const actionsOf = (a: Assignment): RowAction[] => {
    if (!site.canAssign) return [];
    const list: RowAction[] = [];
    if (a.canChange) list.push({ key: 'change', label: 'Изменить период', icon: Edit, onSelect: () => setEditing(a) });
    if (a.canRemove) list.push({ key: 'remove', label: 'Снять назначение', icon: Delete, danger: true, onSelect: () => setRemoving(a) });
    return list;
  };
  const menu = (a: Assignment) => {
    const list = actionsOf(a);
    return list.length > 0 ? <RowActionsMenu actions={list} ariaLabel={`Действия назначения: ${a.employeeName}`} /> : null;
  };

  const chooseView = (next: PanelView) => { setView(next); storeView(next); };

  return (
    <Shell>
      <Header site={site} count={items.length}>
        {items.length > 0 && (
          <>
            <Tabs id={VIEW_TABS} variant="segmented" aria-label="Вид панели" value={view} onChange={chooseView}
              items={[{ value: 'table', label: 'Таблица' }, { value: 'timeline', label: 'Лента' }]} />
            {site.today && (
              <Select variant="chip" aria-label="Отбор назначений" emptyItem="Все назначения" value={filter}
                onValueChange={v => setFilter(v === 'current' ? 'current' : '')}>
                <SelectItem value="current">Действующие и будущие</SelectItem>
              </Select>
            )}
          </>
        )}
        {site.canAssign && items.length > 0 && (
          <Button variant="tonal" icon={<Icon icon={Add} size={18} aria-hidden />} onClick={() => setBulk(true)}>
            Назначить на период
          </Button>
        )}
      </Header>

      {!site.canAssign && site.reason && (
        <p className="text-body-medium text-on-surface-variant" data-testid="assignments-reason">{site.reason}</p>
      )}

      {items.length === 0 ? (
        <EmptyState icon={<Icon icon={Group} size={40} aria-hidden />} title="На стройку никто не назначен"
          description={site.canAssign
            ? 'Назначьте менеджера и монтажников на период.'
            : undefined}
          action={site.canAssign ? (
            <Button variant="tonal" icon={<Icon icon={Add} size={18} aria-hidden />} onClick={() => setBulk(true)}>
              Назначить на период
            </Button>
          ) : undefined} />
      ) : (
        <TabPanel id={VIEW_TABS} value={view}>
          {view === 'table' ? (
            <DataGrid columns={columns(site, menu)} rows={shown} rowKey={a => a.id}
              state={hidden > 0 ? 'filtered-out' : 'no-data'} />
          ) : site.today ? (
            <Timeline aria-label="Лента назначений" nameLabel="Сотрудник" today={site.today}
              rows={shown.map(a => timelineRow(a, site, menu(a), actionsOf(a).some(x => x.key === 'change') ? () => setEditing(a) : undefined))} />
          ) : (
            <p className="text-body-medium text-on-surface-variant">
              Ленте нужна сегодняшняя дата стройки, а пояс стройки не определён — назначения видны в таблице.
            </p>
          )}
          {hidden > 0 && (
            <p className="mt-2 text-body-small text-on-surface-variant">
              Ещё {hidden} закончившихся — их покажет отбор «Все назначения».
            </p>
          )}
        </TabPanel>
      )}

      {bulk && (
        <BulkAssignDialog constructionId={constructionId} siteName={site.name} today={site.today} onClose={() => setBulk(false)} />
      )}
      {editing && (
        <AssignmentDialog assignment={editing} siteName={site.name} onReread={() => reread(editing.id)}
          onClose={() => setEditing(null)} />
      )}
      <ConfirmDialog open={removing !== null} onOpenChange={o => { if (!o) setRemoving(null); }}
        title="Снять назначение?" confirmLabel="Снять назначение" errorTitle="Назначение не снято"
        description={removing && (
          <p>
            {removing.employeeName} перестанет быть {removing.kind === 'manager' ? 'менеджером' : 'монтажником'} стройки
            «{site.name}» ({periodLabel(removing)}). Отчёты, которые он уже ввёл, останутся.
          </p>
        )}
        onConfirm={() => remove.mutateAsync({ id: removing!.id, version: removing!.version }).catch(e => {
          // Признаки соседей («последний менеджер») и версия могли измениться — список перечитывается сразу.
          void qc.invalidateQueries({ queryKey: ASSIGNMENTS_KEY });
          throw e;
        })}
        onRefused={() => ({ note: 'Назначение осталось в списке. Перечитайте назначения стройки — признаки могли измениться.' })} />
    </Shell>
  );
}

type PanelView = 'table' | 'timeline';

const VIEW_TABS = 'work-assignments-view';
const VIEW_KEY = 'crg.work.assignments.view';

/** Выбор вида — удобство зрителя: без хранилища (частное окно) панель просто открывается таблицей. */
function storedView(): PanelView {
  try { return localStorage.getItem(VIEW_KEY) === 'timeline' ? 'timeline' : 'table'; } catch { return 'table'; }
}

function storeView(view: PanelView) {
  try { localStorage.setItem(VIEW_KEY, view); } catch { /* хранилище недоступно — выбор живёт до ухода с панели */ }
}

function Shell({ children }: { children: ReactNode }) {
  return <div className="mx-auto max-w-5xl space-y-3">{children}</div>;
}

function Header({ site, count, children }: { site?: AssignmentSite; count?: number; children?: ReactNode }) {
  return (
    <div className="flex flex-wrap items-center gap-3">
      <div className="min-w-0 flex-1">
        <h2 className="text-title-large text-on-surface">Назначения</h2>
        {site?.today && count !== undefined && (
          <p className="text-body-small text-on-surface-variant">Сегодня на стройке — {formatDate(site.today)}</p>
        )}
      </div>
      {children}
    </div>
  );
}

function columns(site: AssignmentSite, menu: (a: Assignment) => ReactNode): DataGridColumn<Assignment>[] {
  return [
    {
      key: 'employee', label: 'Сотрудник', rowHeader: true, wrap: true,
      cell: a => (
        <span className="flex flex-col">
          <span className="text-body-medium text-on-surface">{a.employeeName}</span>
          {a.personnelNumber && <span className="text-body-small text-on-surface-variant">Таб. № {a.personnelNumber}</span>}
        </span>
      ),
    },
    { key: 'kind', label: 'Вид', width: 'w-32', cell: a => KIND_LABEL[a.kind] },
    { key: 'period', label: 'Период', width: 'w-60', cell: a => periodLabel(a) },
    {
      key: 'phase', label: 'Состояние', width: 'w-36',
      cell: a => <StatusText tone={a.active ? 'success' : 'neutral'}>{phaseLabel(a, site.today)}</StatusText>,
    },
    { key: 'actions', label: 'Действия', width: 'w-14', interactive: true, cell: a => menu(a) },
  ];
}

/** Тон полосы: менеджер — вторичный, действующий монтажник — основной, будущий — контур, закончившийся — тихий. */
function toneOf(a: Assignment, today: string | null): TimelineTone {
  const phase = phaseOf(a, today);
  if (phase === 'past') return 'muted';
  if (a.kind === 'manager') return 'secondary';
  return phase === 'future' ? 'outline' : 'primary';
}

function timelineRow(a: Assignment, site: AssignmentSite, actions: ReactNode, onOpen?: () => void): TimelineRow {
  const label = periodLabel(a);
  return {
    key: a.id, label: a.employeeName, sublabel: KIND_LABEL[a.kind], from: a.dateFrom, to: a.dateTo,
    tone: toneOf(a, site.today), barLabel: barLabel(a),
    aria: onOpen ? `${a.employeeName}, ${KIND_LABEL[a.kind]}, ${label} — изменить период` : `${a.employeeName}, ${KIND_LABEL[a.kind]}, ${label}`,
    onOpen, actions,
  };
}
