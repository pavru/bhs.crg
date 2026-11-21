import { useState } from 'react';
import { Button } from '@/shared/ui/Button';
import { Banner } from '@/shared/ui/Banner';
import { DateRange } from '@/shared/ui/DateRange';
import { EmployeePicker } from '@/shared/ui/EmployeePicker';
import { Select, SelectItem } from '@/shared/ui/Select';
import { useAssignmentCandidates, useBulkAssign, assignmentRefusalOf, periodComplete, type AssignmentKind, type AssignmentRefusal } from '@/shared/api/workAssignments';
import { constructionId, TODAY } from './data';

export function Composer({ onClose, stepped = false }: { onClose: () => void; stepped?: boolean }) {
  const [kind, setKind] = useState<AssignmentKind>('installer');
  const [from, setFrom] = useState(TODAY);
  const [to, setTo] = useState('2026-11-30');
  const [search, setSearch] = useState('');
  const [selected, setSelected] = useState(new Set<string>());
  const [step, setStep] = useState(1);
  const [refusal, setRefusal] = useState<AssignmentRefusal | null>(null);
  const candidates = useAssignmentCandidates({ constructionId, kind, dateFrom: from, dateTo: to, q: search }, true);
  const save = useBulkAssign();
  const ready = periodComplete(kind, from, to);
  const items = candidates.data?.page?.items;
  const unfit = items?.filter(c => !c.eligible && selected.has(c.employeeId)) ?? [];
  if (unfit.length && !candidates.isPlaceholderData) {
    const next = new Set(selected); unfit.forEach(c => next.delete(c.employeeId)); setSelected(next);
  }
  const changePeriod = (f: string, t: string) => { setFrom(f); setTo(t); setSearch(''); setRefusal(null); };
  const submit = async () => {
    setRefusal(null);
    try { await save.mutateAsync({ constructionId, kind, dateFrom: from, dateTo: to || null, employeeIds: [...selected] }); onClose(); }
    catch (e) { setRefusal(assignmentRefusalOf(e)); }
  };
  return <section className="w12-composer" aria-label="Новое назначение">
    <div className="w12-section-heading"><div><p className="w12-eyebrow">{stepped ? `Шаг ${step} из 2` : 'Добавление в бригаду'}</p><h3>Назначить на период</h3></div><Button variant="text" disabled={save.isPending} onClick={onClose}>Закрыть</Button></div>
    {refusal && <Banner tone="error">{refusal.error}</Banner>}
    {(!stepped || step === 1) && <div className="w12-form">
      <Select label="Вид назначения" value={kind} disabled={save.isPending} onValueChange={v => { setKind(v as AssignmentKind); setSearch(''); setRefusal(null); }}>
        <SelectItem value="installer">Монтажник</SelectItem><SelectItem value="manager">Менеджер</SelectItem>
      </Select>
      <DateRange label="Период" from={from} to={to} onChange={changePeriod} disabled={save.isPending} />
      <p className="w12-note">{kind === 'manager' ? 'Для менеджера конец периода можно оставить пустым.' : 'Для монтажника укажите начало и конец периода.'}</p>
      {stepped && <Button variant="tonal" disabled={!ready} onClick={() => setStep(2)}>Далее: сотрудники</Button>}
    </div>}
    {(!stepped || step === 2) && <>
      {stepped && <div className="w12-section-heading"><p className="w12-note">{kind === 'manager' ? 'Менеджер' : 'Монтажник'} · {from} — {to || 'без срока'}</p><Button variant="text" disabled={save.isPending} onClick={() => setStep(1)}>Изменить период</Button></div>}
      <EmployeePicker candidates={ready ? items : undefined} selected={selected} onChange={setSelected} search={search} onSearch={setSearch}
        missing={candidates.data?.missing} loading={ready && candidates.isLoading} pending={candidates.isFetching} disabled={save.isPending}
        refused={Object.fromEntries((refusal?.kind === 'refused' ? refusal.refused : []).map(r => [r.employeeId, r.reason]))} />
      <div className="w12-composer-footer"><span className="w12-note" aria-live="polite">Выбрано: {selected.size}</span><Button variant="filled" loading={save.isPending} disabled={!selected.size || !ready || candidates.isFetching} onClick={submit}>Назначить · {selected.size}</Button></div>
    </>}
  </section>;
}
