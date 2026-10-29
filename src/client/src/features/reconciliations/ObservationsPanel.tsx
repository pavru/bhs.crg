import { useMemo, useState } from 'react';
import { Icon } from '@/shared/ui/Icon';
import { SmartToy } from '@/shared/ui/icons';
import { Button } from '@/shared/ui/Button';
import { EmptyState } from '@/shared/ui/EmptyState';
import { useListConstructions } from '@/shared/api/constructions';
import {
  useObservations,
  SEVERITY_LABELS, OBSERVATION_STATUS_LABELS, isUnreviewed,
  type Observation, type ObservationStatus,
} from '@/shared/api/observations';
import { ObservationReviewDialog, ObservationReferences } from './ObservationReviewDialog';

/**
 * Журнал замечаний внешнего анализа (issue #442).
 *
 * Ключевое требование к оформлению: замечание — УТВЕРЖДЕНИЕ АГЕНТА, а не результат проверки системы.
 * Происхождение видно всегда; выдать его за находку системы — худшее, что можно тут сделать.
 */

const SEVERITY_STYLE: Record<Observation['severity'], string> = {
  Error: 'bg-error-container text-on-error-container',
  Warning: 'bg-warning-container text-on-warning-container',
  Info: 'bg-surface-container text-on-surface-variant',
};

const STATUS_STYLE: Record<ObservationStatus, string> = {
  New: 'bg-primary-container text-on-primary-container',
  Confirmed: 'bg-surface-container text-on-surface-variant',
  Rejected: 'bg-surface-container text-on-surface-variant',
  // Отозвано агентом: не исчезает и не выглядит разобранным — закрывает всё равно человек.
  Retracted: 'bg-warning-container text-on-warning-container',
};

function ObservationRow({ o, setPath, onReview }: {
  o: Observation; setPath: string | null; onReview: (o: Observation) => void;
}) {
  const dim = !isUnreviewed(o);
  return (
    <div className={`px-4 py-3 border-b border-outline-variant group ${dim ? 'opacity-70' : ''}`}>
      <div className="flex items-start gap-2">
        <span className={`text-caption px-1.5 py-0.5 rounded-full shrink-0 mt-0.5 ${SEVERITY_STYLE[o.severity]}`}>
          {SEVERITY_LABELS[o.severity]}
        </span>
        <div className="min-w-0 flex-1">
          <div className="text-sm text-on-surface">{o.title}</div>
          {o.detail && <p className="text-xs text-on-surface-variant mt-0.5 line-clamp-3">{o.detail}</p>}
          <ObservationReferences o={o} setPath={setPath} />
          {o.reviewNote && (
            <p className="text-caption text-on-surface-variant mt-1">
              {OBSERVATION_STATUS_LABELS[o.status]}: {o.reviewNote}
              {o.reviewedBy ? ` (${o.reviewedBy})` : ''}
            </p>
          )}
        </div>
        <span className={`text-caption px-1.5 py-0.5 rounded-full shrink-0 ${STATUS_STYLE[o.status]}`}>
          {OBSERVATION_STATUS_LABELS[o.status]}
        </span>
        <Button variant="text" size="sm" onClick={() => onReview(o)}
          className="opacity-0 group-hover:opacity-100 focus:opacity-100 shrink-0">
          Разобрать
        </Button>
      </div>
    </div>
  );
}

export function ObservationsPanel() {
  const { data: items = [], isLoading } = useObservations();
  const { data: constructions = [] } = useListConstructions();
  const [reviewing, setReviewing] = useState<Observation | null>(null);
  const [onlyNew, setOnlyNew] = useState(false);

  // Путь к комплекту по его идентификатору: замечание знает только область (комплект), а deep-link
  // документа собирается из стройки и комплекта.
  const setPaths = useMemo(() => {
    const map = new Map<string, string>();
    for (const c of constructions)
      for (const section of c.sections ?? [])
        for (const set of section.documentSets ?? [])
          map.set(set.id, `/document-sets/${c.id}/sets/${set.id}`);
    return map;
  }, [constructions]);
  const pathOf = (o: Observation) => (o.scopeId ? setPaths.get(o.scopeId) ?? null : null);

  const unreviewed = items.filter(isUnreviewed).length;
  const shown = onlyNew ? items.filter(isUnreviewed) : items;

  return (
    <div className="flex-1 min-w-0 flex flex-col">
      <div className="px-4 py-3 border-b border-outline-variant shrink-0">
        <h2 className="text-base font-semibold text-on-surface flex items-center gap-2">
          <Icon icon={SmartToy} size={18} className="text-on-surface-variant" /> Замечания анализа
        </h2>
        {/* Происхождение — не мелкий шрифт ради приличия, а суть: это не результат проверки системы. */}
        <p className="text-xs text-on-surface-variant mt-0.5">
          Утверждения внешнего ИИ-агента. Система их не проверяла — подтвердите или отклоните.
        </p>
      </div>

      {items.length > 0 && (
        <div className="flex items-center gap-2 px-4 py-2 border-b border-outline-variant text-xs shrink-0">
          <button type="button" onClick={() => setOnlyNew(v => !v)}
            className={`px-2 py-1 rounded-full transition-colors ${
              onlyNew ? 'bg-primary-container text-on-primary-container font-medium' : 'text-on-surface-variant hover:bg-surface-container'}`}>
            Не разобрано: {unreviewed}
          </button>
          <span className="text-on-surface-variant">всего {items.length}</span>
        </div>
      )}

      <div className="flex-1 min-h-0 overflow-y-auto">
        {isLoading && <p className="px-4 py-3 text-sm text-on-surface-variant">Загрузка…</p>}
        {!isLoading && shown.length === 0 && (
          <div className="p-6">
            <EmptyState icon={<Icon icon={SmartToy} size={40} />}
              title={items.length === 0 ? 'Замечаний нет' : 'Всё разобрано'}
              description={items.length === 0
                ? 'Здесь появятся находки внешнего агента, если попросить его записать их в журнал.'
                : undefined} />
          </div>
        )}
        {shown.map(o => <ObservationRow key={o.id} o={o} setPath={pathOf(o)} onReview={setReviewing} />)}
      </div>

      {reviewing && (
        <ObservationReviewDialog observation={reviewing} setPath={pathOf(reviewing)}
          onClose={() => setReviewing(null)} />
      )}
    </div>
  );
}
