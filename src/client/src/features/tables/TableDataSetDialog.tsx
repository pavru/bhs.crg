import { useState, type FormEvent } from 'react';
import { useNavigate } from 'react-router';
import { useQueryClient } from '@tanstack/react-query';
import { createTableDataSet, type TableColumn, type TableExportBody } from '@/shared/api/tables';
import { Button } from '@/shared/ui/Button';
import { Modal, ModalExit } from '@/shared/ui/Modal';
import { TextField } from '@/shared/ui/TextField';
import { useToast } from '@/shared/ui/Toast';
import { apiError } from '@/shared/utils/apiError';

/**
 * Диалог моста «Сделать источником данных» (ТЗ CORE-33, COST-22; задача G3b, issue #1096).
 *
 * До нажатия он называет две вещи, которые иначе узнались бы поздно. Первая — колонки, которые в
 * источник не войдут: их значение зависит от отбора («доля»), а источник отбирает строки сам, и
 * шаблон напечатал бы под отбором по стройке полную сумму. Вторая — что источник это копия: правка
 * представления его не изменит.
 */
export function TableDataSetDialog({ address, initialName, body, columns, onClose }: {
  address: string;
  initialName: string;
  body: TableExportBody;
  /** Колонки экрана по порядку — с признаком «зависит от отбора». */
  columns: TableColumn[];
  onClose: () => void;
}) {
  const toast = useToast();
  const navigate = useNavigate();
  const qc = useQueryClient();
  const [name, setName] = useState(initialName);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const kept = columns.filter(c => !c.dependsOnFilter);
  const dropped = columns.filter(c => c.dependsOnFilter);

  async function submit(e: FormEvent) {
    e.preventDefault();
    if (!name.trim() || busy) return;
    setBusy(true);
    setError(null);
    try {
      const created = await createTableDataSet(address, name.trim(), body);
      void qc.invalidateQueries({ queryKey: ['datasets'] });
      onClose();
      toast.success(`Источник «${created.name}» создан в системном наборе уровня системы`, {
        action: { label: 'Открыть', onClick: () => navigate(`/datasets?file=${encodeURIComponent(created.fileId)}`) },
      });
    } catch (refusal) {
      // Отказ — под полем, словами сервера: «отбор стоит на колонке, которая закрыта».
      setError(apiError(refusal, 'Источник не создан'));
      setBusy(false);
    }
  }

  return (
    <Modal open onOpenChange={open => { if (!open) onClose(); }} title="Сделать источником данных" busy={busy}
      isDirty={name.trim() !== initialName.trim()}>
      <form onSubmit={submit} className="space-y-3">
        <TextField label="Название источника" value={name} maxLength={200} autoFocus required error={error ?? undefined}
          onChange={e => { setName(e.target.value); setError(null); }} />
        <div className="space-y-1.5 text-sm text-fg2">
          <p>
            Колонки: {kept.map(c => c.label).join(', ') || 'все колонки таблицы'}.
            {body.filter || body.sort?.length ? ' Отбор и порядок строк — как сейчас на экране.' : ' Отбора нет — все строки таблицы.'}
          </p>
          {dropped.length > 0 && (
            <p className="text-warning">
              Не войдут: {dropped.map(c => `«${c.label}»`).join(', ')} — значение зависит от отбора, а источник
              отбирает строки сам и посчитал бы его без отбора.
            </p>
          )}
          <p className="text-xs text-fg4">
            Это копия: источник не следит за представлением, и правка представления его не изменит. Строки он
            читает правами того, кто его открывает. Источник появится в системном наборе уровня
            системы.
          </p>
        </div>
        <div className="flex justify-end gap-2 pt-1">
          <ModalExit />
          <Button type="submit" variant="filled" loading={busy} disabled={!name.trim()}>Создать источник</Button>
        </div>
      </form>
    </Modal>
  );
}
