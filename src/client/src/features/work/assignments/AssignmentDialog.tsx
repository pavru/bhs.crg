import { useState } from 'react';
import { Check } from '@/shared/ui/icons';
import { Icon } from '@/shared/ui/Icon';
import { DateRange } from '@/shared/ui/DateRange';
import { Modal, ModalExit, ModalPrimary } from '@/shared/ui/Modal';
import { ModalBanner } from '@/shared/ui/ModalBanner';
import {
  KIND_LABEL, assignmentRefusalOf, useChangeAssignment, type Assignment, type AssignmentRefusal,
} from '@/shared/api/workAssignments';

/**
 * «Изменить период» назначения (change `work-12-assignments-ui`, макет Р2 лист 0) — окно 512. Сотрудник, стройка и
 * вид правкой не меняются: другое назначение — снятие и заведение. У менеджера «по» можно оставить пустым — без
 * срока; у монтажника период закрыт.
 *
 * Отказы — словами сервера, окно не закрывается и набранное не теряет. 409 (назначение изменили под окном, период
 * пересекается с другим, не останется менеджера) предлагает перечитать: окно берёт свежую версию из перечитанного
 * списка, и следующее «Сохранить» отправит её. 403 и 422 — причина сервера.
 */
export function AssignmentDialog({ assignment, siteName, onReread, onClose }: {
  assignment: Assignment;
  siteName: string;
  /** Перечитать назначения стройки и вернуть это назначение; `null` — его тем временем сняли. */
  onReread: () => Promise<Assignment | null>;
  onClose: () => void;
}) {
  const change = useChangeAssignment();
  const [from, setFrom] = useState(assignment.dateFrom);
  const [to, setTo] = useState(assignment.dateTo ?? '');
  // Версия, с которой уйдёт правка: прочитанная при открытии, после «Перечитать» — свежая.
  const [version, setVersion] = useState(assignment.version);
  const [refusal, setRefusal] = useState<AssignmentRefusal | null>(null);
  const [rereading, setRereading] = useState(false);
  const [refreshed, setRefreshed] = useState(false);
  const [gone, setGone] = useState(false);

  const manager = assignment.kind === 'manager';
  const complete = !!from && (manager || !!to);
  const dirty = from !== assignment.dateFrom || to !== (assignment.dateTo ?? '');

  const save = async () => {
    setRefusal(null);
    setRefreshed(false);
    try {
      await change.mutateAsync({ id: assignment.id, version, dateFrom: from, dateTo: to || null });
      onClose();
    } catch (e) {
      const refused = assignmentRefusalOf(e);
      // 404 — назначение сняли под окном: сохранять нечего, «Сохранить» уходит, остаётся выход.
      if (refused.kind === 'gone') setGone(true);
      else setRefusal(refused);
    }
  };

  const reread = async () => {
    setRereading(true);
    try {
      const fresh = await onReread();
      if (fresh === null) setGone(true);
      else {
        setVersion(fresh.version);
        setRefusal(null);
        setRefreshed(true);
      }
    } catch (e) {
      setRefusal(assignmentRefusalOf(e));
    } finally {
      setRereading(false);
    }
  };

  return (
    <Modal open onOpenChange={o => { if (!o) onClose(); }} title="Изменить период" busy={change.isPending} isDirty={dirty}
      footer={(
        <>
          <ModalExit />
          {!gone && (
            <ModalPrimary icon={<Icon icon={Check} size={18} />} loading={change.isPending} disabled={!complete}
              onClick={save}>
              Сохранить
            </ModalPrimary>
          )}
        </>
      )}>
      {gone ? (
        <ModalBanner tone="warning">Назначение тем временем сняли — изменять нечего. Закройте окно.</ModalBanner>
      ) : refusal ? (
        <ModalBanner tone="error"
          actions={refusal.kind === 'conflict' ? [{ label: 'Перечитать', onClick: reread, loading: rereading }] : []}>
          {refusal.error}
        </ModalBanner>
      ) : refreshed && (
        <ModalBanner tone="info">Назначения перечитаны, набранный период на месте. Проверьте его и сохраните ещё раз.</ModalBanner>
      )}
      <div className="space-y-4">
        <p className="text-body-medium text-on-surface-variant">
          {assignment.employeeName} · {KIND_LABEL[assignment.kind]} · {siteName}
        </p>
        <DateRange label="Период" from={from} to={to} required={!manager}
          onChange={(f, t) => { setFrom(f); setTo(t); }} />
        {manager && (
          <p className="text-body-small text-on-surface-variant">Оставьте «по» пустым — менеджер назначится без срока.</p>
        )}
      </div>
    </Modal>
  );
}
