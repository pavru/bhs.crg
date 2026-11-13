import type { ReactNode } from 'react';
import type { InvoiceQueue } from '@/shared/api/invoices';
import type { InvoiceQueues } from '@/shared/api/invoiceQueues';
import type { ChipLead } from '@/shared/ui/Chip';
import { UploadFile } from '@/shared/ui/icons';
import { Select, SelectItem } from '@/shared/ui/Select';
import { parseQueue, queueLead, queueOptions } from './invoiceQueues';
import { Tooltip } from '@/shared/ui/Tooltip';

/** Значение пункта «Эта загрузка» — не очередь: наружу не уходит. */
const UPLOAD = 'upload';
const UPLOAD_TITLE = 'Показаны только счета этой загрузки. «Все счета» или очередь снимают этот отбор.';

/**
 * Очереди списка счетов (issue #1186) — одним чипом отбора с меню «Очередь счетов» (costs-invoice-filter-row,
 * issue #1910, [COST-6.2]): «Все счета» в покое, выбранная очередь — «Разобрать · 3812». Четыре чипа с
 * числами у заказчика вставали над лентой (колонка 320) столбиком в пять рядов.
 *
 * Выбор один: отбирает сервер, и пересечения очередей он не считает — «разобрать И удалённые» никто не
 * просил. Снимает отбор пункт «Все счета». Пункты и их числа — `queueOptions`; удалённые записи видны и на
 * закрытом чипе ведущим знаком (`queueLead`), иначе сигнал о поломке прятался бы в меню.
 *
 * Числа считает сервер, те же стоят над реестром. Тому, кто счёт править не может, пунктов «наведите
 * порядок» не показывают (числа у него и не спрашивают): число было бы упрёком без выхода.
 */
export function InvoiceQueueChips({ queue, upload = false, onChange, queues, parsing, failed, onRetry, sites }: {
  queue: InvoiceQueue | null;
  /**
   * Список урезан до одной загрузки («Показать только их»). Чип тогда называет её, «Эта загрузка», а не
   * «Все счета»: в покое он утверждал бы, что показано всё, а пункт «Все счета» — тот же, что выбран, —
   * Radix не сообщает, и вернуть общий список им было бы нельзя (ревью PR #1934). Выбор очереди или «Все
   * счета» отбор загрузки снимает, как прежде чип очереди.
   */
  upload?: boolean;
  onChange: (queue: InvoiceQueue | null) => void;
  /** Числа; `undefined` — не пришли или их не спрашивали. */
  queues: InvoiceQueues | undefined;
  /** Сколько счетов под «Разобрать»; `undefined` — не посчитано (идёт запрос, отказ, прежний сервер). */
  parsing: number | undefined;
  /** Числа не пришли: это не «ноль», и молча убрать пункт нельзя. */
  failed: boolean;
  onRetry: () => void;
  /** Отбор «Стройки» (#1432) — последним: под очередью его нет, и уходя, он не сдвигает чип очередей. */
  sites?: ReactNode;
}) {
  const options = queueOptions(queues, parsing, failed, queue);
  const chosen = options.find(o => o.value === queue);
  const lead = queueLead(queues);
  // Знак — свойства чипа как есть (`QueueLead` — это `ChipLead` с подсказкой); без знака — пусто.
  const mark: ChipLead = lead ?? {};

  return (
    <div className="flex flex-wrap items-center gap-x-1.5 gap-y-2 px-3 py-1.5 text-body-small" role="group" aria-label="Отбор списка счетов">
      {/* На закрытый чип из пункта уходит только подпись — число выбранной очереди чип несёт сам: «Разобрать · 3812».
          Подсказка — о выбранной очереди, а в покое — об удалённых записях, если они есть. */}
      <Select variant="chip" aria-label="Очередь счетов" emptyItem="Все счета"
        value={upload ? UPLOAD : queue ?? ''} onValueChange={v => { if (v !== UPLOAD) onChange(parseQueue(v)); }}
        {...mark} count={chosen?.count || undefined} title={chosen?.title ?? (upload ? UPLOAD_TITLE : lead?.title)}>
        {upload && <SelectItem value={UPLOAD} icon={UploadFile}>Эта загрузка</SelectItem>}
        {options.map(o => (
          <SelectItem key={o.value} value={o.value} icon={o.icon} iconTone={o.iconTone} count={o.count || undefined}>
            {o.label}
          </SelectItem>
        ))}
      </Select>
      {failed && (
        <Tooltip content="Сколько счетов не распознано, с удалёнными и с архивными записями — не посчитано. Это не «ноль». Нажмите, чтобы повторить">
          <button type="button" onClick={onRetry} className="text-on-surface-variant underline hover:text-on-surface">
            Отборы не посчитаны
          </button>
        </Tooltip>
      )}
      {sites}
    </div>
  );
}
