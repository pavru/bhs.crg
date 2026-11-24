import { useState, type FormEvent } from 'react';
import { useWorkTypeChoices } from '@/shared/api/planningMatches';
import { scheduleRefusal, useWriteScheduleWork } from '@/shared/api/planningSchedules';
import { useLocale } from '@/shared/hooks/useLocale';
import { apiError } from '@/shared/utils/apiError';
import { Banner } from '@/shared/ui/Banner';
import { Button } from '@/shared/ui/Button';
import { DateField } from '@/shared/ui/DateField';
import { Modal, ModalButtons, ModalExit, ModalPrimary } from '@/shared/ui/Modal';
import { NumberInput } from '@/shared/ui/NumberInput';
import { Select, SelectItem } from '@/shared/ui/Select';
import { ScheduleEmployeeDialog } from './ScheduleEmployeeDialog';
import { assigneeText, type ScheduleSection } from './scheduleDisplay';
import { draftChanged, emptyDraft, workInput } from './workDraft';

export function ManualScheduleWorkDialog({ constructionId, id, sections, canReadEmployees, onClose }: {
  constructionId: string; id: string; sections: ScheduleSection[]; canReadEmployees: boolean; onClose: () => void;
}) {
  const [locale] = useLocale();
  const choices = useWorkTypeChoices();
  const write = useWriteScheduleWork(constructionId, id);
  const [typeId, setTypeId] = useState('');
  const [sectionId, setSectionId] = useState('whole-site');
  const [draft, setDraft] = useState(emptyDraft);
  const [badStart, setBadStart] = useState(false);
  const [badEnd, setBadEnd] = useState(false);
  const [picker, setPicker] = useState(false);
  const types = choices.data?.items ?? [];
  const type = types.find(item => item.id === typeId);
  const input = workInput(draft, locale);
  const ready = Boolean(type?.unitId && input && !badStart && !badEnd);
  const pending = write.isPending;

  async function save(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!ready || !input || pending) return;
    try {
      await write.mutateAsync({ kind: 'add', workTypeId: typeId, sectionId: sectionId === 'whole-site' ? null : sectionId, input });
      onClose();
    } catch { /* Отказ остаётся в открытой форме. */ }
  }

  return <>
    <Modal open title="Добавить работу" busy={pending}
      isDirty={!!typeId || sectionId !== 'whole-site' || draftChanged(emptyDraft, draft) || badStart || badEnd}
      onOpenChange={open => { if (!open) onClose(); }}
      footer={<ModalButtons><ModalExit /><ModalPrimary type="submit" form="manual-schedule-work"
        disabled={!ready} loading={pending}>Добавить</ModalPrimary></ModalButtons>}>
      <form id="manual-schedule-work" className="grid gap-4" onSubmit={save}>
        {choices.isPending && <p role="status" className="text-body-medium">Читаем классификатор…</p>}
        {choices.isError && <Banner tone="error" actions={[{ label: 'Повторить', onClick: () => void choices.refetch() }]}>
          {apiError(choices.error, 'Классификатор видов работ не прочитался.')}
        </Banner>}
        {choices.data?.missing && <Banner tone="warning">{choices.data.missing.error}</Banner>}
        {choices.isSuccess && !choices.data.missing && !types.length && <p className="text-body-medium">В классификаторе пока нет видов работ.</p>}
        {!!types.length && <Select label="Вид работы" value={typeId} onValueChange={setTypeId} disabled={pending}>
          {types.map(item => <SelectItem key={item.id} value={item.id} disabled={!item.unitId}
            supporting={item.unitId ? undefined : 'В классификаторе не задана единица'}>
            {item.code ? `${item.code} · ` : ''}{item.name}
          </SelectItem>)}
        </Select>}
        <Select label="Раздел" value={sectionId} onValueChange={setSectionId} disabled={pending}>
          <SelectItem value="whole-site">По стройке в целом</SelectItem>
          {sections.map(section => <SelectItem key={section.id} value={section.id}>{section.name}</SelectItem>)}
        </Select>
        <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
          <DateField label="Начало" value={draft.start} disabled={pending} onIncomplete={message => setBadStart(!!message)}
            onChange={start => setDraft(current => ({ ...current, start }))} />
          <DateField label="Окончание" value={draft.end} disabled={pending} onIncomplete={message => setBadEnd(!!message)}
            onChange={end => setDraft(current => ({ ...current, end }))} />
        </div>
        <NumberInput label="Объём" value={draft.volume} unit={type?.unitName} disabled={pending}
          onChange={volume => setDraft(current => ({ ...current, volume }))} />
        {canReadEmployees ? <div><Button variant="outlined" disabled={pending} onClick={() => setPicker(true)}>
          {draft.employeeIds.length ? assigneeText(draft.employeeIds, draft.assignees) : 'Выбрать исполнителей'}
        </Button></div> : <p className="text-body-medium text-on-surface-variant">
          Для выбора исполнителей нужно право чтения справочника сотрудников.
        </p>}
        {!input && <p className="text-body-medium text-error" role="alert">Проверьте объём и порядок дат.</p>}
        {write.error && <Banner tone="error">{scheduleRefusal(write.error, 'Работа не добавилась.').message}</Banner>}
      </form>
    </Modal>
    {picker && canReadEmployees && <ScheduleEmployeeDialog assignees={draft.assignees} ids={draft.employeeIds}
      onClose={() => setPicker(false)} onChoice={(employeeIds, assignees) => {
        setDraft(current => ({ ...current, employeeIds, assignees }));
        setPicker(false);
      }} />}
  </>;
}
