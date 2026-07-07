import { useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { Loader2, FileText, ExternalLink, LayoutGrid, Table2, RefreshCw } from 'lucide-react';
import { Modal } from '@/shared/ui/Modal';
import {
  usePreviewDataSetSource, useSourcePages, useSetDocumentTags, useRecognizeDocumentTable, useRecognizeDocument,
} from '@/shared/api/datasets';
import { openAttachmentInNewTab, formatBytes } from '@/shared/api/attachments';
import type { DataSetPreview, DataSetSource } from '@/shared/api/types';

const MAX_ROWS = 200;

/** Тэги типа таблицы документа (спецификация / кабельный журнал) — распознаются и выгружаются. */
const TABLE_TAGS: { code: string; label: string }[] = [
  { code: 'gostDoc.specification', label: 'Спецификация / ведомость' },
  { code: 'gostDoc.cableJournal', label: 'Кабельный журнал' },
];

/**
 * Специализированный просмотр для источника «Документы» (PDF, sheetOrPath === 'gost-documents'):
 * вместо сырых строковых колонок — карточки по документу с именем, числом листов, размером
 * и кнопкой открытия физически разделённого под-PDF (ФайлПуть — путь в MinIO). ФайлПуть может
 * быть пустым, если разбиение конкретного документа не удалось (backend логирует предупреждение
 * и оставляет колонку пустой) — это ожидаемо, кнопка в этом случае просто неактивна.
 */
function GostDocumentsPreview({ sourceId, sourceName, data }: { sourceId: string; sourceName: string; data: DataSetPreview }) {
  const navigate = useNavigate();
  const { data: grouping } = useSourcePages(sourceId);
  const setTags = useSetDocumentTags(sourceId);
  const recognizeTable = useRecognizeDocumentTable(sourceId);
  const recognizeDoc = useRecognizeDocument(sourceId);
  const docGroups = (grouping?.groups ?? []).filter(g => g.kind === 'Document');
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
      <div className="flex justify-end mb-1">
        <button
          onClick={() => navigate(`/datasets/sources/${sourceId}/grouping`, { state: { sourceName } })}
          className="flex items-center gap-1.5 px-2.5 py-1 text-xs rounded-md border border-stroke text-fg2 hover:bg-base transition-colors"
          title="Перенести страницы между документами, разделить/объединить группы вручную">
          <LayoutGrid size={12} /> Редактировать разбиение
        </button>
      </div>
      {recognizeTable.isError && (
        <p className="text-xs text-danger px-1">
          {(recognizeTable.error as { response?: { data?: { error?: string } } })?.response?.data?.error ?? 'Не удалось распознать таблицу'}
        </p>
      )}
      {recognizeTable.isSuccess && (
        <p className="text-xs text-fg3 px-1">
          Распознавание таблицы запущено — прогресс в индикаторе задач вверху; результат появится в источниках файла (выгрузка XLSX/CSV) и в уведомлениях.
        </p>
      )}
      {data.rows.map((row, i) => {
        const name = nameIdx >= 0 ? row[nameIdx] : null;
        const pages = pagesIdx >= 0 ? row[pagesIdx] : null;
        const sizeRaw = sizeIdx >= 0 ? row[sizeIdx] : null;
        const path = pathIdx >= 0 ? row[pathIdx] : null;
        const size = sizeRaw != null && sizeRaw !== '' && !Number.isNaN(Number(sizeRaw)) ? formatBytes(Number(sizeRaw)) : null;
        const group = docGroups[i];
        const firstPage = group?.pageIndices[0];
        const currentTag = group?.tags?.find(t => TABLE_TAGS.some(x => x.code === t)) ?? '';
        const recognizing = recognizeTable.isPending && recognizeTable.variables === firstPage;
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
            {group && firstPage !== undefined && (
              <select value={currentTag} disabled={setTags.isPending}
                onChange={e => setTags.mutate({ firstPageIndex: firstPage, tags: e.target.value ? [e.target.value] : [] })}
                title="Тип таблицы документа — распознаётся и выгружается (XLSX/CSV)"
                className="text-[11px] border border-stroke rounded px-1 py-0.5 bg-surface text-fg3 max-w-[150px] shrink-0 disabled:opacity-50">
                <option value="">— тип таблицы</option>
                {TABLE_TAGS.map(t => <option key={t.code} value={t.code}>{t.label}</option>)}
              </select>
            )}
            {currentTag && firstPage !== undefined && (
              <button
                onClick={() => recognizeTable.mutate(firstPage)}
                disabled={recognizing}
                title="Распознать таблицу этого документа как отдельный источник данных"
                className="flex items-center gap-1 shrink-0 px-2 py-1 text-xs rounded-md border border-stroke text-fg2 hover:bg-surface disabled:opacity-50">
                {recognizing ? <Loader2 size={12} className="animate-spin" /> : <Table2 size={12} />}
                Таблица
              </button>
            )}
            {group && firstPage !== undefined && (
              <button
                onClick={() => recognizeDoc.mutate(firstPage)}
                disabled={recognizeDoc.isPending && recognizeDoc.variables === firstPage}
                title="Перераспознать только этот документ (не весь набор)"
                className="flex items-center gap-1 shrink-0 px-2 py-1 text-xs rounded-md border border-stroke text-fg2 hover:bg-surface disabled:opacity-50">
                {recognizeDoc.isPending && recognizeDoc.variables === firstPage
                  ? <Loader2 size={12} className="animate-spin" />
                  : <RefreshCw size={12} />}
                Перераспознать
              </button>
            )}
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
        <GostDocumentsPreview sourceId={source.id} sourceName={source.name} data={data} />
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
