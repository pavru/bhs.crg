import { useState } from 'react';
import { Banner } from '@/shared/ui/Banner';
import { Modal, ModalExit, ModalPrimary } from '@/shared/ui/Modal';
import { TextAreaField } from '@/shared/ui/TextAreaField';
import { briefDate, shiftName, siteName, type Report, type Scenario } from './model';

export type Decision = 'accept' | 'return';

export function DecisionDialog({ decision, report, scenario, onClose, onDecide, onReload }: {
  decision: Decision;
  report: Report;
  scenario: Scenario;
  onClose: () => void;
  onDecide: (decision: Decision, comment: string) => void;
  onReload: () => void;
}) {
  const [comment, setComment] = useState('');
  const [failure, setFailure] = useState<'stale' | 'decided' | null>(null);
  const returning = decision === 'return';
  const label = returning ? 'Вернуть' : 'Принять';
  const close = failure === 'decided' ? onReload : onClose;

  function submit() {
    if (scenario === 'stale' || scenario === 'decided') {
      setFailure(scenario);
      return;
    }
    onDecide(decision, comment.trim());
  }

  return <Modal open onOpenChange={open => { if (!open) close(); }} title={`${label} отчёт`} isDirty={!!comment.trim()}
    footer={<>
      <ModalExit>{failure === 'decided' ? 'Закрыть' : 'Отмена'}</ModalExit>
      {failure === 'stale' ? <ModalPrimary onClick={onReload}>Перечитать отчёт</ModalPrimary>
        : !failure && <ModalPrimary onClick={submit} disabled={returning && !comment.trim()}>{label}</ModalPrimary>}
    </>}>
    <div className="space-y-4">
      <div>
        <p className="text-title-medium">{report.employee}</p>
        <p className="text-body-medium text-on-surface-variant">{siteName(report.site)} · {briefDate(report.date)} · {shiftName[report.shift]}</p>
      </div>
      {failure && <Banner tone="error">{failure === 'stale' ? 'Отчёт тем временем изменили. Перечитайте его перед решением.' : 'Отчёт уже принят.'}</Banner>}
      {!returning && report.acceptWouldMark && <Banner tone="warning">В отчёте появится пометка «ввёл и принял один человек».</Banner>}
      {returning && <TextAreaField label="Что исправить" required value={comment} onChange={event => setComment(event.target.value)}
        hint="Комментарий обязателен. Его увидит сотрудник." rows={3} />}
    </div>
  </Modal>;
}
