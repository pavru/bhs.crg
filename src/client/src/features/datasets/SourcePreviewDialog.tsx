import { useState } from 'react';
import { Icon } from '@/shared/ui/Icon';
import { CircularProgress } from '@/shared/ui/CircularProgress';
import { Description, OpenInNew, VerifiedUser } from '@/shared/ui/icons';
import { Modal } from '@/shared/ui/Modal';
import { DataGrid, type DataGridColumn } from '@/shared/ui/DataGrid';
import { usePreviewDataSetSource } from '@/shared/api/datasets';
import { formatBytes } from '@/shared/api/attachments';
import { useOpenAttachment } from '@/shared/hooks/useOpenAttachment';
import type { DataSetPreview, DataSetSource } from '@/shared/api/types';
import { Button } from '@/shared/ui/Button';

/**
 * Граница выдачи опубликованного набора (ТЗ CORE-24.3, issue #965).
 *
 * Подпись ПОСТОЯННАЯ и нейтральная по виду: не предупреждение и не ошибка. Строки такого набора
 * отобраны по правам открывшего, и человек должен видеть это и тогда, когда всё в порядке —
 * оформленная как замечание, она читалась бы как «что-то не так», и её перестали бы замечать.
 *
 * Про скрытое не сказано намеренно: «12 строк скрыто» человек проверить не может, а две разные
 * цифры, обе выглядящие окончательными, опаснее одной с оговоркой.
 */
function SourceBoundary({ boundary }: { boundary?: string | null }) {
  if (!boundary) return null;
  return (
    <div className="px-1 pt-2 text-body-small text-on-surface-variant flex items-start gap-1.5">
      <Icon icon={VerifiedUser} size={18} className="mt-0.5 shrink-0 text-on-surface-variant" />
      <span>{boundary}</span>
    </div>
  );
}

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
  const openAttachment = useOpenAttachment();

  async function handleOpen(i: number, path: string) {
    setOpening(i);
    await openAttachment(path);
    setOpening(null);
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
          <div key={i} className="flex items-center gap-3 rounded-sm px-3 py-2 bg-surface-container">
            <Icon icon={Description} size={18} className="text-on-surface-variant shrink-0" />
            <div className="min-w-0 flex-1">
              <div className="text-body-small font-medium truncate text-on-surface">
                {name ?? <em className="text-on-surface-variant">без названия</em>}
              </div>
              <div className="text-label-small text-on-surface-variant">
                {pages ? `${pages} л.` : '—'}{size ? ` · ${size}` : ''}
              </div>
            </div>
            <Button title={path ? 'Открыть PDF в новой вкладке' : 'Файл не выделен — разбиение не удалось при распознавании'} variant="outlined" className="shrink-0"
              onClick={() => path && handleOpen(i, path)} disabled={!path} loading={opening === i}
              icon={<Icon icon={OpenInNew} size={18} />}>
              Просмотреть
            </Button>
          </div>
        );
      })}
      <SourceBoundary boundary={data.boundary} />
      <div className="px-1 pt-1 text-body-small text-on-surface-variant">
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
/**
 * Предпросмотр приходит строками-массивами, а сетке нужны записи. Ключ колонки — её номер, а не
 * имя: имена у колонок источника повторяются, и одна затёрла бы другую.
 */
function previewGrid(data: DataSetPreview) {
  const columns: DataGridColumn[] = data.columns.map((label, j) => ({ key: String(j), label }));
  const rows = data.rows.map(row => Object.fromEntries(row.map((v, j) => [String(j), v])));
  return { columns, rows };
}

export function SourcePreviewDialog({ source, onClose }: { source: DataSetSource; onClose: () => void }) {
  const { data, isFetching, error } = usePreviewDataSetSource(source.id, MAX_ROWS);
  const isGostDocuments = source.sheetOrPath === 'gost-documents';

  return (
    <Modal open onOpenChange={o => { if (!o) onClose(); }} title={`Результат обработки — ${source.name}`} wide nothingToLose>
      <p className="text-body-small mb-3 text-on-surface-variant">
        Результат после Filter/Transformation/Sort — то, что получит маппинг документа.
      </p>

      {isFetching ? (
        <div className="flex items-center gap-2 py-8 justify-center text-body-medium text-on-surface-variant">
          <CircularProgress size={18} /> Загрузка...
        </div>
      ) : error ? (
        <p className="text-body-medium text-error py-4">
          {(error as { response?: { data?: { error?: string } } })?.response?.data?.error ?? 'Не удалось получить результат'}
        </p>
      ) : !data || data.columns.length === 0 ? (
        <p className="text-body-medium text-on-surface-variant py-4 text-center">Нет данных — либо строки не найдены, либо все отфильтрованы.</p>
      ) : isGostDocuments ? (
        <GostDocumentsPreview data={data} />
      ) : (
        <>
          {/* Ограниченная по высоте карточка — единый скролл-контейнер (sticky-шапка + горизонтальный
              скролл ВНУТРИ него); иначе таблица растягивала тело модалки и горизонтальный скроллбар
              «уезжал» к дну длинной таблицы, а на стыке со скроллом модалки был глюк (issue #240). */}
          <DataGrid className="max-h-[65vh]" {...previewGrid(data)} />
          <SourceBoundary boundary={data.boundary} />
          <div className="px-1 pt-2 text-body-small text-on-surface-variant">
            {data.totalRows} строк{data.totalRows > data.rows.length ? ` (показано первых ${data.rows.length})` : ''}
          </div>
        </>
      )}
    </Modal>
  );
}
