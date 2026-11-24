import { useState, type FormEvent } from 'react';
import { useConstructionEstimates } from '@/shared/api/planningEstimates';
import { scheduleRefusal, useCreateSchedule } from '@/shared/api/planningSchedules';
import { apiError } from '@/shared/utils/apiError';
import { newLocalId } from '@/shared/utils/localId';
import { Banner } from '@/shared/ui/Banner';
import { Modal, ModalButtons, ModalExit, ModalPrimary } from '@/shared/ui/Modal';
import { Select, SelectItem } from '@/shared/ui/Select';
import { TextField } from '@/shared/ui/TextField';

export function CreateScheduleDialog({ constructionId, fromEstimate, onClose, onCreated }: {
  constructionId: string; fromEstimate: boolean; onClose: () => void; onCreated: (id: string) => void;
}) {
  const [name, setName] = useState('');
  const [estimateId, setEstimateId] = useState('');
  const [revisionId, setRevisionId] = useState('');
  const [creationKey, setCreationKey] = useState(newLocalId);
  const estimates = useConstructionEstimates(fromEstimate ? constructionId : '');
  const save = useCreateSchedule();
  const choices = estimates.data?.items.filter(estimate => estimate.revisions.length) ?? [];
  const estimate = choices.find(item => item.id === estimateId);
  const ready = !!name.trim() && (!fromEstimate || Boolean(estimate?.revisions.some(r => r.id === revisionId)));

  function change(set: (value: string) => void, value: string) {
    set(value);
    setCreationKey(newLocalId());
    save.reset();
  }

  function chooseEstimate(id: string) {
    const chosen = choices.find(item => item.id === id);
    setRevisionId(chosen?.currentRevisionId ?? chosen?.revisions[0]?.id ?? '');
    change(setEstimateId, id);
  }

  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!ready || save.isPending) return;
    try {
      const schedule = await save.mutateAsync({
        constructionId, revisionId: fromEstimate ? revisionId : null, name: name.trim(), creationKey,
      });
      onCreated(schedule.id);
    } catch { /* Отказ остаётся в открытой форме. */ }
  }

  return <Modal open title={fromEstimate ? 'Построить из сметы' : 'Новый график'} busy={save.isPending}
    isDirty={!!name || !!estimateId} onOpenChange={open => { if (!open) onClose(); }}
    footer={<ModalButtons><ModalExit /><ModalPrimary type="submit" form="create-schedule" disabled={!ready} loading={save.isPending}>
      {fromEstimate ? 'Построить' : 'Создать'}
    </ModalPrimary></ModalButtons>}>
    <form id="create-schedule" onSubmit={submit} className="grid gap-4">
      <TextField required autoFocus label="Название графика" maxLength={250} value={name} disabled={save.isPending}
        onChange={event => change(setName, event.currentTarget.value)} />
      {fromEstimate && <>
        {estimates.isPending && <p role="status" className="text-body-medium">Читаем сметы стройки…</p>}
        {estimates.isError && <Banner tone="error" actions={[{ label: 'Повторить', onClick: () => void estimates.refetch() }]}>
          {apiError(estimates.error, 'Сметы стройки не прочитались.')}
        </Banner>}
        {estimates.isSuccess && choices.length === 0 && <p className="text-body-medium">У смет стройки пока нет редакций.</p>}
        {!!choices.length && <Select label="Смета" value={estimateId} onValueChange={chooseEstimate} disabled={save.isPending}>
          {choices.map(item => <SelectItem key={item.id} value={item.id}>{item.name}</SelectItem>)}
        </Select>}
        {estimate && <Select label="Редакция" value={revisionId} disabled={save.isPending}
          onValueChange={id => change(setRevisionId, id)}>
          {estimate.revisions.map(item => <SelectItem key={item.id} value={item.id}>
            Редакция {item.number}{item.current ? ' · действующая' : ''}
          </SelectItem>)}
        </Select>}
      </>}
      {save.error && <Banner tone="error">{scheduleRefusal(save.error, 'График создать не удалось.').message}</Banner>}
    </form>
  </Modal>;
}
