import { Archive, Check, ListChecks, ScanLine, TriangleAlert } from 'lucide-react';
import type { InvoiceQueue } from '@/shared/api/invoices';
import type { InvoiceQueues } from '@/shared/api/invoiceQueues';
import { QUEUE_LABEL, queueChip } from './invoiceQueues';

/**
 * Очереди списка счетов чипами (issue #1186): «Разобрать», «Удалённые записи 3», «В архиве 5».
 *
 * Выбор один, повторное нажатие снимает: отбирает сервер, и пересечения очередей он не считает —
 * «разобрать И удалённые» никто не просил.
 *
 * Числа считает сервер, те же стоят над реестром. Тому, кто счёт править не может, чипов «наведите
 * порядок» не показывают (числа у него и не спрашивают): число было бы упрёком без выхода.
 */
export function InvoiceQueueChips({ queue, onChange, queues, failed, onRetry }: {
  queue: InvoiceQueue | null;
  onChange: (queue: InvoiceQueue | null) => void;
  /** Числа; `undefined` — не пришли или их не спрашивали. */
  queues: InvoiceQueues | undefined;
  /** Числа не пришли: это не «ноль», и молча убрать чип нельзя. */
  failed: boolean;
  onRetry: () => void;
}) {
  const toggle = (next: InvoiceQueue) => onChange(queue === next ? null : next);
  const lost = queueChip('lost', queues, queue === 'lost');
  const archived = queueChip('archived', queues, queue === 'archived');

  return (
    <div className="flex flex-wrap items-center gap-1.5 px-3 py-1.5 text-xs" role="group" aria-label="Отбор списка счетов">
      {/* «Разобрать» (ТЗ COST-6.2) — рабочая очередь снабженца: счета, у которых строки ждут позиции
          номенклатуры. Отбирает СЕРВЕР: считать «ждут позиции» по загруженному списку можно, а вот
          утверждать по нему, что других таких счетов нет, — нельзя. */}
      <Chip active={queue === 'parsing'} icon={ListChecks} label={QUEUE_LABEL.parsing}
        title="Счета, у которых строки ждут позиции номенклатуры" tone="quiet" onClick={() => toggle('parsing')} />
      {/* «Не распознано» (issue #1077): черновики со сканом, в которых ещё нет строк. Ноль не рисуем,
          нажатый чип остаётся — иначе отбор нечем снять. */}
      {/* Числа не пришли — чип остаётся, без числа: адрес чисел падает вместе с опросом ссылок ядра,
          к распознаванию не относящимся, а сам отбор при этом работает. */}
      {(queue === 'unrecognized' || failed || (queues?.unrecognized ?? 0) > 0) && (
        <Chip active={queue === 'unrecognized'} icon={ScanLine} label={QUEUE_LABEL.unrecognized}
          count={queues && queues.unrecognized > 0 ? String(queues.unrecognized) : undefined}
          title={'Черновики с файлом, в которых нет строк: файл не распознан, прочитан без строк или не распознавался.'
            + (queues ? '' : ' Сколько их — не посчитано.')}
          tone="doubt" onClick={() => toggle('unrecognized')}
          ariaLabel={`${QUEUE_LABEL.unrecognized}, счетов: ${queues?.unrecognized || 'нет'}`} />
      )}
      {lost && (
        <Chip active={queue === 'lost'} icon={TriangleAlert} label={QUEUE_LABEL.lost} count={lost.count}
          title={lost.title} tone={lost.doubt ? 'doubt' : 'danger'} onClick={() => toggle('lost')}
          ariaLabel={`${QUEUE_LABEL.lost}, счетов: ${lost.count || 'нет'}`} />
      )}
      {archived && (
        <Chip active={queue === 'archived'} icon={Archive} label={QUEUE_LABEL.archived} count={archived.count}
          title={archived.title} tone="quiet" onClick={() => toggle('archived')}
          ariaLabel={`${QUEUE_LABEL.archived}, счетов: ${archived.count || 'нет'}`} />
      )}
      {failed && (
        <button type="button" onClick={onRetry} className="text-fg3 underline hover:text-fg1"
          title="Сколько счетов не распознано, с удалёнными и с архивными записями — не посчитано. Это не «ноль». Нажмите, чтобы повторить">
          Отборы не посчитаны
        </button>
      )}
    </div>
  );
}

function Chip({ active, icon: Icon, label, count, title, tone, onClick, ariaLabel }: {
  active: boolean;
  icon: typeof Check;
  label: string;
  count?: string;
  title: string;
  /** Архив и «Разобрать» — тихо: счёт верен. Потеря — красным, сомнение — предупреждением. */
  tone: 'quiet' | 'danger' | 'doubt';
  onClick: () => void;
  ariaLabel?: string;
}) {
  const look = active ? 'border-brand text-brand bg-brand-subtle'
    : tone === 'danger' ? 'border-danger text-danger hover:bg-danger-subtle'
    : tone === 'doubt' ? 'border-warning text-warning hover:bg-surface2'
    : 'border-stroke text-fg3 hover:text-fg1';
  const Shown = active ? Check : Icon;
  return (
    <button type="button" aria-pressed={active} aria-label={ariaLabel} title={title} onClick={onClick}
      className={`inline-flex items-center gap-1 rounded-full border px-2 py-0.5 bg-surface ${look}`}>
      <Shown size={12} aria-hidden />
      {label}
      {count && <span className="font-medium">{count}</span>}
    </button>
  );
}
