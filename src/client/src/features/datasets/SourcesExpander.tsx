import { useState } from 'react';
import {
  ChevronDown, ChevronRight, Plus, Pencil, Trash2, Copy, Eye, Filter, FunctionSquare, ArrowUpDown, Loader2, BookmarkPlus,
} from 'lucide-react';
import { parseSourceColumnNames, countFilterConditions } from '@/shared/api/datasetHelpers';
import {
  useDeleteDataSetSource, useDuplicateDataSetSource, useSetDataSetSourceProcessing, useListProcessingTemplates,
  usePreviewDataSetSource, useCreateProcessingTemplate,
} from '@/shared/api/datasets';
import { SourceEditorDialog } from './SourceEditorDialog';
import { SourcePreviewDialog } from './SourcePreviewDialog';
import { RowFilterDialog } from './RowFilterDialog';
import { ComputedColumnsDialog } from './ComputedColumnsDialog';
import { SortSpecDialog } from './SortSpecDialog';
import type { DataSetFile, DataSetProcessingTemplate, DataSetSource, RowFilterDef, ComputedColumn, SortSpec } from '@/shared/api/types';

/**
 * Обработка (Filter/Transformation/Sort) одного источника — доступна для любого формата
 * (не только XML). Своя, независимая настройка; шаблон обработки — только источник значений
 * для разового применения (копирование, как и шаблон маппинга), не живая ссылка: применили —
 * дальше можно свободно корректировать под конкретный источник.
 */
function SourceProcessingControls({ source, templates }: {
  source: DataSetSource; templates: DataSetProcessingTemplate[];
}) {
  const [filterOpen, setFilterOpen] = useState(false);
  const [transformsOpen, setTransformsOpen] = useState(false);
  const [sortOpen, setSortOpen] = useState(false);
  const [savingTemplate, setSavingTemplate] = useState(false);
  const setProcessing = useSetDataSetSourceProcessing();
  const createTemplate = useCreateProcessingTemplate();

  const filterCount = countFilterConditions(source.rowFilter);
  const transformCount = source.computedColumns?.length ?? 0;
  const sortCount = source.sortSpec?.length ?? 0;
  // Filter/Sort работают уже ПОСЛЕ Transformation в пайплайне (см. DataSetBindingProcessor) —
  // их список колонок должен включать и вычисляемые (иначе в UI недоступны, хотя backend их
  // уже поддерживает и в фильтре, и в сортировке).
  const computedAliases = (source.computedColumns ?? []).map(c => c.alias).filter(Boolean);
  const columns = [...new Set([...parseSourceColumnNames(source.cachedSchema), ...computedAliases])];
  const hasOwnProcessing = filterCount > 0 || transformCount > 0 || sortCount > 0;

  function save(patch: {
    rowFilter?: RowFilterDef | null; computedColumns?: ComputedColumn[] | null; sortSpec?: SortSpec | null;
  }) {
    setProcessing.mutate({
      id: source.id,
      rowFilter: source.rowFilter, computedColumns: source.computedColumns, sortSpec: source.sortSpec,
      ...patch,
    });
  }

  // Применение шаблона копирует его значения в источник ОДИН РАЗ (как и шаблон маппинга) —
  // дальше это обычная своя настройка, дальнейшая правка шаблона на источник не влияет.
  function applyTemplate(templateId: string) {
    const t = templates.find(x => x.id === templateId);
    if (!t) return;
    save({ rowFilter: t.rowFilter, computedColumns: t.computedColumns, sortSpec: t.sortSpec });
  }

  async function saveAsTemplate(name: string) {
    await createTemplate.mutateAsync({
      name, rowFilter: source.rowFilter, computedColumns: source.computedColumns, sortSpec: source.sortSpec,
    });
    setSavingTemplate(false);
  }

  const iconCls = (count: number) => `p-1 rounded ${count > 0 ? 'text-brand' : 'text-fg4'} disabled:opacity-40`;

  return (
    <div className="flex items-center gap-1 shrink-0" onClick={e => e.stopPropagation()}>
      <select value="" onChange={e => applyTemplate(e.target.value)}
        title="Применить шаблон обработки (копирует значения, не живая ссылка)"
        className="text-[11px] border border-stroke rounded px-1 py-0.5 bg-surface text-fg3 max-w-[110px]">
        <option value="">применить шаблон…</option>
        {templates.map(t => <option key={t.id} value={t.id}>{t.name}</option>)}
      </select>
      <button onClick={() => setFilterOpen(true)} className={iconCls(filterCount)} title="Фильтрация строк">
        <Filter size={12} />
      </button>
      <button onClick={() => setTransformsOpen(true)} className={iconCls(transformCount)} title="Вычисляемые колонки">
        <FunctionSquare size={12} />
      </button>
      <button onClick={() => setSortOpen(true)} className={iconCls(sortCount)} title="Сортировка строк">
        <ArrowUpDown size={12} />
      </button>
      <button onClick={() => setSavingTemplate(true)} disabled={!hasOwnProcessing}
        className="p-1 rounded text-fg4 hover:text-brand disabled:opacity-40"
        title={!hasOwnProcessing ? 'Нечего сохранять — обработка не задана' : 'Сохранить текущую обработку как шаблон'}>
        <BookmarkPlus size={12} />
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
      {savingTemplate && (
        <SaveAsTemplateDialog
          defaultName={source.name} isPending={createTemplate.isPending}
          onSave={saveAsTemplate} onClose={() => setSavingTemplate(false)}
        />
      )}
    </div>
  );
}

/** Мини-диалог: только имя нового шаблона — сама обработка уже известна (текущая источника). */
function SaveAsTemplateDialog({ defaultName, isPending, onSave, onClose }: {
  defaultName: string; isPending: boolean;
  onSave: (name: string) => void; onClose: () => void;
}) {
  const [name, setName] = useState(defaultName);

  return (
    <div className="fixed inset-0 z-[60] flex items-center justify-center bg-black/50 p-4" onClick={onClose}>
      <div className="rounded-xl p-5 w-full max-w-sm bg-surface border border-stroke shadow-2xl" onClick={e => e.stopPropagation()}>
        <p className="text-sm font-semibold mb-1 text-fg1">Сохранить обработку как шаблон</p>
        <p className="text-xs mb-3 text-fg3">
          Текущие Filter/Transformation/Sort источника станут переиспользуемым шаблоном;
          источник сразу переключится на живую ссылку на него.
        </p>
        <input value={name} onChange={e => setName(e.target.value)} autoFocus
          placeholder="Название шаблона"
          className="w-full px-3 py-2 rounded-lg border border-stroke-strong bg-base text-sm mb-4" />
        <div className="flex gap-2 justify-end">
          <button onClick={onClose} className="px-3 py-1.5 text-sm rounded-md border border-stroke text-fg2 hover:bg-muted">
            Отмена
          </button>
          <button
            onClick={() => name.trim() && onSave(name.trim())}
            disabled={isPending || !name.trim()}
            className="px-3 py-1.5 text-sm rounded-md bg-brand text-white disabled:opacity-50">
            {isPending ? 'Сохранение…' : 'Сохранить'}
          </button>
        </div>
      </div>
    </div>
  );
}

/**
 * Число строк ПОСЛЕ полного пайплайна (Extraction → Filter → Transformation → Sort) — не
 * cachedRowCount источника (тот считается на Extraction, до Filter/Transformation, и может
 * сильно расходиться с реальным результатом маппинга). Живой запрос — тот же путь, что и
 * SourcePreviewDialog, maxRows=1 просто чтобы не тянуть лишние данные строк.
 */
function SourceRowCountBadge({ sourceId }: { sourceId: string }) {
  const { data, isFetching } = usePreviewDataSetSource(sourceId, 1);
  if (isFetching) return <Loader2 size={11} className="inline-block ml-2 animate-spin text-fg4" />;
  return <span className="ml-2 font-normal text-fg4">{data?.totalRows ?? 0} строк</span>;
}

/**
 * Collapsible list of a file's data sources with a preview of their column names.
 * Для XML (и XML внутри ZIP) источники управляются только вручную (создание/редактирование/
 * удаление) — авто-детект по top-level элементам для XML не используется. Для JSON — авто-детект
 * top-level массивов/объектов создаёт исходные источники, но также доступно ручное управление
 * (например, чтобы задать вложенный/фильтрующий JSONPath, недоступный авто-детекту).
 * Обработка (Filter/Transformation/Sort) доступна для источников любого формата.
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
  const [previewing, setPreviewing] = useState<DataSetSource | null>(null);
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
                      <SourceRowCountBadge sourceId={src.id} />
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
                    <button onClick={() => setPreviewing(src)} className="p-1 text-fg4 hover:text-brand"
                      title="Просмотреть результат обработки">
                      <Eye size={12} />
                    </button>
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

      {previewing && (
        <SourcePreviewDialog source={previewing} onClose={() => setPreviewing(null)} />
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
