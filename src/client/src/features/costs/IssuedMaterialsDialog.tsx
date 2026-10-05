import { useState } from 'react';
import { TriangleAlert } from 'lucide-react';
import { Modal } from '@/shared/ui/Modal';
import { apiError } from '@/shared/utils/apiError';
import type { CostsConstruction } from '@/shared/api/invoices';
import { useIssuedMaterials } from '@/shared/api/waybills';
import { formatDate } from './invoiceFields';
import { linesWord, quantityInput } from './waybills';

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
    <Modal open onOpenChange={open => { if (!open) onClose(); }} title="Материалы на объекте" wide>
      <div className="space-y-3">
        <label className="block space-y-1">
          <span className="text-xs text-fg3">Стройка</span>
          <select value={site} onChange={e => setSite(e.target.value)} aria-label="Стройка"
            className="w-full rounded border border-stroke bg-surface px-2 py-1.5 text-sm text-fg outline-none focus:border-primary">
            <option value="">выберите стройку</option>
            {sites.map(s => <option key={s.id} value={s.id}>{s.name}</option>)}
          </select>
          {sitesFailed && (
            <span className="block text-xs text-danger">Список строек не пришёл: это отказ чтения, а не пустой список.</span>
          )}
        </label>

        {!site && <p className="text-xs text-fg4">Перечень считается по стройке — из проведённых накладных.</p>}
        {site && materials.isPending && <p className="text-xs text-fg3">Загрузка…</p>}
        {site && materials.isError && (
          <p className="text-xs text-danger">Перечень не пришёл: {apiError(materials.error, 'сервер отказал')}</p>
        )}

        {site && data && (
          <>
            {data.unmatchedLines > 0 && (
              <p role="status" className="flex items-start gap-2 rounded-lg border border-warning-border
                bg-warning-subtle px-3 py-2 text-xs text-warning">
                <TriangleAlert size={13} className="shrink-0 mt-0.5" />
                Не сопоставлено: {data.unmatchedLines} {linesWord(data.unmatchedLines)} в накладных
                ({data.unmatchedWaybills}). В перечне ниже их нет — выдано больше, чем показано. Найти их:
                отбор «Сопоставить» в списке накладных.
              </p>
            )}
            {data.items.length === 0
              ? (
                <p className="text-xs text-fg3">
                  {data.unmatchedLines > 0
                    ? 'Сопоставленных строк нет: всё выданное на эту стройку ждёт позиции номенклатуры.'
                    : 'На эту стройку ничего не выдано: проведённых накладных нет.'}
                </p>
              )
              : (
                <table className="w-full text-xs">
                  <thead>
                    <tr className="text-left text-fg4">
                      <th className="py-1 font-normal">Позиция</th>
                      <th className="w-28 py-1 font-normal text-right">Количество</th>
                      <th className="w-16 py-1 pl-2 font-normal">Ед.</th>
                      <th className="w-44 py-1 font-normal">Отпуск</th>
                      <th className="w-20 py-1 font-normal text-right">Накладных</th>
                    </tr>
                  </thead>
                  <tbody>
                    {data.items.map(item => (
                      <tr key={`${item.nomenclatureId}|${item.unit ?? ''}`} className="border-t border-stroke">
                        <td className="py-1 text-fg">{item.name ?? 'позиция без названия'}</td>
                        <td className="py-1 text-right tabular-nums text-fg">{quantityInput(item.quantity)}</td>
                        <td className="py-1 pl-2 text-fg3">{item.unit ?? ''}</td>
                        <td className="py-1 text-fg3">
                          {item.first === item.last
                            ? formatDate(item.first)
                            : `${formatDate(item.first)} — ${formatDate(item.last)}`}
                        </td>
                        <td className="py-1 text-right tabular-nums text-fg3">{item.waybills}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              )}
          </>
        )}
      </div>
    </Modal>
  );
}
