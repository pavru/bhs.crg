import { CircleCheck, ListTodo } from 'lucide-react';
import { goTo, type ReadinessItem } from './invoiceReadiness';
import { Tooltip } from '@/shared/ui/Tooltip';

/**
 * «До „Разобран“ осталось: плательщик · 3 строки без позиции · разноска» — строка у кнопки «Разобран»
 * (issue #1291).
 *
 * <p>Раньше чего не хватает до «Разобран», человек узнавал нажатием — и по одной причине за раз. Счёт
 * доводят разные люди: тот, кто продолжает чужой, не знал, на чём остановился предыдущий.</p>
 *
 * <p>Перечень — о СОХРАНЁННОМ счёте: его считает сервер. Пока в форме есть несохранённое, строка это
 * говорит — иначе «осталось: плательщик» стояло бы рядом с выбранным, но не сохранённым плательщиком
 * без объяснения.</p>
 */
export function InvoiceReadinessLine({ items, unsaved }: {
  items: ReadinessItem[];
  /** В форме есть несохранённые правки — шапки или строк. */
  unsaved: boolean;
}) {
  const stale = unsaved
    ? <span className="text-fg3"> Строка — о сохранённом счёте: сохраните правки, и она обновится.</span>
    : null;

  if (items.length === 0)
    return (
      <p className="flex items-start gap-1.5 text-xs text-success" data-testid="invoice-readiness">
        <CircleCheck size={13} className="shrink-0 mt-0.5" />
        <span>Всё готово к «Разобран».{stale}</span>
      </p>
    );

  return (
    <p className="flex items-start gap-1.5 text-xs text-on-surface-variant" data-testid="invoice-readiness">
      <ListTodo size={13} className="shrink-0 mt-0.5 text-fg3" />
      <span>
        До «Разобран» осталось:{' '}
        {items.map((item, index) => (
          <span key={item.id}>
            {index > 0 && <span className="text-fg4"> · </span>}
            {item.target
              ? (
                <button type="button" className="text-primary hover:underline"
                  onClick={() => { if (item.target) goTo(item.target); }}>
                  {item.label}
                </button>
              )
              : <Tooltip content={item.why}><span>{item.label}</span></Tooltip>}
          </span>
        ))}
        .{stale}
      </span>
    </p>
  );
}
