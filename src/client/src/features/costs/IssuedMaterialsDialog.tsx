import { useState } from 'react';
import { Icon } from '@/shared/ui/Icon';
import { Warning } from '@/shared/ui/icons';
import { Modal } from '@/shared/ui/Modal';
import { apiError } from '@/shared/utils/apiError';
import type { CostsConstruction } from '@/shared/api/invoices';
import { useIssuedMaterials, type IssuedMaterial } from '@/shared/api/waybills';
import { formatCount, formatDate, formatQuantity } from '@/shared/format/format';
import { linesWord } from './waybills';
import { Select, SelectItem } from '@/shared/ui/Select';
import { DataGrid, type DataGridColumn } from '@/shared/ui/DataGrid';

const COLUMNS: DataGridColumn<IssuedMaterial>[] = [
  { key: 'name', label: 'Позиция', wrap: true, cell: item => item.name ?? 'позиция без названия' },
  { key: 'quantity', label: 'Количество', align: 'right', width: 'w-28', cell: item => formatQuantity(item.quantity) },
  { key: 'unit', label: 'Ед.', width: 'w-16', cell: item => <span className="text-on-surface-variant">{item.unit ?? ''}</span> },
  { key: 'first', label: 'Отпуск', width: 'w-44',
    cell: item => (
      <span className="text-on-surface-variant">
        {item.first === item.last ? formatDate(item.first) : `${formatDate(item.first)} — ${formatDate(item.last)}`}
      </span>
    ) },
  { key: 'waybills', label: 'Накладных', align: 'right', width: 'w-20',
    cell: item => <span className="text-on-surface-variant">{formatCount(item.waybills)}</span> },
];

/**
 * «Материалы на объекте»: что выдано на стройку проведёнными накладными (ТЗ COST-17; задача D1,
 * issue #1083).
 *
 * <p>⚠️ Оговорка о несопоставленных строках стоит НАД перечнем, а не под ним: перечень без неё
 * читается как «выдано только это», а строка без позиции номенклатуры в него не попадает.</p>
 */
export function IssuedMaterialsDialog({ sites, sitesFailed, onClose }: {
  sites: CostsConstruction[];
  sitesFailed: boolean;
  onClose: () => void;
}) {
  const [site, setSite] = useState('');
  const materials = useIssuedMaterials(site || null);
  const data = materials.data;

  return (
    <Modal open onOpenChange={open => { if (!open) onClose(); }} title="Материалы на объекте" wide nothingToLose>
      <div className="space-y-3">
        <Select label="Стройка" value={site} onValueChange={setSite} placeholder="выберите стройку"
          error={sitesFailed ? 'Список строек не пришёл: это отказ чтения, а не пустой список.' : undefined}>
          {sites.map(s => <SelectItem key={s.id} value={s.id}>{s.name}</SelectItem>)}
        </Select>

        {!site && <p className="text-xs text-on-surface-variant">Перечень считается по стройке — из проведённых накладных.</p>}
        {site && materials.isPending && <p className="text-xs text-on-surface-variant">Загрузка…</p>}
        {site && materials.isError && (
          <p className="text-xs text-error">Перечень не пришёл: {apiError(materials.error, 'сервер отказал')}</p>
        )}

        {site && data && (
          <>
            {data.unmatchedLines > 0 && (
              <p role="status" className="flex items-start gap-2 rounded-lg border border-outline-variant
                bg-warning-container px-3 py-2 text-xs text-on-warning-container">
                <Icon icon={Warning} size={18} className="shrink-0 mt-0.5" />
                Не сопоставлено: {data.unmatchedLines} {linesWord(data.unmatchedLines)} в накладных
                ({data.unmatchedWaybills}). В перечне ниже их нет — выдано больше, чем показано. Найти их:
                отбор «Сопоставить» в списке накладных.
              </p>
            )}
            {data.items.length === 0
              ? (
                <p className="text-xs text-on-surface-variant">
                  {data.unmatchedLines > 0
                    ? 'Сопоставленных строк нет: всё выданное на эту стройку ждёт позиции номенклатуры.'
                    : 'На эту стройку ничего не выдано: проведённых накладных нет.'}
                </p>
              )
              : (
                <DataGrid density="compact" columns={COLUMNS} rows={data.items}
                  rowKey={item => `${item.nomenclatureId}|${item.unit ?? ''}`} />
              )}
          </>
        )}
      </div>
    </Modal>
  );
}
