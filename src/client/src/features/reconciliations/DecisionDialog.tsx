import { useState } from 'react';
import { Modal } from '@/shared/ui/Modal';
import { Button } from '@/shared/ui/Button';
import { TextField } from '@/shared/ui/TextField';
import { Select, SelectItem } from '@/shared/ui/Select';
import { DECISION_LABELS, STATUS_LABELS, type DecisionKind, type Finding } from '@/shared/api/reconciliations';

/**
 * Разбор находки. Решение адресуется КЛЮЧОМ позиции, а не идентификатором находки, поэтому переживает
 * следующий прогон — иначе журнал терял бы память о разобранном при каждом пересчёте (issue #414).
 */
export function DecisionDialog({ finding, onClose, onSave, onRemove }: {
  finding: Finding;
  onClose: () => void;
  onSave: (kind: DecisionKind, note: string | null) => Promise<void>;
  /** Задан, только если решение уже есть. */
  onRemove?: () => Promise<void>;
}) {
  const [kind, setKind] = useState<DecisionKind>(finding.decision?.kind ?? 'Accepted');
  const [note, setNote] = useState(finding.decision?.note ?? '');
  const [busy, setBusy] = useState(false);

  async function run(action: () => Promise<void>) {
    setBusy(true);
    try { await action(); } finally { setBusy(false); }
  }

  return (
    <Modal open onOpenChange={o => { if (!o) onClose(); }} title="Разбор находки"
      footer={
        <div className="flex items-center gap-2">
          {onRemove && (
            <Button danger disabled={busy} onClick={() => run(onRemove)}>Снять решение</Button>
          )}
          <span className="flex-1" />
          <Button disabled={busy} onClick={onClose}>Отмена</Button>
          <Button variant="filled" loading={busy}
            onClick={() => run(() => onSave(kind, note.trim() || null))}>
            Сохранить
          </Button>
        </div>
      }>
      <div className="space-y-4">
        <div className="rounded-lg bg-muted px-3 py-2">
          <div className="text-sm font-medium text-fg1">{finding.label}</div>
          <div className="text-xs text-fg3 mt-0.5">
            {STATUS_LABELS[finding.status]} · слева {finding.leftValue ?? '—'} · справа {finding.rightValue ?? '—'}
          </div>
        </div>

        <div>
          <label className="block text-xs text-fg3 mb-1">Решение</label>
          <Select value={kind} onValueChange={v => setKind(v as DecisionKind)} aria-label="Решение">
            <SelectItem value="Accepted">{DECISION_LABELS.Accepted}</SelectItem>
            <SelectItem value="Suppressed">{DECISION_LABELS.Suppressed}</SelectItem>
          </Select>
          <p className="text-[11px] text-fg4 mt-1">
            «Признано нормой» — расхождение реально и объяснимо (давальческое оборудование, учтено в
            другом разделе). «Исключено» — позиция к этой сверке неприменима.
          </p>
        </div>

        <TextField label="Примечание" value={note} onChange={e => setNote(e.target.value)}
          hint="Почему это не ошибка" />

        <p className="text-[11px] text-fg4">
          Решение сохраняется за позицией, а не за прогоном, и останется после следующего пересчёта.
        </p>
      </div>
    </Modal>
  );
}
