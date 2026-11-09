import { Icon } from '@/shared/ui/Icon';
import { RestartAlt } from '@/shared/ui/icons';
import { useDueByRule, type InvoiceView } from '@/shared/api/invoices';
import { Button } from '@/shared/ui/Button';
import { DateField } from '@/shared/ui/DateField';
import { useSnackbar } from '@/shared/ui/Snackbar';
import { asInput, K } from './invoiceFields';
import { dueState } from './invoiceDue';

/**
 * Поле «Оплатить до» (ТЗ COST-9.1; задача C4, issue #1080). Срок подставляет сервер правилом из
 * настройки модуля; человек может вписать свой — тогда поле помечено, а правило его не трогает. Вернуть
 * срок к правилу — кнопкой «По правилу» (решение владельца 14.10.2026): стереть поле значит «срок не
 * определён», а не «верни правило».
 */
export function InvoiceDueField({ view, edits, current, set, pick = set, locked, fromScan, required }: {
  view: InvoiceView; edits: Record<string, unknown>; current: unknown;
  set: (key: string, next: unknown) => void;
  /** Дата выбрана — ушли из поля, Enter, день в календаре; у черновика пишет (D-12.2). Каждая цифра — только `set`. */
  pick?: (key: string, next: unknown) => void;
  locked: boolean; fromScan: boolean; required: boolean;
}) {
  const toast = useSnackbar();
  const byRule = useDueByRule();
  const { note, offerRule } = dueState(view, edits, locked);

  return (
    <div>
      <DateField label="Оплатить до" hint={note ?? undefined} fromScan={fromScan} required={required}
        value={asInput(current).slice(0, 10)} readOnly={locked}
        onChange={iso => set(K.dueDate, iso ? iso.slice(0, 10) : null)}
        onCommit={iso => pick(K.dueDate, iso ? iso.slice(0, 10) : null)} />
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
