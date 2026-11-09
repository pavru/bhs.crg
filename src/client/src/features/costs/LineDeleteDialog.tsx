import { useRef } from 'react';
import { ConfirmDialog } from '@/shared/ui/ConfirmDialog';
import { Icon } from '@/shared/ui/Icon';
import { Warning } from '@/shared/ui/icons';
import { ruPlural } from '@/shared/utils/pluralize';
import type { InvoiceLineView } from '@/shared/api/invoices';
import { cascadeTargets, lineTotal } from './lineDeletion';
import { lineName } from './invoiceLines';

/** Строка, удаление которой ждёт ответа: её ключ в черновике и кому сказать исход — сетке, она ставит фокус (D2). */
export interface LineDeleteAsk {
  key: string;
  answer: (removed: boolean) => void;
}

/**
 * Вопрос перед удалением строки с сохранённой разноской (#1655, D1, вариант Б [COST-7] [COST-12]): какая строка,
 * куда и на сколько она разнесена и что после сохранения разноску не вернуть. Каскад на сервере верен —
 * окно его только называет.
 *
 * <p>Всё в окне — от сохранённой строки: удаляется записанное, и разноска посчитана от записанных количеств. Правка
 * черновика, ещё не сохранённая, в окне не видна — номер строки называет её однозначно.</p>
 *
 * <p>`onClose(true)` зовётся при ЗАКРЫТИИ окна после подтверждения, а не в `onConfirm`: строка уходит из таблицы тем
 * же рендером, что и окно, — иначе фокус, поставленный сеткой на соседнюю строку (D2), забрала бы ловушка фокуса
 * ещё открытого окна.</p>
 */
export function LineDeleteDialog({ saved, number, onClose }: {
  /** Сохранённая строка, о которой спрашивают; нет — окно закрыто. */
  saved: InvoiceLineView | null;
  /** Номер строки в таблице сейчас, а не на миг нажатия: строки могли перечитаться. */
  number: number;
  onClose: (confirmed: boolean) => void;
}) {
  const confirmed = useRef(false);
  const targets = saved ? cascadeTargets(saved) : [];
  return (
    <ConfirmDialog open={saved !== null}
      onOpenChange={open => {
        if (open) return;
        const yes = confirmed.current;
        confirmed.current = false;
        onClose(yes);
      }}
      title={`Удалить строку ${number}?`}
      confirmLabel={`Удалить строку ${number}`}
      onConfirm={() => { confirmed.current = true; }}
      description={saved && (
        <>
          <div className="flex flex-col pb-3">
            <span className="text-on-surface">{lineName(saved)}</span>
            <span className="text-body-small">{lineTotal(saved)}</span>
          </div>
          {targets.length > 0 && (
            <>
              <p className="text-title-small text-on-surface">
                Разнесена на {targets.length} {ruPlural(targets.length, 'цель', 'цели', 'целей')}
              </p>
              {/* Длинный перечень прокручивает тело окна — одна прокрутка, без вложенной (её колесо и Tab путали). */}
              <ul aria-label="Разноска строки">
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
