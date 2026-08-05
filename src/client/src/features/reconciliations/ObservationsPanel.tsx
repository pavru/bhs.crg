import { useMemo, useState } from 'react';
import { Bot, Check, X, RotateCcw, FileText, Layers } from 'lucide-react';
import { Link } from 'react-router';
import { Button } from '@/shared/ui/Button';
import { Modal } from '@/shared/ui/Modal';
import { TextField } from '@/shared/ui/TextField';
import { EmptyState } from '@/shared/ui/EmptyState';
import { useToast } from '@/shared/ui/Toast';
import { useListConstructions } from '@/shared/api/constructions';
import {
  useObservations, useReviewObservation,
  SEVERITY_LABELS, OBSERVATION_STATUS_LABELS, isUnreviewed,
  type Observation, type ObservationStatus,
} from '@/shared/api/observations';

/**
 * Журнал замечаний внешнего анализа (issue #442).
 *
 * Ключевое требование к оформлению: замечание — УТВЕРЖДЕНИЕ АГЕНТА, а не результат проверки системы.
 * Происхождение видно всегда; выдать его за находку системы — худшее, что можно тут сделать.
 */

const SEVERITY_STYLE: Record<Observation['severity'], string> = {
  Error: 'bg-danger-subtle text-danger',
  Warning: 'bg-warning-subtle text-warning',
  Info: 'bg-muted text-fg3',
};

const STATUS_STYLE: Record<ObservationStatus, string> = {
  New: 'bg-brand-subtle text-brand',
  Confirmed: 'bg-muted text-fg2',
  Rejected: 'bg-muted text-fg4',
  // Отозвано агентом: не исчезает и не выглядит разобранным — закрывает всё равно человек.
  Retracted: 'bg-warning-subtle text-warning',
};

/**
 * Ссылки на первоисточник: без перехода проверять утверждение неудобно, а непроверенное — мнение.
 *
 * Документ открывается deep-link'ом внутрь комплекта, поэтому нужна ещё и стройка: она вычисляется по
 * области замечания. Не нашлась — показываем идентификатор текстом, но НЕ рисуем ссылку: битая ссылка
 * хуже её отсутствия.
 */
function References({ o, setPath }: { o: Observation; setPath: string | null }) {
  const docs = o.references.documentIds ?? [];
  const sourceId = o.references.sourceId;
  const rows = o.references.rows ?? [];
  if (docs.length === 0 && !sourceId && !o.references.note) return null;

  return (
    <div className="flex flex-wrap items-center gap-x-3 gap-y-1 mt-1.5 text-[11px]">
      {docs.map(id => setPath ? (
        <Link key={id} to={`${setPath}?doc=${id}`}
          className="inline-flex items-center gap-1 text-brand hover:underline">
          <FileText size={11} /> документ {id.slice(0, 8)}
        </Link>
      ) : (
        <span key={id} className="inline-flex items-center gap-1 text-fg4">
          <FileText size={11} /> документ {id.slice(0, 8)}
        </span>
      ))}
      {sourceId && (
        <Link to="/datasets" className="inline-flex items-center gap-1 text-brand hover:underline">
          <Layers size={11} /> источник {sourceId.slice(0, 8)}
          {rows.length > 0 && <span className="text-fg4">· строк {rows.length}</span>}
        </Link>
      )}
      {o.references.note && <span className="text-fg4">{o.references.note}</span>}
    </div>
  );
}

function ReviewDialog({ observation, setPath, onClose }: {
  observation: Observation; setPath: string | null; onClose: () => void;
}) {
  const [note, setNote] = useState(observation.reviewNote ?? '');
  const review = useReviewObservation();
  const toast = useToast();

  async function decide(status: ObservationStatus) {
    await review.mutateAsync({ id: observation.id, status, note: note.trim() || null });
    onClose();
    toast.success(status === 'Rejected'
      // Примечание к отклонению читает агент — иначе он сообщит то же самое в следующий раз.
      ? 'Отклонено. Причину увидит агент при следующем анализе'
      : status === 'Confirmed' ? 'Подтверждено' : 'Возвращено в работу');
  }

  return (
    <Modal open onOpenChange={o => { if (!o) onClose(); }} title="Разбор замечания" wide
      footer={
        <div className="flex items-center gap-2">
          {observation.status !== 'New' && (
            <Button disabled={review.isPending} onClick={() => decide('New')}>
              <RotateCcw size={14} /> Вернуть в работу
            </Button>
          )}
          <span className="flex-1" />
          <Button disabled={review.isPending} onClick={onClose}>Отмена</Button>
          <Button danger disabled={review.isPending} onClick={() => decide('Rejected')}>
            <X size={14} /> Отклонить
          </Button>
          <Button variant="filled" loading={review.isPending} onClick={() => decide('Confirmed')}>
            <Check size={14} /> Подтвердить
          </Button>
        </div>
      }>
      <div className="space-y-4">
        <div>
          <div className="text-sm font-medium text-fg1">{observation.title}</div>
          {observation.detail && (
            <p className="text-sm text-fg2 mt-1 whitespace-pre-wrap">{observation.detail}</p>
          )}
          <References o={observation} setPath={setPath} />
        </div>

        <TextField label="Примечание" value={note} onChange={e => setNote(e.target.value)}
          hint="При отклонении объясните, почему это не ошибка — это прочитает агент и не повторит замечание" />
      </div>
    </Modal>
  );
}

function ObservationRow({ o, setPath, onReview }: {
  o: Observation; setPath: string | null; onReview: (o: Observation) => void;
}) {
  const dim = !isUnreviewed(o);
  return (
    <div className={`px-4 py-3 border-b border-stroke group ${dim ? 'opacity-70' : ''}`}>
      <div className="flex items-start gap-2">
        <span className={`text-[11px] px-1.5 py-0.5 rounded-full shrink-0 mt-0.5 ${SEVERITY_STYLE[o.severity]}`}>
          {SEVERITY_LABELS[o.severity]}
        </span>
        <div className="min-w-0 flex-1">
          <div className="text-sm text-fg1">{o.title}</div>
          {o.detail && <p className="text-xs text-fg3 mt-0.5 line-clamp-3">{o.detail}</p>}
          <References o={o} setPath={setPath} />
          {o.reviewNote && (
            <p className="text-[11px] text-fg3 mt-1">
              {OBSERVATION_STATUS_LABELS[o.status]}: {o.reviewNote}
              {o.reviewedBy ? ` (${o.reviewedBy})` : ''}
            </p>
          )}
        </div>
        <span className={`text-[11px] px-1.5 py-0.5 rounded-full shrink-0 ${STATUS_STYLE[o.status]}`}>
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
      <div className="px-4 py-3 border-b border-stroke shrink-0">
        <h2 className="text-base font-semibold text-fg1 flex items-center gap-2">
          <Bot size={16} className="text-fg3" /> Замечания анализа
        </h2>
        {/* Происхождение — не мелкий шрифт ради приличия, а суть: это не результат проверки системы. */}
        <p className="text-xs text-fg3 mt-0.5">
          Утверждения внешнего ИИ-агента. Система их не проверяла — подтвердите или отклоните.
        </p>
      </div>

      {items.length > 0 && (
        <div className="flex items-center gap-2 px-4 py-2 border-b border-stroke text-xs shrink-0">
          <button type="button" onClick={() => setOnlyNew(v => !v)}
            className={`px-2 py-1 rounded-full transition-colors ${
              onlyNew ? 'bg-brand-subtle text-brand font-medium' : 'text-fg3 hover:bg-muted'}`}>
            Не разобрано: {unreviewed}
          </button>
          <span className="text-fg4">всего {items.length}</span>
        </div>
      )}

      <div className="flex-1 min-h-0 overflow-y-auto">
        {isLoading && <p className="px-4 py-3 text-sm text-fg4">Загрузка…</p>}
        {!isLoading && shown.length === 0 && (
          <div className="p-6">
            <EmptyState icon={<Bot size={32} />}
              title={items.length === 0 ? 'Замечаний нет' : 'Всё разобрано'}
              description={items.length === 0
                ? 'Здесь появятся находки внешнего агента, если попросить его записать их в журнал.'
                : undefined} />
          </div>
        )}
        {shown.map(o => <ObservationRow key={o.id} o={o} setPath={pathOf(o)} onReview={setReviewing} />)}
      </div>

      {reviewing && (
        <ReviewDialog observation={reviewing} setPath={pathOf(reviewing)}
          onClose={() => setReviewing(null)} />
      )}
    </div>
  );
}
