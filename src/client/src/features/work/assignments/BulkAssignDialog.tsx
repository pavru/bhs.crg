import { useState } from 'react';
import { useDebouncedValue } from '@/shared/hooks/useDebouncedValue';
import { Check } from '@/shared/ui/icons';
import { Icon } from '@/shared/ui/Icon';
import { DateRange } from '@/shared/ui/DateRange';
import { EmployeePicker } from '@/shared/ui/EmployeePicker';
import { Modal, ModalExit, ModalNote, ModalPrimary } from '@/shared/ui/Modal';
import { ModalBanner } from '@/shared/ui/ModalBanner';
import { Select, SelectItem } from '@/shared/ui/Select';
import { formatDate } from '@/shared/format/format';
import { apiError } from '@/shared/utils/apiError';
import {
  KIND_LABEL, assignmentRefusalOf, periodComplete, useAssignmentCandidates, useBulkAssign,
  type AssignmentKind, type AssignmentRefusal,
} from '@/shared/api/workAssignments';

/**
 * «Назначить на период» (change `work-12-assignments-ui`, макет Р2 А) — широкое окно: вид и период сверху, под ними
 * кандидаты сервера таблицей с флажками. Одно окно на любое число людей — отдельного «назначить одного» нет.
 *
 * Кандидатов и их годность считает сервер под вид и период: смена любого — новый запрос, отмеченные и всё ещё
 * годные остаются отмеченными, ставшие негодными — снимаются (их строка остаётся с причиной). Запись — целиком
 * или никак: при 422 окно не закрывается, отметки целы, у каждого названного — причина цветом ошибки.
 */
export function BulkAssignDialog({ constructionId, siteName, today, onClose }: {
  constructionId: string;
  siteName: string;
  /** «Сегодня» стройки — начало периода по умолчанию. */
  today: string | null;
  onClose: () => void;
}) {
  const [kind, setKind] = useState<AssignmentKind>('installer');
  const [from, setFrom] = useState(today ?? '');
  const [to, setTo] = useState('');
  const [q, setQ] = useState('');
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [refusal, setRefusal] = useState<AssignmentRefusal | null>(null);
  const assign = useBulkAssign();

  // Поиск — с задержкой: каждый вызов читает справочник (до двухсот записей) и пересечения.
  const search = useDebouncedValue(q, 300);
  const candidates = useAssignmentCandidates({ constructionId, kind, dateFrom: from, dateTo: to, q: search }, true);
  const complete = periodComplete(kind, from, to);
  const page = complete ? candidates.data?.page : undefined;

  // Отмечены только годные в последней выдаче: ставшего негодным под новый период из пачки убираем — флажок у
  // неактивной строки не ставится и не должен уйти на сервер. Смена вида или периода сбрасывает поиск
  // (`changeKind`, `changePeriod`), поэтому прореживание видит всех отмеченных, а не только найденных.
  const items = page?.items;
  const unfit = items?.filter(c => !c.eligible && selected.has(c.employeeId)) ?? [];
  if (unfit.length > 0 && !candidates.isPlaceholderData) {
    const next = new Set(selected);
    for (const c of unfit) next.delete(c.employeeId);
    setSelected(next);
  }

  const refused = Object.fromEntries(
    (refusal?.kind === 'refused' ? refusal.refused : []).map(r => [r.employeeId, r.reason]),
  );

  const save = async () => {
    setRefusal(null);
    try {
      await assign.mutateAsync({ constructionId, kind, dateFrom: from, dateTo: to || null, employeeIds: [...selected] });
      onClose();
    } catch (e) {
      setRefusal(assignmentRefusalOf(e));
    }
  };

  const changePeriod = (f: string, t: string) => { setFrom(f); setTo(t); setQ(''); setRefusal(null); };
  const changeKind = (v: string) => { setKind(v as AssignmentKind); setQ(''); setRefusal(null); };
  const count = selected.size;

  return (
    <Modal open onOpenChange={o => { if (!o) onClose(); }} title="Назначить на период" wide busy={assign.isPending}
      isDirty={count > 0}
      footer={(
        <>
          <ModalNote>Выбрано: {count}</ModalNote>
          <ModalExit />
          <ModalPrimary icon={<Icon icon={Check} size={18} />} loading={assign.isPending}
            disabled={count === 0 || !complete} onClick={save}>
            {count > 0 ? `Назначить · ${count}` : 'Назначить'}
          </ModalPrimary>
        </>
      )}>
      {refusal && <ModalBanner tone="error">{refusal.error}</ModalBanner>}
      <div className="space-y-4">
        <p className="text-body-medium text-on-surface-variant">
          {siteName}{refusal?.kind === 'refused' && complete
            ? ` · ${KIND_LABEL[kind]} · ${to ? `${formatDate(from)} — ${formatDate(to)}` : `с ${formatDate(from)}, без срока`}`
            : ''}
        </p>
        <div className="flex flex-wrap items-end gap-3">
          <Select label="Вид" value={kind} onValueChange={changeKind}
            containerClassName="w-48">
            <SelectItem value="installer">Монтажник</SelectItem>
            <SelectItem value="manager">Менеджер</SelectItem>
          </Select>
          <DateRange label="Период" from={from} to={to} onChange={changePeriod} />
        </div>
        <p className="text-body-small text-on-surface-variant">
          {kind === 'manager'
            ? 'Кандидаты подобраны под вид и период: оставьте «по» пустым — менеджер назначится без срока. Менеджеру нужна учётная запись.'
            : 'Кандидаты подобраны под вид и период: смените даты — список перечитается, отметки годных останутся.'}
        </p>
        {candidates.isError ? (
          <p role="alert" className="text-body-medium text-error">{apiError(candidates.error, 'Кандидаты не прочитались')}</p>
        ) : (
          <EmployeePicker candidates={items} selected={selected} onChange={next => { setSelected(next); setRefusal(r => (r?.kind === 'refused' ? r : null)); }}
            search={q} onSearch={setQ} refused={refused} more={page?.more} limit={page?.limit}
            missing={complete ? candidates.data?.missing ?? null : null}
            pending={complete && (candidates.isFetching || q !== search)} loading={complete && candidates.isPending}
            disabled={assign.isPending} />
        )}
      </div>
    </Modal>
  );
}
