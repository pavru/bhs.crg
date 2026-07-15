import { useState } from 'react';
import { Loader2, FileText, ExternalLink } from 'lucide-react';
import { Modal } from '@/shared/ui/Modal';
import { dtCard, dtTable, dtTh, dtTd, dtRow } from '@/shared/ui/dataTable';
import { usePreviewDataSetSource } from '@/shared/api/datasets';
import { openAttachmentInNewTab, formatBytes } from '@/shared/api/attachments';
import type { DataSetPreview, DataSetSource } from '@/shared/api/types';

const MAX_ROWS = 200;

/**
 * Просмотр источника «Документы» (PDF, sheetOrPath === 'gost-documents'): карточки по документу с
 * именем, числом листов, размером и кнопкой открытия физически разделённого под-PDF (ФайлПуть — путь
 * в MinIO). ФайлПуть может быть пустым, если разбиение конкретного документа не удалось — кнопка тогда
 * неактивна. Действия препроцессинга (разбиение, тэги, распознавание таблиц/документов) — на уровне
 * НАБОРА в редакторе разбиения (кнопка «Разбиение» у набора, issue #40), не здесь.
 */
function GostDocumentsPreview({ data }: { data: DataSetPreview }) {
  const nameIdx = data.columns.indexOf('НаименованиеДокумента');
  const pagesIdx = data.columns.indexOf('КоличествоЛистов');
  const sizeIdx = data.columns.indexOf('РазмерБайт');
  const pathIdx = data.columns.indexOf('ФайлПуть');
  const [opening, setOpening] = useState<number | null>(null);

  async function handleOpen(i: number, path: string) {
    setOpening(i);
    try { await openAttachmentInNewTab(path); }
    finally { setOpening(null); }
  }

  return (
    <div className="space-y-1.5">
      {data.rows.map((row, i) => {
        const name = nameIdx >= 0 ? row[nameIdx] : null;
        const pages = pagesIdx >= 0 ? row[pagesIdx] : null;
        const sizeRaw = sizeIdx >= 0 ? row[sizeIdx] : null;
        const path = pathIdx >= 0 ? row[pathIdx] : null;
        const size = sizeRaw != null && sizeRaw !== '' && !Number.isNaN(Number(sizeRaw)) ? formatBytes(Number(sizeRaw)) : null;
        return (
          <div key={i} className="flex items-center gap-3 rounded-md px-3 py-2 bg-muted">
            <FileText size={14} className="text-fg4 shrink-0" />
            <div className="min-w-0 flex-1">
              <div className="text-xs font-medium truncate text-fg1">
                {name ?? <em className="text-fg4">без названия</em>}
              </div>
              <div className="text-[11px] text-fg4">
                {pages ? `${pages} л.` : '—'}{size ? ` · ${size}` : ''}
              </div>
            </div>
            <button
              onClick={() => path && handleOpen(i, path)}
              disabled={!path || opening === i}
              title={path ? 'Открыть PDF в новой вкладке' : 'Файл не выделен — разбиение не удалось при распознавании'}
              className="flex items-center gap-1 shrink-0 px-2 py-1 text-xs rounded-md border border-stroke text-fg2 hover:bg-surface disabled:opacity-40 disabled:hover:bg-transparent">
              {opening === i ? <Loader2 size={12} className="animate-spin" /> : <ExternalLink size={12} />}
              Просмотреть
            </button>
          </div>
        );
      })}
      <div className="px-1 pt-1 text-xs text-fg4">
        {data.totalRows} строк{data.totalRows > data.rows.length ? ` (показано первых ${data.rows.length})` : ''}
      </div>
    </div>
  );
}

/**
 * Результат источника ПОСЛЕ полного пайплайна обработки (Extraction → Filter →
 * Transformation → Sort) — тот же запрос, что видел бы маппинг документа. Скаляр и
 * табличный набор не различаются на уровне источника (см. DataSetBindingProcessor):
 * скаляр — просто таблица с одной строкой, единый рендер покрывает оба случая.
 * Источник «Документы» (gost-documents) — исключение: у него вместо генерик-таблицы
 * специализированный просмотр с карточками и открытием разделённых под-PDF (см. GostDocumentsPreview).
 */
export function SourcePreviewDialog({ source, onClose }: { source: DataSetSource; onClose: () => void }) {
  const { data, isFetching, error } = usePreviewDataSetSource(source.id, MAX_ROWS);
  const isGostDocuments = source.sheetOrPath === 'gost-documents';

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
      ) : isGostDocuments ? (
        <GostDocumentsPreview data={data} />
      ) : (
        <div className={dtCard}>
          <table className={dtTable}>
            <thead>
              <tr>
                {data.columns.map(c => (
                  <th key={c} className={dtTh}>{c}</th>
                ))}
              </tr>
            </thead>
            <tbody>
              {data.rows.map((row, i) => (
                <tr key={i} className={dtRow}>
                  {row.map((v, j) => (
                    <td key={j} className={`${dtTd} whitespace-nowrap ${v == null ? 'text-fg4' : 'text-fg1'}`}>
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
