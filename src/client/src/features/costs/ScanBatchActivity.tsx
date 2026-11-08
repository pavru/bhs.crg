import { useId } from 'react';
import { useNavigate } from 'react-router';
import { useNavigationGuard } from '@/shared/ui/NavigationGuard';
import { INVOICE_RECORD } from '@/shared/ui/recordRoutes';
import { InvoiceScanBatchBar } from './InvoiceScanBatchBar';
import { acceptHeld, retryRejected, type ScanBatch } from './scanBatch';
import { useBatchPort } from './useBatchPort';

/**
 * Раздел «Загрузка счетов» в центре активности (costs-invoice-screen-14, D5). Списка счетов на экране может не
 * быть (1280, файл рядом) — тогда ход пакета с «Остановить» и его итог живут здесь, и итог сам не исчезает:
 * закрывает его крестик. Пакет живёт вне страницы (`scanBatch`), поэтому и раздел показывает тот же пакет на
 * любой странице. «Открыть» ведёт к счёту обычным переходом — под охраной ухода.
 *
 * <p>Своей кнопки в шапке нет («вторую кнопку для ещё одного потока событий не заводят», docs/UI.md): пакет —
 * клиентский источник рядом с серверными задачами одной кнопки активности.</p>
 */
export function ScanBatchActivity({ batch, owner }: { batch: ScanBatch; owner: string }) {
  const heading = useId();
  const port = useBatchPort(owner);
  const navigate = useNavigate();
  const guard = useNavigationGuard();
  const open = (id: string) => {
    const go = () => navigate(`${INVOICE_RECORD.screen}?${INVOICE_RECORD.param}=${id}`);
    if (!guard?.attempt(go)) go();
  };
  return (
    <section aria-labelledby={heading} data-testid="activity-scan-batch" className="shrink-0 border-b border-outline-variant pt-1">
      <h2 id={heading} className="px-5 pt-2 text-body-large font-medium text-on-surface">
        Загрузка счетов{batch.phase === 'done' ? ' · итог' : ''}
      </h2>
      <div className="max-h-72 overflow-y-auto">
        <InvoiceScanBatchBar bare batch={batch} only={false} found={null} alive={null} onOpen={open}
          onRetry={() => retryRejected(port)} onAccept={print => acceptHeld(print, port)} />
      </div>
    </section>
  );
}
