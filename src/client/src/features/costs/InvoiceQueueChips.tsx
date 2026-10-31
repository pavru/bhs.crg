import { Chip } from '@/shared/ui/Chip';
import { Archive, Checklist, DocumentScanner, Help, Warning } from '@/shared/ui/icons';
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
    <div className="flex flex-wrap items-center gap-x-1.5 gap-y-2 px-3 py-1.5 text-body-small" role="group" aria-label="Отбор списка счетов">
      {/* «Разобрать» (ТЗ COST-6.2, issue #1292) — рабочая очередь: все недоведённые счета, то есть
          черновики. Отбирает и считает СЕРВЕР. Чип стоит всегда; числа нет, пока оно не пришло либо
          равно нулю, — «не посчитано» нулём не рисуем. */}
      <Chip selected={queue === 'parsing'} icon={Checklist} count={parsing ? String(parsing) : undefined}
        title={'Черновики — счета, не доведённые до «Разобран». Чего не хватает каждому, видно в его строке.'
          + (parsing == null ? ' Сколько их — не посчитано.' : ` Сейчас их: ${parsing}.`)}
        onClick={() => toggle('parsing')}
        aria-label={parsing ? `${QUEUE_LABEL.parsing}, счетов: ${parsing}` : undefined}>
        {QUEUE_LABEL.parsing}
      </Chip>
      {/* «Не распознано» (issue #1077): черновики со сканом, в которых ещё нет строк. Ноль не рисуем,
          нажатый чип остаётся — иначе отбор нечем снять. */}
      {/* Числа не пришли — чип остаётся, без числа: адрес чисел падает вместе с опросом ссылок ядра,
          к распознаванию не относящимся, а сам отбор при этом работает. */}
      {(queue === 'unrecognized' || failed || (queues?.unrecognized ?? 0) > 0) && (
        <Chip selected={queue === 'unrecognized'} icon={DocumentScanner} count={queues && queues.unrecognized > 0 ? String(queues.unrecognized) : undefined}
          title={'Черновики с файлом, в которых нет строк: файл не распознан, прочитан без строк или не распознавался.'
            + (queues ? '' : ' Сколько их — не посчитано.')}
          iconTone="warning" onClick={() => toggle('unrecognized')}
          aria-label={`${QUEUE_LABEL.unrecognized}, счетов: ${queues?.unrecognized || 'нет'}`}>
          {QUEUE_LABEL.unrecognized}
        </Chip>
      )}
      {lost && (
        // «Возможно» и «точно потеряно» — разными знаками, а не цветом одного (WCAG 1.4.1, [UI-68]).
        <Chip selected={queue === 'lost'} icon={lost.doubt ? Help : Warning} iconTone={lost.doubt ? 'warning' : 'error'}
          count={lost.count} title={lost.title} onClick={() => toggle('lost')}
          aria-label={`${QUEUE_LABEL.lost}, счетов: ${lost.count || 'нет'}`}>
          {QUEUE_LABEL.lost}
        </Chip>
      )}
      {archived && (
        <Chip selected={queue === 'archived'} icon={Archive} count={archived.count}
          title={archived.title} onClick={() => toggle('archived')}
          aria-label={`${QUEUE_LABEL.archived}, счетов: ${archived.count || 'нет'}`}>
          {QUEUE_LABEL.archived}
        </Chip>
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
