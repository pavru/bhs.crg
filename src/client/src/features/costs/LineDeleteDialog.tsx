import { useRef } from 'react';
import { ConfirmDialog } from '@/shared/ui/ConfirmDialog';
import { Icon } from '@/shared/ui/Icon';
import { Warning } from '@/shared/ui/icons';
import { ruPlural } from '@/shared/utils/pluralize';
import type { InvoiceLineView } from '@/shared/api/invoices';
import { cascadeTargets, lineTotal } from './lineDeletion';
import type { LineDraft } from './invoiceLines';

/** Строка, удаление которой ждёт ответа: черновик (что человек видит) и её номер в таблице. */
export interface LineDeleteAsk {
  line: LineDraft;
  index: number;
  /** Куда вернуть фокус по отказу — корзина строки. У окна нет `Dialog.Trigger`, и сам Radix его никуда не вернёт. */
  returnTo: HTMLElement | null;
}

/**
 * Вопрос перед удалением строки с сохранённой разноской (#1655, D1, вариант Б [COST-7] [COST-12]): какая строка,
 * куда и на сколько она разнесена и что после сохранения разноску не вернуть. Каскад на сервере верен —
 * окно его только называет.
 *
 * <p>`onClose(true)` зовётся при ЗАКРЫТИИ окна после подтверждения, а не в `onConfirm`: строка уходит из таблицы тем
 * же рендером, что и окно, — иначе фокус, поставленный таблицей на соседнюю строку (D2), забрала бы ловушка фокуса
 * ещё открытого окна. Отказ (Отмена, Esc, мимо) — `onClose(false)` и фокус на корзину строки (D2): уже после
 * закрытия, когда ловушка фокуса окна снята.</p>
 */
export function LineDeleteDialog({ asking, saved, onClose }: {
  asking: LineDeleteAsk | null;
  saved: readonly InvoiceLineView[];
  onClose: (confirmed: boolean) => void;
}) {
  const confirmed = useRef(false);
  const number = asking ? asking.index + 1 : 0;
  const line = asking ? saved.find(s => s.id === asking.line.id) : undefined;
  const targets = line ? cascadeTargets(line) : [];
  const name = asking ? asking.line.nomenclatureName ?? (asking.line.supplierText.trim() || 'Без наименования') : '';
  return (
    <ConfirmDialog open={asking !== null}
      onOpenChange={open => {
        if (open) return;
        const yes = confirmed.current;
        confirmed.current = false;
        const back = asking?.returnTo;
        onClose(yes);
        if (!yes && back) setTimeout(() => { if (back.isConnected) back.focus(); });
      }}
      title={`Удалить строку ${number}?`}
      confirmLabel={`Удалить строку ${number}`}
      onConfirm={() => { confirmed.current = true; }}
      description={(
        <>
          <div className="flex flex-col pb-3">
            <span className="text-on-surface">{name}</span>
            {line && <span className="text-body-small">{lineTotal(line)}</span>}
          </div>
          {targets.length > 0 && (
            <>
              <p className="text-title-small text-on-surface">
                Разнесена на {targets.length} {ruPlural(targets.length, 'цель', 'цели', 'целей')}
              </p>
              {/* Длинный перечень прокручивается сам: итог над ним и предупреждение под ним остаются на виду. */}
              <ul aria-label="Разноска строки" className="max-h-64 overflow-y-auto">
                {targets.map((t, i) => (
                  <li key={i} className="flex justify-between gap-3 py-2 border-t border-outline-variant">
                    <span className="flex flex-col min-w-0">
                      <span className="text-on-surface">{t.target}</span>
                      {t.detail && <span className={`text-body-small ${t.troubled ? 'text-error' : ''}`}>{t.detail}</span>}
                    </span>
                    <span className="flex flex-col items-end shrink-0 tabular-nums">
                      <span className="text-on-surface">{t.share}</span>
                      {t.amount && <span className="text-body-small">{t.amount}</span>}
                    </span>
                  </li>
                ))}
              </ul>
            </>
          )}
          <p className="flex items-start gap-2 pt-3 border-t border-outline-variant text-body-small">
            <Icon icon={Warning} size={18} className="shrink-0 text-error" />
            <span>
              Разноска удалится вместе со строкой. После сохранения её не вернуть: строку придётся ввести и разнести
              заново.
            </span>
          </p>
        </>
      )} />
  );
}
