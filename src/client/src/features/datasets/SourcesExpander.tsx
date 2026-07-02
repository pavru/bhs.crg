import { useState } from 'react';
import { ChevronDown, ChevronRight, Plus, Pencil, Trash2, Copy, Filter, FunctionSquare, ArrowUpDown } from 'lucide-react';
import { parseSourceColumnNames, countFilterConditions } from '@/shared/api/datasetHelpers';
import {
  useDeleteDataSetSource, useDuplicateDataSetSource, useSetDataSetSourceProcessing, useListProcessingTemplates,
} from '@/shared/api/datasets';
import { SourceEditorDialog } from './SourceEditorDialog';
import { RowFilterDialog } from './RowFilterDialog';
import { ComputedColumnsDialog } from './ComputedColumnsDialog';
import { SortSpecDialog } from './SortSpecDialog';
import type { DataSetFile, DataSetProcessingTemplate, DataSetSource, RowFilterDef, ComputedColumn, SortSpec } from '@/shared/api/types';

/**
 * Обработка (Filter/Conversion/Sort) одного источника — доступна для любого формата
 * (не только XML). Либо своя настройка, либо живая ссылка на переиспользуемый шаблон
 * (правится централизованно — см. «Шаблоны обработки» на странице «Наборы данных»).
 */
function SourceProcessingControls({ source, templates }: {
  source: DataSetSource; templates: DataSetProcessingTemplate[];
}) {
  const [filterOpen, setFilterOpen] = useState(false);
  const [transformsOpen, setTransformsOpen] = useState(false);
  const [sortOpen, setSortOpen] = useState(false);
  const setProcessing = useSetDataSetSourceProcessing();

  const usesTemplate = !!source.processingTemplateId;
  const activeTemplate = templates.find(t => t.id === source.processingTemplateId);
  const effective = usesTemplate && activeTemplate
    ? { rowFilter: activeTemplate.rowFilter, computedColumns: activeTemplate.computedColumns, sortSpec: activeTemplate.sortSpec }
    : { rowFilter: source.rowFilter, computedColumns: source.computedColumns, sortSpec: source.sortSpec };

  const filterCount = countFilterConditions(effective.rowFilter);
  const transformCount = effective.computedColumns?.length ?? 0;
  const sortCount = effective.sortSpec?.length ?? 0;
  const columns = parseSourceColumnNames(source.cachedSchema);

  function save(patch: {
    rowFilter?: RowFilterDef | null; computedColumns?: ComputedColumn[] | null; sortSpec?: SortSpec | null;
  }) {
    setProcessing.mutate({
      id: source.id,
      rowFilter: source.rowFilter, computedColumns: source.computedColumns, sortSpec: source.sortSpec,
      processingTemplateId: source.processingTemplateId,
      ...patch,
    });
  }

  function selectTemplate(templateId: string) {
    setProcessing.mutate({
      id: source.id,
      // Свои значения не теряем — можно вернуться к individual-режиму с прежними настройками.
      rowFilter: source.rowFilter, computedColumns: source.computedColumns, sortSpec: source.sortSpec,
      processingTemplateId: templateId || null,
    });
  }

  const iconCls = (count: number) => `p-1 rounded ${count > 0 ? 'text-brand' : 'text-fg4'} disabled:opacity-40`;

  return (
    <div className="flex items-center gap-1 shrink-0" onClick={e => e.stopPropagation()}>
      <select value={source.processingTemplateId ?? ''} onChange={e => selectTemplate(e.target.value)}
        title="Шаблон обработки (Filter/Conversion/Sort)"
        className="text-[11px] border border-stroke rounded px-1 py-0.5 bg-surface text-fg3 max-w-[110px]">
        <option value="">своя настройка</option>
        {templates.map(t => <option key={t.id} value={t.id}>{t.name}</option>)}
      </select>
      <button onClick={() => setFilterOpen(true)} disabled={usesTemplate} className={iconCls(filterCount)}
        title={usesTemplate ? 'Управляется шаблоном обработки' : 'Фильтрация строк'}>
        <Filter size={12} />
      </button>
      <button onClick={() => setTransformsOpen(true)} disabled={usesTemplate} className={iconCls(transformCount)}
        title={usesTemplate ? 'Управляется шаблоном обработки' : 'Вычисляемые колонки'}>
        <FunctionSquare size={12} />
      </button>
      <button onClick={() => setSortOpen(true)} disabled={usesTemplate} className={iconCls(sortCount)}
        title={usesTemplate ? 'Управляется шаблоном обработки' : 'Сортировка строк'}>
        <ArrowUpDown size={12} />
      </button>

      {filterOpen && (
        <RowFilterDialog columns={columns} initial={source.rowFilter}
          onSave={f => save({ rowFilter: f })} onClose={() => setFilterOpen(false)} />
      )}
      {transformsOpen && (
        <ComputedColumnsDialog initial={source.computedColumns}
          onSave={c => save({ computedColumns: c })} onClose={() => setTransformsOpen(false)} />
      )}
      {sortOpen && (
        <SortSpecDialog columns={columns} initial={source.sortSpec}
          onSave={s => save({ sortSpec: s })} onClose={() => setSortOpen(false)} />
      )}
    </div>
  );
}

/**
 * Collapsible list of a file's data sources with a preview of their column names.
 * Для XML (и XML внутри ZIP) источники управляются только вручную (создание/редактирование/
 * удаление) — авто-детект по top-level элементам для XML не используется. Для JSON — авто-детект
 * top-level массивов/объектов создаёт исходные источники, но также доступно ручное управление
 * (например, чтобы задать вложенный/фильтрующий JSONPath, недоступный авто-детекту).
 * Обработка (Filter/Conversion/Sort) доступна для источников любого формата.
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
  const [confirmDelete, setConfirmDelete] = useState<DataSetSource | null>(null);
  const deleteMutation = useDeleteDataSetSource();
  const duplicateMutation = useDuplicateDataSetSource();
  const { data: templates = [] } = useListProcessingTemplates();
  const canManageExtraction = file.format === 'Xml' || file.format === 'Zip' || file.format === 'Json';
  const sources = file.sources;

  if (sources.length === 0 && !canManageExtraction)
    return <span className="text-xs text-fg4">Нет источников</span>;

  return (
    <div>
      <div className="flex items-center gap-2">
        <button onClick={() => setOpen(o => !o)} className="flex items-center gap-1 text-xs text-brand">
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
          {sources.map(src => {
            const cols = parseSourceColumnNames(src.cachedSchema);
            return (
              <div key={src.id} className="text-xs rounded-md p-2 bg-muted">
                <div className="flex items-start justify-between gap-2">
                  <div className="min-w-0">
                    <div className="font-medium text-fg1">
                      {src.name}
                      <span className="ml-2 font-normal text-fg4">{src.cachedRowCount} строк</span>
                    </div>
                    <div className="font-mono text-fg4 mt-0.5">{src.sheetOrPath}</div>
                    {cols.length > 0 && (
                      <div className="text-fg3 mt-0.5">
                        {cols.slice(0, maxColumns).join(', ')}{cols.length > maxColumns ? ` +${cols.length - maxColumns}` : ''}
                      </div>
                    )}
                  </div>
                  <div className="flex items-center gap-1 shrink-0">
                    <SourceProcessingControls source={src} templates={templates} />
                    <button
                      onClick={() => duplicateMutation.mutate({ id: src.id })}
                      disabled={duplicateMutation.isPending && duplicateMutation.variables?.id === src.id}
                      className="p-1 text-fg4 hover:text-brand disabled:opacity-50" title="Создать копию">
                      <Copy size={12} />
                    </button>
                    {canManageExtraction && (
                      <>
                        <button onClick={() => setEditing(src)} className="p-1 text-fg4 hover:text-brand" title="Редактировать">
                          <Pencil size={12} />
                        </button>
                        <button onClick={() => setConfirmDelete(src)} className="p-1 text-fg4 hover:text-danger" title="Удалить">
                          <Trash2 size={12} />
                        </button>
                      </>
                    )}
                  </div>
                </div>
              </div>
            );
          })}
        </div>
      )}

      {editing && (
        <SourceEditorDialog
          fileId={file.id}
          isZip={file.format === 'Zip'}
          format={file.format === 'Json' ? 'Json' : 'Xml'}
          initial={editing === 'new' ? undefined : editing}
          onClose={() => setEditing(null)}
        />
      )}

      {confirmDelete && (
        <div className="fixed inset-0 z-[60] flex items-center justify-center bg-black/50 p-4"
          onClick={() => setConfirmDelete(null)}>
          <div className="rounded-xl p-5 w-full max-w-sm bg-surface border border-stroke shadow-2xl" onClick={e => e.stopPropagation()}>
            <p className="text-sm font-semibold mb-1 text-fg1">Удалить источник «{confirmDelete.name}»?</p>
            <p className="text-xs mb-4 text-fg3">
              Если он используется в привязках документов — удаление будет отклонено.
            </p>
            <div className="flex gap-2 justify-end">
              <button onClick={() => setConfirmDelete(null)}
                className="px-3 py-1.5 text-sm rounded-md border border-stroke text-fg2 hover:bg-muted">
                Отмена
              </button>
              <button
                onClick={() => deleteMutation.mutateAsync({ id: confirmDelete.id })
                  .then(() => setConfirmDelete(null))
                  .catch(() => {})}
                disabled={deleteMutation.isPending}
                className="px-3 py-1.5 text-sm rounded-md bg-danger text-white disabled:opacity-50">
                Удалить
              </button>
            </div>
            {deleteMutation.isError && (
              <p className="text-xs text-danger mt-2">
                {(deleteMutation.error as { response?: { data?: string } })?.response?.data
                  ?? 'Не удалось удалить — возможно, источник используется в привязках.'}
              </p>
            )}
          </div>
        </div>
      )}
    </div>
  );
}
