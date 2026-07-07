import { useState } from 'react';
import {
  ChevronDown, ChevronRight, Plus, Pencil, Trash2, Copy, Eye, Filter, FunctionSquare, ArrowUpDown, Loader2,
  BookmarkPlus, ScanText, FileDown, Download, AlertTriangle,
} from 'lucide-react';
import { parseSourceColumnNames, countFilterConditions } from '@/shared/api/datasetHelpers';
import {
  useDeleteDataSetSource, useDuplicateDataSetSource, useSetDataSetSourceProcessing, useListProcessingTemplates,
  usePreviewDataSetSource, useCreateProcessingTemplate, useApplyProcessingTemplate, useRecognizePdfSource,
  isManualGroupingConflict, exportDataSetSource,
} from '@/shared/api/datasets';
import { ConfirmDialog } from '@/shared/ui/ConfirmDialog';
import { Modal } from '@/shared/ui/Modal';
import { RowActionsMenu, type RowAction } from '@/shared/ui/RowActionsMenu';
import { SourceEditorDialog } from './SourceEditorDialog';
import { PdfSourceDialog } from './PdfSourceDialog';
import { SourcePreviewDialog } from './SourcePreviewDialog';
import { RowFilterDialog } from './RowFilterDialog';
import { ComputedColumnsDialog } from './ComputedColumnsDialog';
import { SortSpecDialog } from './SortSpecDialog';
import type {
  DataSetFile, DataSetProcessingTemplate, DataSetSource, RowFilterDef, ComputedColumn, SortSpec, ColumnExprDef,
} from '@/shared/api/types';

/** Мини-диалог: только имя нового шаблона — сама Extraction + обработка уже известны (текущие источника). */
function SaveAsTemplateDialog({ defaultName, isPending, onSave, onClose }: {
  defaultName: string; isPending: boolean;
  onSave: (name: string) => void; onClose: () => void;
}) {
  const [name, setName] = useState(defaultName);
  const canSave = !isPending && !!name.trim();

  return (
    <Modal
      open
      onOpenChange={o => { if (!o && !isPending) onClose(); }}
      title="Сохранить как шаблон"
      footer={
        <div className="flex gap-2 justify-end">
          <button onClick={onClose} disabled={isPending}
            className="px-3 py-1.5 text-sm rounded-md border border-stroke text-fg2 hover:bg-muted disabled:opacity-50">
            Отмена
          </button>
          <button onClick={() => canSave && onSave(name.trim())} disabled={!canSave}
            className="px-3 py-1.5 text-sm rounded-md bg-brand text-white disabled:opacity-50">
            {isPending ? 'Сохранение…' : 'Сохранить'}
          </button>
        </div>
      }>
      <p className="text-xs mb-3 text-fg3">
        Row-selector/колонки (Extraction) и текущие Filter/Transformation/Sort источника
        станут переиспользуемым шаблоном — копия, не живая ссылка.
      </p>
      <input value={name} onChange={e => setName(e.target.value)} autoFocus
        onKeyDown={e => { if (e.key === 'Enter' && canSave) onSave(name.trim()); }}
        placeholder="Название шаблона"
        className="w-full px-3 py-2 rounded-lg border border-stroke-strong bg-base text-sm" />
    </Modal>
  );
}

/**
 * Число строк ПОСЛЕ полного пайплайна (Extraction → Filter → Transformation → Sort) — не
 * cachedRowCount источника (тот считается на Extraction, до Filter/Transformation, и может
 * сильно расходиться с реальным результатом маппинга).
 */
function SourceRowCountBadge({ sourceId }: { sourceId: string }) {
  const { data, isFetching } = usePreviewDataSetSource(sourceId, 1);
  if (isFetching && data === undefined) return <Loader2 size={11} className="inline-block ml-2 animate-spin text-fg4" />;
  return <span className="ml-2 font-normal text-fg4">{data?.totalRows ?? 0} строк</span>;
}

/**
 * Одна строка источника. Все действия (обработка/копия/распознавание/редактирование/удаление)
 * свёрнуты в меню «три точки» (см. RowActionsMenu) — их больше трёх; видимым остаётся только
 * Просмотр (основное действие чтения). Удаление — пунктом меню через ConfirmDialog, не hover-only
 * красная иконка (см. feedback_delete_ui_safety). Точка на «трёх точках» — активная обработка.
 */
function SourceRow({ src, isPdf, canManageExtraction, templates, maxColumns, onEdit }: {
  src: DataSetSource; isPdf: boolean; canManageExtraction: boolean;
  templates: DataSetProcessingTemplate[]; maxColumns: number; onEdit: (src: DataSetSource) => void;
}) {
  const [filterOpen, setFilterOpen] = useState(false);
  const [transformsOpen, setTransformsOpen] = useState(false);
  const [sortOpen, setSortOpen] = useState(false);
  const [savingTemplate, setSavingTemplate] = useState(false);
  const [previewing, setPreviewing] = useState(false);
  const [confirmDelete, setConfirmDelete] = useState(false);
  const [recognizeConflict, setRecognizeConflict] = useState(false);
  const [deleteError, setDeleteError] = useState<string | null>(null);

  const setProcessing = useSetDataSetSourceProcessing();
  const createTemplate = useCreateProcessingTemplate();
  const applyTemplateMutation = useApplyProcessingTemplate();
  const deleteMutation = useDeleteDataSetSource();
  const duplicateMutation = useDuplicateDataSetSource();
  const recognizeMutation = useRecognizePdfSource();

  const filterCount = countFilterConditions(src.rowFilter);
  const transformCount = src.computedColumns?.length ?? 0;
  const sortCount = src.sortSpec?.length ?? 0;
  const hasActiveProcessing = filterCount > 0 || transformCount > 0 || sortCount > 0;

  const computedAliases = (src.computedColumns ?? []).map(c => c.alias).filter(Boolean);
  const columns = [...new Set([...parseSourceColumnNames(src.cachedSchema), ...computedAliases])];
  const cols = parseSourceColumnNames(src.cachedSchema);

  function save(patch: { rowFilter?: RowFilterDef | null; computedColumns?: ComputedColumn[] | null; sortSpec?: SortSpec | null }) {
    setProcessing.mutate({
      id: src.id, rowFilter: src.rowFilter, computedColumns: src.computedColumns, sortSpec: src.sortSpec, ...patch,
    });
  }
  function applyTemplate(templateId: string) {
    applyTemplateMutation.mutate({ sourceId: src.id, templateId });
  }
  async function saveAsTemplate(name: string) {
    let columnExpressions: ColumnExprDef[] | null = null;
    try { columnExpressions = src.columnExpressions ? JSON.parse(src.columnExpressions) : null; } catch { columnExpressions = null; }
    await createTemplate.mutateAsync({
      name, sheetOrPath: src.sheetOrPath, columnExpressions,
      rowFilter: src.rowFilter, computedColumns: src.computedColumns, sortSpec: src.sortSpec,
    });
    setSavingTemplate(false);
  }
  function handleRecognize() {
    recognizeMutation.mutate({ id: src.id }, {
      onError: err => { if (isManualGroupingConflict(err)) setRecognizeConflict(true); },
    });
  }

  // Пассивные PDF-подисточники (обложка/титул/товары) заполняются вместе с главным — их не
  // распознают напрямую; показываем подпись-подсказку, но остальные действия доступны.
  const passiveLabel =
    isPdf && src.sheetOrPath === 'invoice-lineitems' ? 'товары'
      : isPdf && src.sheetOrPath === 'gost-cover' ? 'обложка'
      : isPdf && src.sheetOrPath === 'gost-titlepage' ? 'титул'
      : null;

  const badge = (n: number) => (n > 0 ? String(n) : undefined);
  const actions: RowAction[] = [
    { key: 'filter', label: 'Фильтрация строк', icon: <Filter size={13} />, onSelect: () => setFilterOpen(true), active: filterCount > 0, badge: badge(filterCount) },
    { key: 'transform', label: 'Вычисляемые колонки', icon: <FunctionSquare size={13} />, onSelect: () => setTransformsOpen(true), active: transformCount > 0, badge: badge(transformCount) },
    { key: 'sort', label: 'Сортировка строк', icon: <ArrowUpDown size={13} />, onSelect: () => setSortOpen(true), active: sortCount > 0, badge: badge(sortCount) },
    { key: 'apply-tpl', label: 'Применить шаблон', icon: <FileDown size={13} />, disabled: templates.length === 0 || applyTemplateMutation.isPending,
      submenu: templates.map(t => ({ key: t.id, label: t.name, onSelect: () => applyTemplate(t.id) })) },
    { key: 'save-tpl', label: 'Сохранить как шаблон…', icon: <BookmarkPlus size={13} />, onSelect: () => setSavingTemplate(true) },
    { key: 'export', label: 'Экспорт', icon: <Download size={13} />, submenu: [
      { key: 'export-xlsx', label: 'XLSX', onSelect: () => void exportDataSetSource(src.id, 'xlsx') },
      { key: 'export-xls', label: 'XLS', onSelect: () => void exportDataSetSource(src.id, 'xls') },
      { key: 'export-csv', label: 'CSV', onSelect: () => void exportDataSetSource(src.id, 'csv') },
    ] },
    { key: 'duplicate', label: 'Создать копию', icon: <Copy size={13} />, onSelect: () => duplicateMutation.mutate({ id: src.id }), disabled: duplicateMutation.isPending },
    ...(isPdf && !passiveLabel ? [{ key: 'recognize', label: 'Распознать', icon: <ScanText size={13} />, onSelect: handleRecognize, disabled: recognizeMutation.isPending }] : []),
    ...(canManageExtraction && !isPdf ? [{ key: 'edit', label: 'Редактировать', icon: <Pencil size={13} />, onSelect: () => onEdit(src) }] : []),
    ...(canManageExtraction ? [{ key: 'delete', label: 'Удалить источник', icon: <Trash2 size={13} />, danger: true, onSelect: () => { setDeleteError(null); setConfirmDelete(true); } }] : []),
  ];

  return (
    <div className="text-xs rounded-md p-2 bg-muted">
      <div className="flex items-start justify-between gap-2">
        <div className="min-w-0">
          <div className="font-medium text-fg1">
            {src.name}
            {src.recognitionStale && (
              <span title="Файл заменён после распознавания — данные относятся к прежнему файлу. Нажмите «Распознать»."
                className="ml-2 inline-flex items-center gap-1 px-1.5 py-0.5 rounded text-[10px] font-medium bg-warning-subtle text-warning align-middle">
                <AlertTriangle size={10} /> устарело
              </span>
            )}
            <SourceRowCountBadge sourceId={src.id} />
          </div>
          <div className="font-mono text-fg4 mt-0.5">{src.sheetOrPath}</div>
          {cols.length > 0 && (
            <div className="text-fg3 mt-0.5">
              {cols.slice(0, maxColumns).join(', ')}{cols.length > maxColumns ? ` +${cols.length - maxColumns}` : ''}
            </div>
          )}
          {deleteError && <div className="text-danger mt-1">{deleteError}</div>}
        </div>
        <div className="flex items-center gap-1 shrink-0" onClick={e => e.stopPropagation()}>
          {passiveLabel && <span className="text-fg4 italic" title="Заполняется вместе с главным источником той же тройки/пары">{passiveLabel}</span>}
          <button onClick={() => setPreviewing(true)} className="p-1 text-fg4 hover:text-brand" title="Просмотреть результат обработки">
            <Eye size={13} />
          </button>
          <RowActionsMenu actions={actions} ariaLabel="Действия над источником" hasActive={hasActiveProcessing} />
        </div>
      </div>

      {filterOpen && (
        <RowFilterDialog columns={columns} initial={src.rowFilter}
          onSave={f => save({ rowFilter: f })} onClose={() => setFilterOpen(false)} />
      )}
      {transformsOpen && (
        <ComputedColumnsDialog initial={src.computedColumns}
          onSave={c => save({ computedColumns: c })} onClose={() => setTransformsOpen(false)} />
      )}
      {sortOpen && (
        <SortSpecDialog columns={columns} initial={src.sortSpec}
          onSave={s => save({ sortSpec: s })} onClose={() => setSortOpen(false)} />
      )}
      {savingTemplate && (
        <SaveAsTemplateDialog defaultName={src.name} isPending={createTemplate.isPending}
          onSave={saveAsTemplate} onClose={() => setSavingTemplate(false)} />
      )}
      {previewing && <SourcePreviewDialog source={src} onClose={() => setPreviewing(false)} />}

      <ConfirmDialog
        open={recognizeConflict}
        onOpenChange={o => { if (!o) setRecognizeConflict(false); }}
        title="Разбиение было скорректировано вручную"
        description={<p>Повторное автораспознавание сотрёт ручные правки разбиения на документы. Продолжить?</p>}
        confirmLabel="Распознать заново"
        onConfirm={() => recognizeMutation.mutate({ id: src.id, confirm: true })}
      />

      <ConfirmDialog
        open={confirmDelete}
        onOpenChange={o => { if (!o) setConfirmDelete(false); }}
        title={`Удалить источник «${src.name}»?`}
        description={<p>Если источник используется в привязках документов — удаление будет отклонено.</p>}
        confirmLabel="Удалить источник"
        onConfirm={() => deleteMutation.mutateAsync({ id: src.id }).catch((err: { response?: { data?: string } }) =>
          setDeleteError(err?.response?.data ?? 'Не удалось удалить — возможно, источник используется в привязках.'))}
      />
    </div>
  );
}

/**
 * Collapsible list of a file's data sources with a preview of their column names.
 * Обработка (Filter/Transformation/Sort) доступна для источников любого формата; для PDF Extraction —
 * распознавание (не builder). Все действия строки свёрнуты в меню «три точки» (см. SourceRow).
 */
export function SourcesExpander({
  file,
  maxColumns = 8,
}: {
  file: DataSetFile;
  maxColumns?: number;
}) {
  const [open, setOpen] = useState(false);
  const [editing, setEditing] = useState<DataSetSource | 'new' | null>(null);
  const { data: templates = [] } = useListProcessingTemplates();
  const isPdf = file.format === 'Pdf';
  const canManageExtraction = file.format === 'Xml' || file.format === 'Zip' || file.format === 'Json' || isPdf;
  const sources = file.sources;

  if (sources.length === 0 && !canManageExtraction)
    return <span className="text-xs text-fg4">Нет источников</span>;

  return (
    <div>
      <div className="flex items-center gap-2">
        <button onClick={() => setOpen(o => !o)} aria-expanded={open} className="flex items-center gap-1 text-xs text-brand">
          {open ? <ChevronDown size={12} /> : <ChevronRight size={12} />}
          {sources.length > 0
            ? `${sources.length} ${sources.length === 1 ? 'источник' : 'источника(-ов)'}`
            : 'Нет источников'}
        </button>
        {canManageExtraction && (
          <button onClick={() => { setEditing('new'); setOpen(true); }}
            className="flex items-center gap-1 text-xs text-brand hover:text-brand-hover">
            <Plus size={11} /> Добавить источник
          </button>
        )}
      </div>
      {open && (
        <div className="mt-2 space-y-2 pl-3">
          {sources.map(src => (
            <SourceRow key={src.id} src={src} isPdf={isPdf} canManageExtraction={canManageExtraction}
              templates={templates} maxColumns={maxColumns} onEdit={setEditing} />
          ))}
        </div>
      )}

      {editing && (
        isPdf ? (
          <PdfSourceDialog fileId={file.id} onClose={() => setEditing(null)} />
        ) : (
          <SourceEditorDialog
            fileId={file.id}
            isZip={file.format === 'Zip'}
            format={file.format === 'Json' ? 'Json' : 'Xml'}
            initial={editing === 'new' ? undefined : editing}
            onClose={() => setEditing(null)}
          />
        )
      )}
    </div>
  );
}
