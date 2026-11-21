import { useEffect, useState, type ReactNode } from 'react';
import { Button } from '@/shared/ui/Button';
import { Banner } from '@/shared/ui/Banner';
import { ConfirmDialog } from '@/shared/ui/ConfirmDialog';
import { DataGrid, type DataGridColumn } from '@/shared/ui/DataGrid';
import { EmptyState } from '@/shared/ui/EmptyState';
import { SearchInput } from '@/shared/ui/SearchInput';
import { RowActionsMenu } from '@/shared/ui/RowActionsMenu';
import { Select, SelectItem } from '@/shared/ui/Select';
import { StatusText } from '@/shared/ui/StatusText';
import { Timeline } from '@/shared/ui/Timeline';
import { Icon } from '@/shared/ui/Icon';
import { Add, Group, Edit, Delete } from '@/shared/ui/icons';
import { useAssignments, useRemoveAssignment, type Assignment } from '@/shared/api/workAssignments';
import { AssignmentDialog } from '@/features/work/assignments/AssignmentDialog';
import { BulkAssignDialog } from '@/features/work/assignments/BulkAssignDialog';
import { barLabel, filterAssignments, periodLabel, phaseLabel, phaseOf } from '@/features/work/assignments/assignmentRows';
import { Composer } from './Composer';
import { constructionId, SITE, TODAY } from './data';

export function Variants({ variant }: { variant: string }) {
  const query = useAssignments(constructionId);
  const remove = useRemoveAssignment();
  const [search, setSearch] = useState('');
  const [all, setAll] = useState(false);
  const [adding, setAdding] = useState(false);
  const [focusedId, setFocusedId] = useState<string | null>(null);
  const [editing, setEditing] = useState<Assignment | null>(null);
  const [removing, setRemoving] = useState<Assignment | null>(null);
  useEffect(() => { if (adding && variant !== 'a') document.querySelector('.w12-composer')?.scrollIntoView({ block: 'start', behavior: 'instant' }); }, [adding, variant]);
  if (query.isError) return <Banner tone="error">Назначения не прочитались.</Banner>;
  if (!query.data) return <p>Читаем назначения…</p>;
  if (query.data.missing) return <Banner tone="warning">{query.data.missing}</Banner>;
  const page = query.data.page!;
  const { construction: site, items } = page;
  const { shown, hidden } = filterAssignments(items, all ? '' : 'current', TODAY);
  const rows = shown.filter(a => a.employeeName.toLowerCase().includes(search.toLowerCase()));
  const focus = rows.find(a => a.id === focusedId) ?? rows[0];
  const open = (a: Assignment) => { if (site.canAssign && a.canChange) setEditing(a); };
  const controls = (a: Assignment) => site.canAssign && <div className="w12-row-actions">
    {a.canChange && <Button variant="text" onClick={() => open(a)}>Изменить период</Button>}
    {a.canRemove && <Button variant="text" onClick={() => setRemoving(a)}>Снять</Button>}
  </div>;
  const add = site.canAssign && <Button variant="filled" icon={<Icon icon={Add} size={18} />} onClick={() => setAdding(true)}>Назначить на период</Button>;
  const section = (title: string, list: Assignment[]) => <section className="w12-group" key={title}>
    <div className="w12-section-heading"><h3>{title}</h3><span className="w12-note">{list.length}</span></div>
    <div className="w12-cards">{list.map(a => <article className="w12-person" key={a.id}>
      <div className="w12-person-heading"><span className="w12-avatar" aria-hidden>{a.employeeName.split(' ').slice(0, 2).map(n => n[0]).join('')}</span><div><h4>{a.employeeName}</h4><p className="w12-note">{a.kind === 'manager' ? 'Менеджер проекта' : 'Монтажник'} · Таб. № {a.personnelNumber}</p></div></div>
      <p className="w12-period">{periodLabel(a)}</p><StatusText tone={a.active ? 'success' : 'neutral'}>{phaseLabel(a, TODAY)}</StatusText>
      {controls(a)}{!a.canRemove && site.canAssign && <p className="w12-note">{a.removeReason}</p>}
    </article>)}</div>
  </section>;
  const columns: DataGridColumn<Assignment>[] = [
    { key: 'employee', label: 'Сотрудник', rowHeader: true, interactive: true, wrap: true, cell: a => <Button variant="text" multiline onClick={() => setFocusedId(a.id)} aria-pressed={a.id === focus?.id}>{a.employeeName}</Button> },
    { key: 'kind', label: 'Вид', cell: a => a.kind === 'manager' ? 'Менеджер' : 'Монтажник' },
    { key: 'phase', label: 'Состояние', cell: a => <StatusText tone={a.active ? 'success' : 'neutral'}>{phaseLabel(a, TODAY)}</StatusText> },
  ];
  return <>
    <div className="w12-heading"><div><p className="w12-eyebrow">Учёт работ / Эта стройка</p><h1>Назначения</h1><p className="w12-note">Сегодня на стройке — 22 ноября 2026</p></div>{items.length > 0 && add}</div>
    {!site.canAssign && <Banner tone="info">{site.reason}</Banner>}
    {items.length > 0 && <>
      <div className="w12-summary"><Summary value={items.filter(a => a.active && a.kind === 'installer').length} label="монтажника сейчас" /><Summary value={items.filter(a => a.active && a.kind === 'manager').length} label="менеджер на стройке" /><Summary value={items.filter(a => phaseOf(a, TODAY) === 'future').length} label="будущее назначение" /></div>
      <div className="w12-filters"><SearchInput value={search} onChange={setSearch} ariaLabel="Поиск назначений" placeholder="Найти сотрудника в бригаде" /><Select label="Показывать" density="dense" value={all ? 'all' : 'current'} onValueChange={v => setAll(v === 'all')}><SelectItem value="current">Действующие и будущие</SelectItem><SelectItem value="all">Все назначения</SelectItem></Select></div>
    </>}
    {items.length === 0 ? <EmptyState title="Состав бригады ещё не назначен" icon={<Icon icon={Group} size={40} />} description={site.canAssign ? 'Добавьте менеджера и монтажников на период работы.' : undefined} action={add} /> : rows.length === 0 ? <EmptyState icon={<Icon icon={Group} size={40} />} title="По вашему запросу назначений нет" /> : <>
      {variant === 'a' && <>{section('Менеджеры стройки', rows.filter(a => a.kind === 'manager'))}{section('Монтажники · сейчас', rows.filter(a => a.kind === 'installer' && a.active))}{section('Следующие периоды', rows.filter(a => phaseOf(a, TODAY) === 'future'))}{all && section('Завершённые', rows.filter(a => phaseOf(a, TODAY) === 'past'))}</>}
      {variant === 'b' && <div className="w12-split"><div className="w12-list"><DataGrid columns={columns} rows={rows} rowKey={a => a.id} /></div><aside className="w12-details" aria-label="Детали назначения">{focus && <><p className="w12-eyebrow">Выбранное назначение</p><h3>{focus.employeeName}</h3><p className="w12-note">{focus.kind === 'manager' ? 'Менеджер проекта' : 'Монтажник'} · № {focus.personnelNumber}</p><hr /><p className="w12-eyebrow">Период на стройке</p><p className="w12-period">{periodLabel(focus)}</p><StatusText tone={focus.active ? 'success' : 'neutral'}>{phaseLabel(focus, TODAY)}</StatusText>{controls(focus)}{!focus.canRemove && site.canAssign && <p className="w12-note">{focus.removeReason}</p>}</>}</aside></div>}
      {variant === 'c' && <div className="w12-timeline"><Timeline today={TODAY} nameLabel="Состав бригады" aria-label="Периоды назначений" rows={rows.map(a => ({ key: a.id, label: a.employeeName, sublabel: a.kind === 'manager' ? 'Менеджер' : 'Монтажник', from: a.dateFrom, to: a.dateTo, tone: phaseOf(a, TODAY) === 'past' ? 'muted' : a.kind === 'manager' ? 'secondary' : a.active ? 'primary' : 'outline', barLabel: barLabel(a), aria: `${a.employeeName}, ${periodLabel(a)}`, onOpen: site.canAssign && a.canChange ? () => open(a) : undefined, actions: site.canAssign && (a.canChange || a.canRemove) ? <RowActionsMenu ariaLabel={`Действия: ${a.employeeName}`} actions={[
        ...(a.canChange ? [{ key: 'edit', label: 'Изменить период', icon: Edit, onSelect: () => open(a) }] : []),
        ...(a.canRemove ? [{ key: 'remove', label: 'Снять назначение', icon: Delete, danger: true, onSelect: () => setRemoving(a) }] : []),
      ]} /> : undefined }))} /><p className="w12-note">Менеджеры — вторичный цвет · Действующие монтажники — основной · Будущие — контур</p></div>}
      {hidden > 0 && <p className="w12-note">Скрыто завершённых: {hidden}. Их покажет отбор «Все назначения».</p>}
    </>}
    {adding && (variant === 'a' ? <BulkAssignDialog constructionId={constructionId} siteName={SITE} today={TODAY} onClose={() => setAdding(false)} /> : <Composer stepped={variant === 'c'} onClose={() => setAdding(false)} />)}
    {editing && <AssignmentDialog assignment={editing} siteName={SITE} onClose={() => setEditing(null)} onReread={async () => (await query.refetch()).data?.page?.items.find(a => a.id === editing.id) ?? null} />}
    <ConfirmDialog open={!!removing} onOpenChange={v => { if (!v) setRemoving(null); }} title="Снять назначение?" confirmLabel="Снять назначение" errorTitle="Назначение не снято" description={`${removing?.employeeName} перестанет быть назначен на стройку. Введённые отчёты останутся.`} onConfirm={() => remove.mutateAsync({ id: removing!.id, version: removing!.version })} />
  </>;
}

function Summary({ value, label }: { value: number; label: ReactNode }) {
  return <div><strong>{value.toString().padStart(2, '0')}</strong><span>{label}</span></div>;
}
