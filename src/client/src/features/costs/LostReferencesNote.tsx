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

  return (
    <div role="note" className="flex items-start gap-2 rounded-lg border border-danger-border bg-danger-subtle
      px-3 py-2 text-xs text-danger">
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
        <p className="text-fg3">Дополнительные поля типа счёта на удалённые записи не проверяются.</p>
      </div>
    </div>
  );
}
