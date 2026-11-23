import { useRef, useState, type ReactNode } from 'react';
import type { ReviewDecisionResult } from '@/shared/api/workReview';
import { Modal, ModalExit, ModalPrimary } from '@/shared/ui/Modal';
import { ModalBanner } from '@/shared/ui/ModalBanner';
import { apiError } from '@/shared/utils/apiError';
import { newLocalId } from '@/shared/utils/localId';

export interface ReviewDecisionProps {
  version: string;
  employeeName: string;
  shiftLabel: string;
  onClose: () => void;
  onDecided: (report: ReviewDecisionResult) => void;
  onReread: () => Promise<unknown>;
}

const statusMessage: Record<string, string> = {
  accepted: 'Отчёт уже принят.',
  returned: 'Отчёт уже возвращён на правку.',
  draft: 'Отчёт сейчас в черновике.',
  submitted: 'Отчёт уже подан.',
};

function refusalOf(error: unknown) {
  const { response } = (error ?? {}) as { response?: { status: number; data?: { code?: string; status?: string } } };
  const message = apiError(error, 'Не удалось сохранить решение.');
  if (response?.status === 409 && response.data?.code === 'report-status')
    return { kind: 'status', message: statusMessage[response.data.status ?? ''] ?? message } as const;
  if (response?.status === 409 && (response.data?.code === 'report-version' || message.startsWith('Отчёт тем временем изменили')))
    return { kind: 'version', message: 'Отчёт тем временем изменили. Перечитайте его перед решением.' } as const;
  return { kind: 'other', message } as const;
}

export function ReviewDecisionDialog({ version, employeeName, shiftLabel, onClose, onDecided, onReread,
  label, comment, warning, children, submit }: ReviewDecisionProps & {
    label: string;
    comment?: string;
    warning?: string;
    children?: ReactNode;
    submit: (version: string, changeId: string, comment: string) => Promise<ReviewDecisionResult>;
  }) {
  const [seenVersion] = useState(version);
  const [busy, setBusy] = useState(false);
  const [refusal, setRefusal] = useState<ReturnType<typeof refusalOf> | null>(null);
  const [refreshError, setRefreshError] = useState<string | null>(null);
  const attempt = useRef<{ comment?: string; id: string } | null>(null);
  const terminal = refusal?.kind === 'status' || refusal?.kind === 'version';

  async function save() {
    if (attempt.current === null || attempt.current.comment !== comment)
      attempt.current = { comment, id: newLocalId() };
    setBusy(true);
    setRefusal(null);
    try {
      const result = await submit(seenVersion, attempt.current.id, comment ?? '');
      onDecided(result);
      onClose();
    } catch (error) {
      setRefusal(refusalOf(error));
    } finally {
      setBusy(false);
    }
  }

  async function reread() {
    setBusy(true);
    setRefreshError(null);
    try {
      await onReread();
      onClose();
    } catch (error) {
      setRefreshError(apiError(error, 'Не удалось перечитать отчёт.'));
    } finally {
      setBusy(false);
    }
  }

  return <Modal open title={`${label} отчёт`} busy={busy} isDirty={!terminal && !!comment}
    onOpenChange={open => { if (!open) { if (refusal?.kind === 'status') void reread(); else onClose(); } }}
    footer={<>
      <ModalExit>{refusal?.kind === 'status' ? 'Закрыть' : 'Отмена'}</ModalExit>
      {refusal?.kind === 'version' && <ModalPrimary onClick={reread}>Перечитать отчёт</ModalPrimary>}
      {!terminal && <ModalPrimary onClick={save} disabled={comment === ''}>{label}</ModalPrimary>}
    </>}>
    {refusal ? <ModalBanner tone="error">{refusal.message}{refreshError && <> {refreshError}</>}</ModalBanner>
      : warning && <ModalBanner tone="warning">{warning}</ModalBanner>}
    <div className="space-y-4">
      <div>
        <p className="text-title-medium">{employeeName}</p>
        <p className="text-body-medium text-on-surface-variant">{shiftLabel}</p>
      </div>
      {children}
    </div>
  </Modal>;
}
