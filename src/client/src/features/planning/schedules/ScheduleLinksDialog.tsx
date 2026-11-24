import { useState } from 'react';
import { useSchedule, useSchedules, useWriteScheduleLink, scheduleRefusal } from '@/shared/api/planningSchedules';
import type { Schedule, ScheduleLink, ScheduleWork } from '@/shared/api/planningScheduleTypes';
import { Banner } from '@/shared/ui/Banner';
import { Button } from '@/shared/ui/Button';
import { ConfirmDialog } from '@/shared/ui/ConfirmDialog';
import { DataGrid, type DataGridColumn } from '@/shared/ui/DataGrid';
import { Delete, Edit } from '@/shared/ui/icons';
import { Modal, ModalButtons, ModalExit, ModalPrimary } from '@/shared/ui/Modal';
import { NumberInput } from '@/shared/ui/NumberInput';
import { RowActionsMenu } from '@/shared/ui/RowActionsMenu';
import { Select, SelectItem } from '@/shared/ui/Select';
import { sectionName, type ScheduleSection } from './scheduleDisplay';

function signedLag(text: string) {
  if (!/^[+-]?\d+$/.test(text.trim())) return null;
  const parsed = Number(text);
  return parsed >= -2147483648 && parsed <= 2147483647 ? parsed : null;
}

export function ScheduleLinksDialog({ schedule, work, sections, canEdit, reload, onClose }: {
  schedule: Schedule; work: ScheduleWork; sections: ScheduleSection[]; canEdit: boolean; onClose: () => void;
  reload: () => Promise<{ isSuccess: boolean; data?: Schedule }>;
}) {
  const [graphId, setGraphId] = useState(schedule.id);
  const [predecessorId, setPredecessorId] = useState('');
  const [lag, setLag] = useState('0');
  const [editing, setEditing] = useState<ScheduleLink | null>(null);
  const [deleting, setDeleting] = useState<ScheduleLink | null>(null);
  const [reloading, setReloading] = useState(false);
  const [review, setReview] = useState('');
  const site = useSchedules(schedule.constructionId, canEdit);
  const other = useSchedule(schedule.constructionId, graphId, canEdit && graphId !== schedule.id);
  const write = useWriteScheduleLink(schedule.constructionId, schedule.id);
  const remove = useWriteScheduleLink(schedule.constructionId, schedule.id);
  const graphs = site.data?.some(item => item.id === schedule.id) ? site.data : [schedule, ...(site.data ?? [])];
  const chosen = graphId === schedule.id ? schedule : other.data;
  const candidates = (chosen?.works ?? []).filter(item => item.id !== work.id);
  const links = schedule.links.filter(link => link.successorId === work.id);
  const validLag = signedLag(lag);
  const valid = candidates.some(candidate => candidate.id === predecessorId) && validLag !== null;
  const predecessorChanged = graphId !== (editing?.predecessor?.scheduleId ?? schedule.id)
    || predecessorId !== (editing?.predecessorId ?? '');
  const lagChanged = validLag !== (editing?.lag ?? 0);
  const dirty = predecessorChanged || lagChanged;
  const refusal = write.error ? scheduleRefusal(write.error, 'Связь не сохранилась.') : null;
  const stale = !!editing && refusal?.kind === 'conflict';
  const busy = write.isPending || reloading || remove.isPending;
  const rowLocked = busy || dirty;
  const rowHint = busy ? 'Дождитесь завершения операции.' : dirty ? 'Сначала сохраните или сбросьте ввод.' : undefined;

  function workLabel(item: { name: string; sectionId: string | null }) {
    return `${item.name} · ${sectionName(item.sectionId, sections)}`;
  }

  function predecessorName(link: ScheduleLink) {
    const predecessor = link.predecessor ?? schedule.works.find(item => item.id === link.predecessorId);
    return predecessor ? workLabel(predecessor) : 'Работа другого графика';
  }

  function clear() {
    setEditing(null);
    setGraphId(schedule.id);
    setPredecessorId('');
    setLag('0');
    setReview('');
    write.reset();
  }

  function choose(link: ScheduleLink) {
    if (rowLocked) return;
    setEditing(link);
    setGraphId(link.predecessor?.scheduleId ?? schedule.id);
    setPredecessorId(link.predecessorId);
    setLag(String(link.lag));
    setReview('');
    write.reset();
  }

  async function save() {
    if (!canEdit || !dirty || !valid || validLag === null || busy || stale) return;
    const input = { predecessorId, successorId: work.id, kind: 'fs' as const, lag: validLag };
    try {
      await write.mutateAsync(editing ? {
        kind: 'edit', id: editing.id, version: editing.version, input,
      } : { kind: 'add', input });
      clear();
    } catch { /* Отказ показывается рядом с сохранённым вводом. */ }
  }

  async function reread() {
    if (!editing || busy) return;
    setReloading(true);
    try {
      const latest = await reload();
      const link = latest.data?.links.find(link => link.id === editing.id);
      if (!latest.isSuccess || link?.successorId !== work.id) {
        setReview(latest.isSuccess
          ? 'Связь уже удалена или перенесена к другой работе. Ваш ввод сохранён.'
          : 'График не прочитался. Ваш ввод сохранён.');
        return;
      }
      if (!predecessorChanged) {
        setGraphId(link.predecessor?.scheduleId ?? schedule.id);
        setPredecessorId(link.predecessorId);
      }
      if (!lagChanged) setLag(String(link.lag));
      setEditing(link);
      setReview('График перечитан. Проверьте введённую связь и сохраните её.');
      write.reset();
    } finally { setReloading(false); }
  }

  const columns: DataGridColumn<ScheduleLink>[] = [
    { key: 'predecessor', label: 'Работа', rowHeader: true, wrap: true, cell: predecessorName },
    { key: 'graph', label: 'График', wrap: true, cell: link =>
      link.predecessor?.scheduleName ?? (schedule.works.some(item => item.id === link.predecessorId) ? schedule.name : 'Другой график') },
    { key: 'lag', label: 'Задержка, раб. дн.', align: 'right' },
  ];
  if (canEdit) columns.push({
    key: 'actions', label: 'Действия', cell: link => <RowActionsMenu ariaLabel={`Действия связи с «${predecessorName(link)}»`}
      actions={[
        { key: 'edit', label: 'Изменить', icon: Edit, disabled: rowLocked, hint: rowHint, onSelect: () => choose(link) },
        { key: 'delete', label: 'Удалить', icon: Delete, danger: true, disabled: rowLocked, hint: rowHint,
          onSelect: () => { if (!rowLocked) setDeleting(link); } },
      ]} />,
  });

  return <>
    <Modal open wide title={`Предшественники · ${work.name}`} busy={busy}
      isDirty={dirty} onOpenChange={open => { if (!open) onClose(); }}
      footer={<ModalButtons>
        <ModalExit>Закрыть</ModalExit>
        {canEdit && <ModalPrimary disabled={!dirty || !valid || stale} loading={write.isPending}
          onClick={() => void save()}>Сохранить связь</ModalPrimary>}
      </ModalButtons>}>
      <div className="grid min-w-0 grid-cols-1 gap-4">
        <DataGrid rows={links} columns={columns} rowKey={link => link.id} density="compact" rowOpen={link => link.id === editing?.id} />
        {canEdit && <>
          <h3 className="text-title-medium">{editing ? 'Изменить связь' : 'Добавить предшественника'}</h3>
          <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
            <Select label="График предшественника" value={graphId} disabled={busy}
              onValueChange={id => { setGraphId(id); setPredecessorId(''); }}>
              {graphs.map(item => <SelectItem key={item.id} value={item.id}>{item.name}</SelectItem>)}
            </Select>
            <Select label="Предшественник" value={predecessorId} onValueChange={setPredecessorId}
              disabled={busy || candidates.length === 0}>
              {candidates.map(item => <SelectItem key={item.id} value={item.id} supporting={sectionName(item.sectionId, sections)}>
                {item.name}
              </SelectItem>)}
            </Select>
          </div>
          {site.isError && <Banner tone="error" actions={[{ label: 'Повторить', onClick: () => void site.refetch() }]}>
            Не удалось прочитать соседние графики.
          </Banner>}
          {graphId !== schedule.id && other.isError && <Banner tone="error"
            actions={[{ label: 'Повторить', onClick: () => void other.refetch() }]}>Не удалось прочитать работы графика.</Banner>}
          {graphId !== schedule.id && other.isPending && <p role="status" className="text-body-medium">Читаем работы графика…</p>}
          {chosen && candidates.length === 0 && <p className="text-body-medium text-on-surface-variant">
            В этом графике нет работ, доступных для выбора.
          </p>}
          <div className="flex flex-wrap items-center gap-2">
            <div className="w-48 max-w-full"><NumberInput label="Задержка" unit="раб. дн." value={lag}
              onChange={setLag} disabled={busy} /></div>
            {(editing || dirty) && <Button variant="text" disabled={busy} onClick={clear}>
              {editing ? 'Отменить правку' : 'Сбросить ввод'}
            </Button>}
          </div>
          {validLag === null && <p className="text-body-medium text-error">Введите целое число рабочих дней со знаком или без знака.</p>}
        </>}
        {refusal && <Banner tone="error" actions={stale && !reloading ? [
          { label: 'Перечитать график', onClick: () => void reread() },
        ] : undefined}><div>
          <p>{refusal.message}</p>
          {refusal.kind === 'cycle' && <ol className="list-decimal pl-5">
            {refusal.cycle.map((item, index) => <li key={`${item.id}/${index}`}>{workLabel(item)} · {item.scheduleName}</li>)}
          </ol>}
        </div></Banner>}
        {review && <p role="status" className="text-body-medium">{review}</p>}
      </div>
    </Modal>
    {canEdit && deleting && <ConfirmDialog open title={`Удалить связь с «${predecessorName(deleting)}»?`}
      confirmLabel="Удалить связь" onOpenChange={open => { if (!open) setDeleting(null); }}
      onConfirm={() => remove.mutateAsync({ kind: 'delete', id: deleting.id, version: deleting.version })} />}
  </>;
}
