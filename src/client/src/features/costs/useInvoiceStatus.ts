import { useCallback, useState } from 'react';
import type { InvoiceView } from '@/shared/api/invoices';
import { useStartRecognition, type InvoiceRecognition } from '@/shared/api/invoiceRecognition';
import { useGuardedNavigate } from '@/shared/ui/NavigationGuard';
import { useSnackbar } from '@/shared/ui/Snackbar';
import { statusOf, type InvoiceStatus, type StatusAction, type StatusInput } from './invoiceStatus';
import type { LinesReport } from './InvoiceLinesTable';
import type { ReadinessTarget } from './invoiceReadiness';

/**
 * Индикатор состояния счёта для формы (costs-invoice-screen-11, D-11.3): собирает отчёты частей (строки,
 * исправление), строит перечень (`statusOf`) и исполняет действия пунктов. Удаление сюда не входит — его
 * исполняет диалог удаления формы (`useStatusRun`).
 */
export interface StatusHandlers {
  view: InvoiceView;
  recognition: InvoiceRecognition | undefined;
  recognitionUnread: boolean;
  onRetryRecognition: () => void;
  /** Шапка набрана поверх прежнего вида, и «Перечитать» её сбрасывает. */
  headerStale: boolean;
  onRereadHeader: () => void;
  onClose: () => void;
  onOpenInvoice: (id: string) => void;
  onRetryOrganizations?: () => void;
  rereadFailed?: { error: string; retrying: boolean; onRetry: () => void };
  hiddenByFilter?: () => void;
  onRetryExtras: () => void;
  onConfirmMarks: () => void;
  reveal: (place: ReadinessTarget) => void;
  /** Остальное — как в `StatusInput.form`, без отчётов частей. */
  form: Omit<StatusInput['form'], 'stale' | 'matches' | 'rereadFailed' | 'hiddenByFilter'>;
}

export function useInvoiceStatus(h: StatusHandlers): {
  status: InvoiceStatus;
  run: (action: StatusAction) => void;
  busy: StatusAction['kind'][];
  onLinesReport: (report: LinesReport) => void;
  onCorrectionStale: (reread: (() => void) | null) => void;
} {
  const [lines, setLines] = useState<LinesReport | null>(null);
  const onLinesReport = useCallback((report: LinesReport) => setLines(report), []);
  const [correction, setCorrection] = useState<(() => void) | null>(null);
  const onCorrectionStale = useCallback((reread: (() => void) | null) => setCorrection(() => reread), []);
  const start = useStartRecognition();
  const toast = useSnackbar();
  const navigate = useGuardedNavigate();

  const status = statusOf({
    view: h.view, recognition: h.recognition, recognitionUnread: h.recognitionUnread,
    form: {
      ...h.form,
      stale: { header: h.headerStale, lines: lines?.stale ?? false, correction: correction !== null },
      matches: lines?.matches ?? null,
      rereadFailed: h.rereadFailed?.error ?? null,
      hiddenByFilter: h.hiddenByFilter !== undefined,
    },
  });

  function run(action: StatusAction) {
    switch (action.kind) {
      case 'goto': h.reveal(action.place); return;
      case 'open': h.onOpenInvoice(action.invoiceId); return;
      case 'close': h.onClose(); return;
      case 'reread':
        // Одно «Перечитать счёт» на все устаревшие части: шапку, строки, исправление.
        if (h.headerStale) h.onRereadHeader();
        if (lines?.stale) lines.acts.current.reread();
        correction?.();
        return;
      case 'retry-view': h.rereadFailed?.onRetry(); return;
      case 'retry-orgs': h.onRetryOrganizations?.(); return;
      case 'recognize':
        start.mutateAsync(h.view.id).catch(e => toast.apiError(e, 'Распознавание не запущено'));
        return;
      case 'retry-recognition': h.onRetryRecognition(); return;
      case 'confirm-marks': h.onConfirmMarks(); return;
      case 'apply-matches': lines?.acts.current.apply(); return;
      case 'retry-matches': lines?.acts.current.retry(); return;
      case 'retry-extras': h.onRetryExtras(); return;
      case 'show-all': h.hiddenByFilter?.(); return;
      case 'periods': navigate('/periods'); return;
      case 'delete': return; // исполняет диалог удаления формы (`useStatusRun`)
    }
  }

  const busy: StatusAction['kind'][] = [
    ...(start.isPending ? ['recognize' as const] : []),
    ...(h.rereadFailed?.retrying ? ['retry-view' as const] : []),
  ];
  return { status, run, busy, onLinesReport, onCorrectionStale };
}
