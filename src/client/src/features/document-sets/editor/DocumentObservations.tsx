import { useState } from 'react';
import { Icon } from '@/shared/ui/Icon';
import { SmartToy } from '@/shared/ui/icons';
import { Button } from '@/shared/ui/Button';
import { Modal } from '@/shared/ui/Modal';
import { EmptyState } from '@/shared/ui/EmptyState';
import {
  useObservations, SEVERITY_LABELS, OBSERVATION_STATUS_LABELS, isUnreviewed,
} from '@/shared/api/observations';
import { StatusBadge } from '@/shared/ui/StatusBadge';

/**
 * Замечания, упоминающие этот документ (issue #456).
 *
 * Заголовок — «упоминающие», а не «ошибки документа»: агент пишет `documentIds` как «документы,
 * которых утверждение касается», и назвать это ошибкой документа значило бы приписать ему вину,
 * которой в данных нет.
 *
 * Находок сверки здесь нет намеренно: связь находки с документом идёт через привязку источника и
 * слабее, чем выглядит — «расхождение в этом документе» было бы неправдой.
 */
export function DocumentObservationsChip({ setId, documentId }: {
  setId: string;
  documentId: string;
}) {
  const [open, setOpen] = useState(false);
  const { data: all = [] } = useObservations(setId);

  const mine = all.filter(o => (o.references.documentIds ?? []).includes(documentId));
  if (mine.length === 0) return null;

  const unreviewed = mine.filter(isUnreviewed).length;

  return (
    <>
      <Button variant="text" size="sm" icon={<Icon icon={SmartToy} size={18} />} onClick={() => setOpen(true)}
        className="shrink-0" title="Замечания внешнего анализа, упоминающие этот документ">
        <span className="hidden sm:inline">Замечания</span>
        {unreviewed > 0 && (
          <StatusBadge count tone="warning">{unreviewed}</StatusBadge>
        )}
      </Button>

      <Modal open={open} onOpenChange={setOpen} nothingToLose title="Замечания, упоминающие этот документ">
        <div className="space-y-3">
          {/* Происхождение рядом с данными: это утверждения, а не результат проверки системы. */}
          <p className="text-label-small text-on-surface-variant">
            Утверждения внешнего ИИ-агента. Система их не проверяла; разбирать — на странице комплекта.
          </p>

          {mine.length === 0 && <EmptyState icon={<Icon icon={SmartToy} size={40} />} title="Замечаний нет" />}

          {mine.map(o => (
            <div key={o.id} className={`px-3 py-2 border border-outline-variant rounded-md ${
              isUnreviewed(o) ? '' : 'opacity-60'}`}>
              <div className="flex items-start gap-2">
                <StatusBadge tone={o.severity === 'Error' ? 'warning' : 'neutral'} className="mt-0.5">
                  {SEVERITY_LABELS[o.severity]}
                </StatusBadge>
                <div className="min-w-0 flex-1">
                  <div className="text-body-medium text-on-surface">{o.title}</div>
                  {o.detail && <p className="text-body-small text-on-surface-variant mt-0.5 whitespace-pre-wrap">{o.detail}</p>}
                  {o.reviewNote && (
                    <p className="text-label-small text-on-surface-variant mt-1">
                      {OBSERVATION_STATUS_LABELS[o.status]}: {o.reviewNote}
                    </p>
                  )}
                </div>
                <span className="text-label-small text-on-surface-variant shrink-0">{OBSERVATION_STATUS_LABELS[o.status]}</span>
              </div>
            </div>
          ))}
        </div>
      </Modal>
    </>
  );
}
