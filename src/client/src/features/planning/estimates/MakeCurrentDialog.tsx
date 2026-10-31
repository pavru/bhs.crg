import { useState } from 'react';
import {
  planRefusal, useCurrentRevisionPreview, useMakeCurrentRevision, useUnitNames, useWorkTypeNames,
  type CurrentRevisionPreview, type PlanRefusal,
} from '@/shared/api/planningEstimates';
import { Button } from '@/shared/ui/Button';
import { DataGrid, type DataGridColumn } from '@/shared/ui/DataGrid';
import { Modal, ModalExit } from '@/shared/ui/Modal';
import { StatusBadge } from '@/shared/ui/StatusBadge';
import { useSnackbar } from '@/shared/ui/Snackbar';
import { apiError } from '@/shared/utils/apiError';
import { formatCount, formatMoney, formatQuantity } from '@/shared/format/format';

/**
 * «Сделать действующей» (ТЗ PLAN-5.2, решение В-5, макет П1): окно с расхождениями плана «было → станет»,
 * которые считает сервер (D7 `plan-31`). Только изменившиеся виды работ и объёмы; деньги — при
 * `plan.estimate.cost`; есть несопоставленные строки — напоминание, что в план они не войдут.
 *
 * Смена называет версию сметы из ПРЕДПРОСМОТРА, который человек видел. Отказ 409 — смету тем временем
 * изменили: окно остаётся открытым на той же редакции (выбор не теряется), предпросмотр перечитывается, и
 * человек подтверждает уже по новым цифрам.
 */
export function MakeCurrentDialog({ estimateId, revision, onClose }: {
  estimateId: string;
  /** Предлагаемая редакция; `null` — окно закрыто. */
  revision: { id: string; number: number } | null;
  onClose: () => void;
}) {
  // Запрос смены — снаружи тела: пока он идёт, окно не закрывается (`busy`) — закрытое спрятало бы исход.
  const make = useMakeCurrentRevision();
  return (
    <Modal open={revision !== null} onOpenChange={open => { if (!open) onClose(); }} wide busy={make.isPending}
      title={revision ? `Сделать ред. ${revision.number} действующей?` : ''}>
      {revision && <Body estimateId={estimateId} revisionId={revision.id} make={make} onDone={onClose} />}
    </Modal>
  );
}

function Body({ estimateId, revisionId, make, onDone }: {
  estimateId: string;
  revisionId: string;
  make: ReturnType<typeof useMakeCurrentRevision>;
  onDone: () => void;
}) {
  const preview = useCurrentRevisionPreview(estimateId, revisionId);
  const toast = useSnackbar();
  const [refusal, setRefusal] = useState<PlanRefusal | null>(null);

  const submit = (seen: CurrentRevisionPreview) => {
    setRefusal(null);
    make.mutate({ estimateId, revisionId, version: seen.version }, {
      onSuccess: () => {
        toast.success(`Ред. ${seen.revisionNumber} — действующая: план стройки считается по ней`);
        onDone();
      },
      // Окно не закрывается: отказ назван над кнопкой, выбор тот же. Предпросмотр перечитывает сама смена —
      // его ключ под ключом сметы, а её отказ перечитывает смету.
      onError: e => setRefusal(planRefusal(e)),
    });
  };

  if (preview.isError) return <p role="alert" className="text-body-medium text-error">{apiError(preview.error, 'Расхождения не посчитались')}</p>;
  if (!preview.data) return <p className="text-body-medium text-on-surface-variant">Считаем, что изменится в плане…</p>;
  const data = preview.data;

  return (
    <div className="space-y-4">
      <p className="text-body-medium text-on-surface-variant">
        {data.currentRevisionNumber === null
          ? `План стройки будет считаться по ред. ${data.revisionNumber}.`
          : `План стройки будет считаться по ред. ${data.revisionNumber} вместо ред. ${data.currentRevisionNumber}.`}
      </p>

      <Changes preview={data} />

      {data.cost && (
        <p className="text-body-medium text-on-surface-variant">
          Себестоимость: {formatMoney(data.cost.was)} → <span className="font-medium text-on-surface">{formatMoney(data.cost.becomes)}</span>
        </p>
      )}

      {data.unmatched > 0 && (
        <p className="rounded-sm border border-outline-variant bg-warning-container px-3 py-2 text-body-medium text-on-warning-container">
          В ред. {data.revisionNumber} не сопоставлено строк: {formatCount(data.unmatched)} — в план они не войдут. Действующей её
          можно сделать и сейчас; сопоставленное позже войдёт в план само.
        </p>
      )}

      {refusal && <p role="alert" className="text-body-medium text-error">{refusal.text}</p>}

      <div className="flex justify-end gap-2 pt-1">
        <ModalExit />
        <Button variant="filled" disabled={make.isPending || preview.isFetching} onClick={() => submit(data)}>
          {make.isPending ? 'Меняем…' : 'Сделать действующей'}
        </Button>
      </div>
    </div>
  );
}

interface ChangeLine {
  key: string;
  workType: string;
  unit: string;
  was: number;
  becomes: number;
}

/** «Было → станет» по видам работ — только изменившееся (решение владельца, П1). */
function Changes({ preview }: { preview: CurrentRevisionPreview }) {
  const workTypes = useWorkTypeNames();
  const units = useUnitNames(preview.changes.flatMap(c => (c.unitId ? [c.unitId] : [])));
  if (preview.changes.length === 0)
    return <p className="text-body-medium text-on-surface-variant">Объёмы плана по видам работ не изменятся.</p>;

  const lines: ChangeLine[] = preview.changes.map(c => ({
    key: `${c.workTypeId}:${c.unitId ?? ''}`,
    workType: workTypes.data?.get(c.workTypeId) ?? '…',
    unit: c.unitId ? units.get(c.unitId) ?? '' : '',
    was: c.was,
    becomes: c.becomes,
  }));
  const was = preview.currentRevisionNumber === null ? 'Было' : `Было (ред. ${preview.currentRevisionNumber})`;
  const columns: DataGridColumn<ChangeLine>[] = [
    { key: 'workType', label: 'Вид работы', wrap: true, rowHeader: true },
    { key: 'unit', label: 'Ед.', width: 'w-16' },
    { key: 'was', label: was, align: 'right', width: 'w-32', cell: l => (l.was === 0 ? '—' : <span className="tabular-nums">{formatQuantity(l.was)}</span>) },
    {
      key: 'becomes', label: `Станет (ред. ${preview.revisionNumber})`, align: 'right', width: 'w-32',
      cell: l => (l.becomes === 0 ? '—' : <span className="tabular-nums">{formatQuantity(l.becomes)}</span>),
    },
    { key: 'delta', label: '', width: 'w-28', cell: l => <Delta was={l.was} becomes={l.becomes} /> },
  ];
  return <DataGrid columns={columns} rows={lines} rowKey={l => l.key} density="compact" />;
}

function Delta({ was, becomes }: { was: number; becomes: number }) {
  if (was === 0) return <StatusBadge tone="brand">новый вид</StatusBadge>;
  if (becomes === 0) return <StatusBadge tone="quiet">уходит</StatusBadge>;
  const d = becomes - was;
  return <span className="text-body-small text-on-surface-variant tabular-nums">{d > 0 ? '+' : '−'}{formatQuantity(Math.abs(d))}</span>;
}
