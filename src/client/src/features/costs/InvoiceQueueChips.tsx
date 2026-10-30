import { Chip } from '@/shared/ui/Chip';
import { Archive, Checklist, DocumentScanner, Warning, type IconDef } from '@/shared/ui/icons';
import type { ReactNode } from 'react';
import type { InvoiceQueue } from '@/shared/api/invoices';
import type { InvoiceQueues } from '@/shared/api/invoiceQueues';
import { QUEUE_LABEL, queueChip } from './invoiceQueues';
import { Tooltip } from '@/shared/ui/Tooltip';

/**
 * Очереди списка счетов чипами (issue #1186): «Разобрать», «Удалённые записи 3», «В архиве 5».
 *
 * Выбор один, повторное нажатие снимает: отбирает сервер, и пересечения очередей он не считает —
 * «разобрать И удалённые» никто не просил.
 *
 * Числа считает сервер, те же стоят над реестром. Тому, кто счёт править не может, чипов «наведите
 * порядок» не показывают (числа у него и не спрашивают): число было бы упрёком без выхода.
 */
export function InvoiceQueueChips({ queue, onChange, queues, parsing, failed, onRetry, sites }: {
  queue: InvoiceQueue | null;
  onChange: (queue: InvoiceQueue | null) => void;
  /** Числа; `undefined` — не пришли или их не спрашивали. */
  queues: InvoiceQueues | undefined;
  /** Сколько счетов под «Разобрать»; `undefined` — не посчитано (идёт запрос, отказ, прежний сервер). */
  parsing: number | undefined;
  /** Числа не пришли: это не «ноль», и молча убрать чип нельзя. */
  failed: boolean;
  onRetry: () => void;
  /** Отбор «Стройки» (#1432) — последним: под очередью его нет, и уходя, он не сдвигает чипы очередей. */
  sites?: ReactNode;
}) {
  const toggle = (next: InvoiceQueue) => onChange(queue === next ? null : next);
  const lost = queueChip('lost', queues, queue === 'lost');
  const archived = queueChip('archived', queues, queue === 'archived');

  return (
    <div className="flex flex-wrap items-center gap-1.5 px-3 py-1.5 text-body-small" role="group" aria-label="Отбор списка счетов">
      {/* «Разобрать» (ТЗ COST-6.2, issue #1292) — рабочая очередь: все недоведённые счета, то есть
          черновики. Отбирает и считает СЕРВЕР. Чип стоит всегда; числа нет, пока оно не пришло либо
          равно нулю, — «не посчитано» нулём не рисуем. */}
      <QueueChip active={queue === 'parsing'} icon={Checklist} label={QUEUE_LABEL.parsing}
        count={parsing ? String(parsing) : undefined}
        title={'Черновики — счета, не доведённые до «Разобран». Чего не хватает каждому, видно в его строке.'
          + (parsing == null ? ' Сколько их — не посчитано.' : ` Сейчас их: ${parsing}.`)}
        tone="quiet" onClick={() => toggle('parsing')}
        ariaLabel={parsing ? `${QUEUE_LABEL.parsing}, счетов: ${parsing}` : undefined} />
      {/* «Не распознано» (issue #1077): черновики со сканом, в которых ещё нет строк. Ноль не рисуем,
          нажатый чип остаётся — иначе отбор нечем снять. */}
      {/* Числа не пришли — чип остаётся, без числа: адрес чисел падает вместе с опросом ссылок ядра,
          к распознаванию не относящимся, а сам отбор при этом работает. */}
      {(queue === 'unrecognized' || failed || (queues?.unrecognized ?? 0) > 0) && (
        <QueueChip active={queue === 'unrecognized'} icon={DocumentScanner} label={QUEUE_LABEL.unrecognized}
          count={queues && queues.unrecognized > 0 ? String(queues.unrecognized) : undefined}
          title={'Черновики с файлом, в которых нет строк: файл не распознан, прочитан без строк или не распознавался.'
            + (queues ? '' : ' Сколько их — не посчитано.')}
          tone="doubt" onClick={() => toggle('unrecognized')}
          ariaLabel={`${QUEUE_LABEL.unrecognized}, счетов: ${queues?.unrecognized || 'нет'}`} />
      )}
      {lost && (
        <QueueChip active={queue === 'lost'} icon={Warning} label={QUEUE_LABEL.lost} count={lost.count}
          title={lost.title} tone={lost.doubt ? 'doubt' : 'danger'} onClick={() => toggle('lost')}
          ariaLabel={`${QUEUE_LABEL.lost}, счетов: ${lost.count || 'нет'}`} />
      )}
      {archived && (
        <QueueChip active={queue === 'archived'} icon={Archive} label={QUEUE_LABEL.archived} count={archived.count}
          title={archived.title} tone="quiet" onClick={() => toggle('archived')}
          ariaLabel={`${QUEUE_LABEL.archived}, счетов: ${archived.count || 'нет'}`} />
      )}
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

/** Чип очереди — общий `Chip` ([UI-42], #1416): нажатый в цвете бренда, у ненажатого тон по смыслу. */
function QueueChip({ active, icon, label, count, title, tone, onClick, ariaLabel }: {
  active: boolean;
  icon: IconDef;
  label: string;
  count?: string;
  title: string;
  /** Архив и «Разобрать» — тихо: счёт верен. Потеря — красным, сомнение — предупреждением. */
  tone: 'quiet' | 'danger' | 'doubt';
  onClick: () => void;
  ariaLabel?: string;
}) {
  return (
    <Tooltip content={title}>
      <Chip selected={active} icon={icon} count={count} aria-label={ariaLabel} onClick={onClick}
        tone={tone === 'danger' ? 'danger' : tone === 'doubt' ? 'warning' : 'neutral'}>
        {label}
      </Chip>
    </Tooltip>
  );
}
