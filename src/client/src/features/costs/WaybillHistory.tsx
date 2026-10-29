import { useState } from 'react';
import { DataGrid, type DataGridColumn } from '@/shared/ui/DataGrid';
import { useWaybillVersions, type WaybillView } from '@/shared/api/waybills';
import { apiError } from '@/shared/utils/apiError';
import { formatDate, formatDateTime, formatInput } from '@/shared/format/format';
import { correctionEvents, type CorrectionEvent, type WaybillContent } from './waybillCorrection';

type PriorLine = WaybillContent['lines'][number];

/**
 * «Исправления» — история накладной (D10): строка — событие исправления, «кто, когда, почему и что
 * изменилось», прежнее содержимое раскрывается щелчком. Только у исправленной: у прочих истории нет, и
 * пустая секция читалась бы «исправлений не было — проверьте».
 */
export function WaybillHistory({ view, gone }: { view: WaybillView; gone: boolean }) {
  // Удалённая накладная историю не спрашивает: адрес ответил бы 404 под «накладной больше нет».
  const shown = (view.currentVersion ?? 1) > 1 && !gone;
  const versions = useWaybillVersions(view.id, shown);
  const [open, setOpen] = useState<number | null>(null);
  if (!shown) return null;

  const events = versions.data ? correctionEvents(versions.data, view) : [];
  const selected = events.find(e => e.version === open) ?? null;
  const columns: DataGridColumn<CorrectionEvent>[] = [
    { key: 'at', label: 'Когда', width: 'w-36', cell: e => formatDateTime(e.at) },
    { key: 'author', label: 'Кто', cell: e => e.author },
    { key: 'reason', label: 'Причина', wrap: true, cell: e => e.reason },
    {
      key: 'changes', label: 'Что изменилось', wrap: true,
      cell: e => (
        <>
          {e.changes.length > 0 ? e.changes.join('; ') : '—'}
          {e.mixed && (
            <span className="block text-on-surface-variant">
              Сверено с нынешним содержимым: после исправления накладную можно было править обычным путём, и
              такие правки тоже здесь.
            </span>
          )}
        </>
      ),
    },
    { key: 'version', label: 'Прежнее', cell: e => `версия ${e.version}` },
  ];

  // Позиции в версии лежат идентификатором: имя знают только строки действующей накладной.
  const names = new Map(view.lines.filter(l => l.nomenclatureId).map(l => [l.nomenclatureId!, l.nomenclatureName]));
  const lineColumns: DataGridColumn<PriorLine>[] = [
    { key: 'ordinal', label: '№', width: 'w-10', cell: l => l.ordinal },
    { key: 'nomenclature', label: 'Позиция', cell: l => (l.nomenclatureId ? names.get(l.nomenclatureId) ?? 'выбрана' : 'не сопоставлена') },
    { key: 'sourceText', label: 'Наименование в накладной', wrap: true, cell: l => l.sourceText ?? '—' },
    { key: 'unit', label: 'Ед.', width: 'w-16', cell: l => l.unit ?? '—' },
    { key: 'quantity', label: 'Количество', align: 'right', width: 'w-28', cell: l => <span className="tabular-nums">{formatInput(l.quantity)}</span> },
    { key: 'note', label: 'Примечание', wrap: true, cell: l => l.note ?? '—' },
  ];

  return (
    <section className="space-y-3" aria-label="Исправления" data-testid="waybill-history">
      <h3 className="text-sm font-medium text-on-surface-variant">Исправления</h3>
      {versions.isPending && <p className="text-xs text-on-surface-variant">Загружаем историю накладной…</p>}
      {versions.error != null && <p className="text-xs text-error">История накладной не загрузилась: {apiError(versions.error)}</p>}
      {versions.data && (
        <>
          <DataGrid density="compact" rows={events} rowKey={e => String(e.version)} columns={columns}
            onRowOpen={e => setOpen(o => (o === e.version ? null : e.version))} rowOpen={e => e.version === open} />
          {selected && (
            <div className="space-y-2">
              <p className="text-xs text-on-surface-variant">
                Прежнее содержимое — версия {selected.version}: № {selected.prior.number ?? 'без номера'}
                {selected.prior.issuedOn ? ` от ${formatDate(selected.prior.issuedOn)}` : ''}
                {selected.prior.warehouse ? `, склад «${selected.prior.warehouse}»` : ''}
                {selected.prior.receivedBy ? `, получил ${selected.prior.receivedBy}` : ''}
                {selected.prior.note ? `; примечание: ${selected.prior.note}` : ''}.
              </p>
              <DataGrid density="compact" rows={selected.prior.lines} rowKey={l => l.id} columns={lineColumns} />
            </div>
          )}
        </>
      )}
    </section>
  );
}
