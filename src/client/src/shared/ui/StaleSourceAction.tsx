import { useState } from 'react';
import { AlertTriangle, RefreshCw, Loader2 } from 'lucide-react';
import { useRecognizeSource, isManualGroupingConflict, recognitionRefusal, type RecognitionRefusal } from '@/shared/api/datasets';
import { staleReasonText } from '@/shared/api/datasetHelpers';
import { ConfirmDialog } from '@/shared/ui/ConfirmDialog';
import { RecognitionBlockedDialog } from '@/features/datasets/RecognitionBlockedDialog';
import { ruCount } from '@/shared/utils/pluralize';
import type { DataSetSource } from '@/shared/api/types';

/**
 * Чип «устарело» и действие «Перераспознать» у привязки к устаревшему источнику (issue #815).
 *
 * Один компонент на обе точки потребления — форму документа и форму записи каталога: строки
 * привязок там свои, но признак и действие обязаны выглядеть и вести себя одинаково. Чип взят
 * дословно из списка источников в наборах данных, где живёт та же метка.
 *
 * Действие адресное — перераспознаётся ИМЕННО этот источник. Файловое «Распознать заново» из формы
 * предлагать нельзя: оно способно стереть ручную корректировку разбиения всего набора, а уход в
 * наборы данных потерял бы несохранённые правки формы (и адреса у источника уровня комплекта там
 * попросту нет).
 */
export function StaleSourceAction({ source }: { source: Pick<DataSetSource, 'id' | 'recognitionStale' | 'staleReason' | 'bindingCount'> | undefined }) {
  const recognize = useRecognizeSource();
  const [confirm, setConfirm] = useState(false);
  const [refusal, setRefusal] = useState<RecognitionRefusal | null>(null);
  const [groupingConflict, setGroupingConflict] = useState(false);

  if (!source?.recognitionStale) return null;

  function run(confirmOverwrite?: boolean) {
    recognize.mutate({ sourceId: source!.id, confirm: confirmOverwrite }, {
      onError: err => {
        // Ручная правка разбиения дороже автораспознавания — 409 спрашивает, а не глотается.
        if (isManualGroupingConflict(err)) { setGroupingConflict(true); return; }
        setRefusal(recognitionRefusal(err));
      },
    });
  }

  return (
    <>
      <span title={staleReasonText(source.staleReason)}
        className="inline-flex items-center gap-1 px-1.5 py-0.5 rounded text-[10px] font-medium bg-warning-subtle text-warning shrink-0">
        <AlertTriangle size={10} /> устарело
      </span>
      <button type="button" onClick={() => setConfirm(true)} disabled={recognize.isPending}
        className="p-1.5 rounded text-warning disabled:opacity-50 shrink-0"
        title="Перераспознать источник" aria-label="Перераспознать источник">
        {recognize.isPending ? <Loader2 size={13} className="animate-spin" /> : <RefreshCw size={13} />}
      </button>

      <ConfirmDialog open={confirm} onOpenChange={o => { if (!o) setConfirm(false); }}
        title="Перераспознать источник?"
        description={
          <div className="space-y-2">
            <p>{staleReasonText(source.staleReason)}.</p>
            <p>Займёт несколько минут.</p>
            {/* Число привязок — честность, а не украшение: человек жмёт кнопку в СВОЁМ документе и
                не ждёт, что тронет данные в чужих. Счёт приходит с источником (issue #417). */}
            {(source.bindingCount ?? 0) > 1 && (
              <p>Источник используется не только здесь — на него ссылаются{' '}
                <b>{ruCount(source.bindingCount!, 'привязка', 'привязки', 'привязок')}</b>, данные обновятся везде.</p>
            )}
          </div>
        }
        confirmLabel="Перераспознать"
        onConfirm={() => run()} />

      <ConfirmDialog open={groupingConflict} onOpenChange={o => { if (!o) setGroupingConflict(false); }}
        title="Разбиение было скорректировано вручную"
        description={<p>Повторное распознавание сотрёт ручные правки разбиения на документы. Продолжить?</p>}
        confirmLabel="Перераспознать"
        onConfirm={() => run(true)} />

      {refusal && (
        <RecognitionBlockedDialog message={refusal.message} configurable={refusal.configurable}
          onClose={() => setRefusal(null)} />
      )}
    </>
  );
}
