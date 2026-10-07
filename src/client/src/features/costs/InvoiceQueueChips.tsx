import { Archive, Check, ListChecks, TriangleAlert } from 'lucide-react';
import type { InvoiceQueue } from '@/shared/api/invoices';
import type { TableShortcut } from '@/shared/api/tables';
import { QUEUE_LABEL, queueChip } from './invoiceQueues';

/**
 * Очереди списка счетов чипами (issue #1186): «Разобрать», «Удалённые записи 3», «В архиве 5».
 *
 * Выбор один, повторное нажатие снимает: отбирает сервер, и пересечения очередей он не считает —
 * «разобрать И удалённые» никто не просил.
 *
 * Числа — готовые отборы таблицы счетов, те же, что над реестром. Тому, кто счёт править не может,
 * сервер их не предлагает, и чипов «наведите порядок» у него нет: число было бы упрёком без выхода.
 */
export function InvoiceQueueChips({ queue, onChange, counts, failed, onRetry, locked }: {
  queue: InvoiceQueue | null;
  onChange: (queue: InvoiceQueue | null) => void;
  /** Готовые отборы таблицы счетов; `undefined` — ещё не пришли. */
  counts: TableShortcut[] | undefined;
  /** Числа не пришли: это не «ноль», и молча убрать чип нельзя. */
  failed: boolean;
  onRetry: () => void;
  /** Счетов с удалёнными записями в закрытом периоде; `null` — не спрашивали. */
  locked: number | null;
}) {
  const toggle = (next: InvoiceQueue) => onChange(queue === next ? null : next);
  const of = (code: 'lost' | 'archived') => queueChip(
    code, counts?.find(s => s.code === code), queue === code, code === 'lost' ? locked : null);
  const lost = of('lost');
  const archived = of('archived');

  return (
    <div className="flex flex-wrap items-center gap-1.5 px-3 py-1.5 text-xs" role="group" aria-label="Отбор списка счетов">
      {/* «Разобрать» (ТЗ COST-6.2) — рабочая очередь снабженца: счета, у которых строки ждут позиции
          номенклатуры. Отбирает СЕРВЕР: считать «ждут позиции» по загруженному списку можно, а вот
          утверждать по нему, что других таких счетов нет, — нельзя. */}
      <Chip active={queue === 'parsing'} icon={ListChecks} label={QUEUE_LABEL.parsing}
        title="Счета, у которых строки ждут позиции номенклатуры" tone="quiet" onClick={() => toggle('parsing')} />
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
          title="Сколько счетов с удалёнными и архивными записями — не посчитано. Это не «ноль». Нажмите, чтобы повторить">
          {QUEUE_LABEL.lost} — не посчитано
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
