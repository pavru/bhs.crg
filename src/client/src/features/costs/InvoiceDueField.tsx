import { Icon } from '@/shared/ui/Icon';
import { RestartAlt } from '@/shared/ui/icons';
import { useDueByRule, type InvoiceView } from '@/shared/api/invoices';
import { Button } from '@/shared/ui/Button';
import { DateField } from '@/shared/ui/DateField';
import { useSnackbar } from '@/shared/ui/Snackbar';
import { asInput, K } from './invoiceFields';
import { dueState, hints } from './invoiceDue';

/**
 * Поле «Оплатить до» (ТЗ COST-9.1; задача C4, issue #1080). Срок подставляет сервер правилом из
 * настройки модуля; человек может вписать свой — тогда поле помечено, а правило его не трогает. Вернуть
 * срок к правилу — кнопкой «По правилу» (решение владельца 14.10.2026): стереть поле значит «срок не
 * определён», а не «верни правило».
 */
export function InvoiceDueField({ view, edits, current, set, locked, hint, required, frame }: {
  view: InvoiceView; edits: Record<string, unknown>; current: unknown;
  set: (key: string, next: unknown) => void;
  locked: boolean; hint?: string; required: boolean; frame: string;
}) {
  const toast = useSnackbar();
  const byRule = useDueByRule();
  const { note, offerRule } = dueState(view, edits, locked);

  return (
    <div className={frame}>
      <DateField label="Оплатить до" hint={hints(hint, note)} required={required}
        value={asInput(current).slice(0, 10)} readOnly={locked}
        onChange={iso => set(K.dueDate, iso ? iso.slice(0, 10) : null)} />
      {offerRule && (
        <Button variant="text" className="mt-0.5 -ml-2" icon={<Icon icon={RestartAlt} size={18} />}
          loading={byRule.isPending}
          onClick={() => byRule.mutateAsync({ id: view.id, seen: view.version })
            .catch(e => toast.apiError(e, 'Срок не пересчитан'))}>
          По правилу
        </Button>
      )}
    </div>
  );
}
