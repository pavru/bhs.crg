import { useState } from 'react';
import type { CrewGroup } from '@/shared/api/work';
import { approveCrewGroup } from '@/shared/api/workCrew';
import { formatLocale, formatNumber } from '@/shared/format/format';
import { Modal, ModalExit, ModalPrimary } from '@/shared/ui/Modal';
import { ModalBanner } from '@/shared/ui/ModalBanner';
import { Select, SelectItem } from '@/shared/ui/Select';
import { TextField } from '@/shared/ui/TextField';
import { apiError, isConflict } from '@/shared/utils/apiError';
import { parseNumber } from '@/shared/utils/parseNumber';

const CUSTOM = 'custom';

export function CrewApprovalDialog({ group, work, shift, units, onClose, onApproved, onReread }: {
  group: CrewGroup;
  work: string;
  shift: string;
  units: Record<string, string>;
  onClose: () => void;
  onApproved: (result: CrewGroup) => void;
  onReread: () => Promise<unknown>;
}) {
  const [choice, setChoice] = useState('');
  const [custom, setCustom] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [conflict, setConflict] = useState(false);
  const unitIds = [...new Set(group.lines.map(line => line.unitId))];
  const unit = units[unitIds[0]] ?? '';
  const mixed = unitIds.length > 1;
  const total = choice === CUSTOM ? parseNumber(custom, formatLocale()) : group.lines.find(line => line.id === choice)?.volume;
  const valid = total != null && total >= 0;

  async function approve() {
    if (busy || conflict || mixed || total == null || total < 0) return;
    setBusy(true);
    setError(null);
    try {
      onApproved(await approveCrewGroup(group.id, group.version, total));
      onClose();
    } catch (failure) {
      setConflict(isConflict(failure));
      setError(apiError(failure, 'Не удалось утвердить итог.'));
    } finally {
      setBusy(false);
    }
  }

  async function reread() {
    setBusy(true);
    try {
      await onReread();
      onClose();
    } catch (failure) {
      setError(apiError(failure, 'Не удалось перечитать группу.'));
    } finally {
      setBusy(false);
    }
  }

  return <Modal open title="Утвердить итог бригады" busy={busy}
    onOpenChange={open => { if (!open) onClose(); }}
    footer={<>
      <ModalExit>Отмена</ModalExit>
      {conflict ? <ModalPrimary onClick={reread}>Перечитать группу</ModalPrimary>
        : <ModalPrimary onClick={approve} disabled={!valid || mixed}>Утвердить итог</ModalPrimary>}
    </>}>
    {error && <ModalBanner tone="error">{error}</ModalBanner>}
    <div className="space-y-4">
      <div>
        <p className="text-title-medium">{work}</p>
        <p className="text-body-medium text-on-surface-variant">{shift}</p>
      </div>
      {mixed ? <p className="text-body-medium">У строк разные единицы измерения. Приведите объёмы к одной единице в отчётах.</p>
        : <>
          <Select label="Источник итогового объёма" value={choice} placeholder="Выберите объём"
            onValueChange={setChoice} disabled={busy || conflict}>
            {group.lines.map(line => <SelectItem key={line.id} value={line.id}>
              {formatNumber(line.volume, 4)}{unit && ` ${unit}`} · {line.fullName}
            </SelectItem>)}
            <SelectItem value={CUSTOM}>Указать свой объём</SelectItem>
          </Select>
          {choice === CUSTOM && <TextField label={unit ? `Свой объём, ${unit}` : 'Свой объём'} inputMode="decimal"
            value={custom} disabled={busy || conflict} onChange={event => setCustom(event.target.value)}
            error={custom && !valid ? 'Введите неотрицательное число.' : undefined} />}
        </>}
    </div>
  </Modal>;
}
