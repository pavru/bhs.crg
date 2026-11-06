import { DocumentScanner } from '@/shared/ui/icons';
import { useSnackbar } from '@/shared/ui/Snackbar';
import type { RowAction } from '@/shared/ui/RowActionsMenu';
import { useStartRecognition, type InvoiceRecognition } from '@/shared/api/invoiceRecognition';

/**
 * «Распознать файл» — пункт «⋮» сводки (D-10.6). `null` — пункта нет: распознавание идёт или прошло.
 * Распознать нельзя — пункт неактивен и называет причину: у пустого счёта со сканом это единственное
 * объяснение, почему распознавания не будет (у счёта со строками причина была бы шумом — пункта нет).
 */
export function useRecognitionAction(invoiceId: string, recognition: InvoiceRecognition | undefined, explain: boolean): RowAction | null {
  const start = useStartRecognition();
  const toast = useSnackbar();
  if (recognition?.state !== 'none') return null;
  if (!recognition.canStart)
    return explain && recognition.whyNot
      ? { key: 'recognize', label: 'Распознать файл', icon: DocumentScanner, disabled: true, hint: `Не распознаётся: ${recognition.whyNot}` }
      : null;
  return {
    key: 'recognize', label: 'Распознать файл', icon: DocumentScanner, loading: start.isPending,
    onSelect: () => { start.mutateAsync(invoiceId).catch(e => toast.apiError(e, 'Распознавание не запущено')); },
  };
}
