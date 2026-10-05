import { Unlink } from 'lucide-react';
import type { InvoiceView } from '@/shared/api/invoices';
import { lostSummary } from './lostReferences';

/**
 * Сводка потерянных ссылок в шапке счёта (issue #1184).
 *
 * <p>Пометка стоит и на месте каждого значения, но место бывает за краем экрана: строка под
 * прокруткой, колонка «Разноска». Сводка в непрокручиваемой шапке говорит, что потери есть и где.</p>
 *
 * <p>Не ошибка и не запрет: счёт с потерянными ссылками читается и сохраняется, ссылки при этом не
 * стираются. Действия здесь не предложено нарочно — «восстановить запись» некуда вести; исправляют
 * потерю в самом поле, выбрав другую запись.</p>
 */
export function LostReferencesNote({ view, locked }: { view: InvoiceView; locked: boolean }) {
  const summary = lostSummary(view);
  if (!summary) return null;

  // Одно «не проверено» — не тревога: типа статей в установке нет у КАЖДОГО такого счёта разом, и
  // красная полоса на всех них кричала бы о том, чего никто не знает.
  const alarming = summary.count > 0 || summary.others.length > 0;

  return (
    <div role="note" className={`flex items-start gap-2 rounded-lg border px-3 py-2 text-xs ${alarming
      ? 'border-danger-border bg-danger-subtle text-danger' : 'border-stroke bg-surface2 text-fg2'}`}>
      <Unlink size={14} className="shrink-0 mt-0.5" />
      <div className="min-w-0 space-y-0.5">
        {summary.count > 0 && (
          <p>
            Удалённые записи справочников — в {summary.count}{' '}
            {summary.count === 1 ? 'месте' : 'местах'}: {summary.places}.{' '}
            {locked
              ? 'Счёт заперт, исправить это нельзя — в число потерянных ссылок он не входит.'
              : 'Счёт сохраняется и так; исправить — выбрать в поле другую запись.'}
          </p>
        )}
        {summary.others.length > 0 && (
          <p>Запись на месте, но не та: {summary.others.join('; ')}. Это не удалённые записи.</p>
        )}
        {summary.unchecked.length > 0 && (
          <p>Не проверено: {summary.unchecked.join('; ')}. Есть ли эти записи, сказать нельзя — справочник не прочитан.</p>
        )}
        <p className="text-fg3">Дополнительные поля типа счёта на удалённые записи не проверяются.</p>
      </div>
    </div>
  );
}
