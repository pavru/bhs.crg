import { Loader2 } from 'lucide-react';
import { Modal } from '@/shared/ui/Modal';
import { usePreviewDataSetSource } from '@/shared/api/datasets';
import type { DataSetSource } from '@/shared/api/types';

const MAX_ROWS = 200;

/**
 * Результат источника ПОСЛЕ полного пайплайна обработки (Extraction → Filter →
 * Transformation → Sort) — тот же запрос, что видел бы маппинг документа. Скаляр и
 * табличный набор не различаются на уровне источника (см. DataSetBindingProcessor):
 * скаляр — просто таблица с одной строкой, единый рендер покрывает оба случая.
 */
export function SourcePreviewDialog({ source, onClose }: { source: DataSetSource; onClose: () => void }) {
  const { data, isFetching, error } = usePreviewDataSetSource(source.id, MAX_ROWS);

  return (
    <Modal open onOpenChange={o => { if (!o) onClose(); }} title={`Результат обработки — ${source.name}`} extraWide>
      <p className="text-xs mb-3 text-fg4">
        Результат после Filter/Transformation/Sort — то, что получит маппинг документа.
      </p>

      {isFetching ? (
        <div className="flex items-center gap-2 py-8 justify-center text-sm text-fg4">
          <Loader2 size={16} className="animate-spin" /> Загрузка...
        </div>
      ) : error ? (
        <p className="text-sm text-danger py-4">
          {(error as { response?: { data?: { error?: string } } })?.response?.data?.error ?? 'Не удалось получить результат'}
        </p>
      ) : !data || data.columns.length === 0 ? (
        <p className="text-sm text-fg4 py-4 text-center">Нет данных — либо строки не найдены, либо все отфильтрованы.</p>
      ) : (
        <div className="overflow-x-auto rounded-lg border border-stroke">
          <table className="text-xs w-full">
            <thead>
              <tr className="bg-base">
                {data.columns.map(c => (
                  <th key={c} className="px-3 py-1.5 text-left font-medium whitespace-nowrap text-fg3">{c}</th>
                ))}
              </tr>
            </thead>
            <tbody>
              {data.rows.map((row, i) => (
                <tr key={i} className="border-t border-stroke">
                  {row.map((v, j) => (
                    <td key={j} className={`px-3 py-1.5 whitespace-nowrap ${v == null ? 'text-fg4' : 'text-fg1'}`}>
                      {v ?? <em>null</em>}
                    </td>
                  ))}
                </tr>
              ))}
            </tbody>
          </table>
          <div className="px-3 py-1.5 border-t border-stroke text-xs text-fg4">
            {data.totalRows} строк{data.totalRows > data.rows.length ? ` (показано первых ${data.rows.length})` : ''}
          </div>
        </div>
      )}
    </Modal>
  );
}
