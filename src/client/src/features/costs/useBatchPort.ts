import { useQueryClient } from '@tanstack/react-query';
import { useFileKinds } from '@/shared/api/fileKinds';
import { refreshInvoiceLists, sendScan } from '@/shared/api/invoiceRecognition';
import { sameFileByFingerprints } from '@/shared/api/invoiceSameFile';
import { fingerprintOf } from '@/shared/utils/fileFingerprint';
import { markCreatedHere } from './invoiceDelete';
import type { BatchPort } from './scanBatch';

/**
 * Куда ходит пакет сканов — одно на страницу счетов и центр активности (costs-invoice-screen-14, D5): «Повторить»
 * и «Завести всё равно» из центра активности шлют файлы тем же путём, что и страница, — с отпечатком, вопросом
 * о повторе и отметкой «заведён в этой вкладке» (D-13.3).
 */
export function useBatchPort(owner: string): BatchPort {
  const qc = useQueryClient();
  const kinds = useFileKinds();
  return {
    owner, kinds: kinds.data,
    fingerprint: file => fingerprintOf(file),
    ask: sameFileByFingerprints,
    send: (file, options) => sendScan(qc, file, options).then(created => {
      markCreatedHere(created.invoice.id);
      return created.invoice;
    }),
    refresh: () => refreshInvoiceLists(qc),
  };
}
