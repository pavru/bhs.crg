import { useState } from 'react';
import { Check, X, RotateCcw, FileText, Layers } from 'lucide-react';
import { Link } from 'react-router';
import { Button } from '@/shared/ui/Button';
import { Modal } from '@/shared/ui/Modal';
import { TextField } from '@/shared/ui/TextField';
import { useToast } from '@/shared/ui/Toast';
import {
  useReviewObservation, type Observation, type ObservationStatus,
} from '@/shared/api/observations';

/**
 * Разбор замечания внешнего анализа — ОДИН на все поверхности (issue #731).
 *
 * Замечание разбирают из двух мест: журнал агента в разделе «Сверка» и вкладка «Проблемы»
 * комплекта. Набор решений и формулировки обязаны совпадать до буквы — «Отклонено» здесь значит
 * то же самое, что и там, и причину в обоих случаях читает агент. Держать это двумя копиями
 * разметки значило бы обречь их разъехаться на первой же правке.
 *
 * Диалог, а не три кнопки на карточке: к решению полагается примечание (при отклонении оно и
 * есть суть — агент прочтёт его при следующем анализе), а поле ввода в каждой строке списка
 * превратило бы список в форму.
 */

/**
 * Ссылки на первоисточник: без перехода проверять утверждение неудобно, а непроверенное — мнение.
 *
 * Документ открывается deep-link'ом внутрь комплекта, поэтому нужен путь комплекта: он вычисляется
 * по области замечания. Не нашёлся — показываем идентификатор текстом, но НЕ рисуем ссылку: битая
 * ссылка хуже её отсутствия.
 *
 * `nameOf` задаёт вызывающая сторона, если знает имена документов: «документ 77de9193» человеку
 * ничего не говорит, а на вкладке комплекта состав под рукой.
 */
export function ObservationReferences({ o, setPath, nameOf }: {
  o: Observation;
  setPath: string | null;
  nameOf?: (documentId: string) => string | undefined;
}) {
  const docs = o.references.documentIds ?? [];
  const sourceId = o.references.sourceId;
  const rows = o.references.rows ?? [];
  if (docs.length === 0 && !sourceId && !o.references.note) return null;

  const label = (id: string) => nameOf?.(id) ?? `документ ${id.slice(0, 8)}`;

  return (
    <div className="flex flex-wrap items-center gap-x-3 gap-y-1 mt-1.5 text-[11px]">
      {docs.map(id => setPath ? (
        <Link key={id} to={`${setPath}?doc=${id}`}
          className="inline-flex items-center gap-1 text-brand hover:underline">
          <FileText size={11} /> {label(id)}
        </Link>
      ) : (
        <span key={id} className="inline-flex items-center gap-1 text-fg4">
          <FileText size={11} /> {label(id)}
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

export function ObservationReviewDialog({ observation, setPath, nameOf, onClose }: {
  observation: Observation;
  setPath: string | null;
  nameOf?: (documentId: string) => string | undefined;
  onClose: () => void;
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
          <ObservationReferences o={observation} setPath={setPath} nameOf={nameOf} />
        </div>

        <TextField label="Примечание" value={note} onChange={e => setNote(e.target.value)}
          hint="При отклонении объясните, почему это не ошибка — это прочитает агент и не повторит замечание" />
      </div>
    </Modal>
  );
}
