import { useState, useMemo } from 'react';
import { Database, Filter, FunctionSquare, Pencil, Trash2, Plus, LayoutTemplate, PlayCircle, Loader2, AlertCircle, CheckCircle2 } from 'lucide-react';
import { ApplyTemplateDialog } from './ApplyTemplateDialog';
import { RowFilterDialog } from '@/features/datasets/RowFilterDialog';
import { ComputedColumnsDialog } from '@/features/datasets/ComputedColumnsDialog';
import {
  useAvailableDataSetFiles, useListDataSetBindings,
  useCreateDataSetBinding, useUpdateDataSetBinding, useDeleteDataSetBinding,
  useAutoMapDataSetSource, usePreviewDataSetBindings,
} from '@/shared/api/datasets';
import type { DocumentInstance, DocumentType, DataSetSource, DataSetBinding, DataSetBindingPreviewResult, RowFilterDef, ComputedColumn } from '@/shared/api/types';
import { DATA_SET_FORMAT_LABELS, SCOPE_LABELS } from '@/shared/api/types';
import { resolveEffectiveFields, isScalarField, type SchemaField } from '@/shared/api/schema';
import { countFilterConditions, parseSourceColumnNames, parseRefMapping, buildRefMapping } from '@/shared/api/datasetHelpers';
function MappingEditor({
  source,
  schemaFields,
  arrayFields,
  allDocTypes,
  mapping,
  targetFieldKey,
  onChange,
}: {
  source: DataSetSource;
  schemaFields: SchemaField[];
  arrayFields: SchemaField[];
  allDocTypes: DocumentType[];
  mapping: Record<string, string>;
  targetFieldKey: string | null;
  onChange: (m: Record<string, string>, t: string | null) => void;
}) {
  const columnNames = useMemo(() => parseSourceColumnNames(source.cachedSchema), [source.cachedSchema]);

  // Поля, доступные для маппинга: для скалярного режима — поля документа,
  // для табличного — поля составного типа элемента массива.
  const effectiveFields = useMemo(() => {
    if (targetFieldKey === null) return schemaFields;
    const arrayField = arrayFields.find(f => f.key === targetFieldKey);
    if (!arrayField?.typeId) return [];
    const compositeType = allDocTypes.find(dt => dt.id === arrayField.typeId);
    if (!compositeType) return [];
    return resolveEffectiveFields(compositeType, allDocTypes);
  }, [targetFieldKey, arrayFields, allDocTypes, schemaFields]);

  const scalarMappable = effectiveFields.filter(isScalarField);
  // Составные поля заполняются ссылкой на запись каталога (по значению колонки).
  const complexMappable = effectiveFields.filter(f => f.type === 'complex' && f.typeId);

  function matchFieldsFor(typeId: string): SchemaField[] {
    const ct = allDocTypes.find(dt => dt.id === typeId);
    if (!ct) return [];
    return resolveEffectiveFields(ct, allDocTypes).filter(isScalarField);
  }

  function setTarget(t: string) {
    // При смене цели сбрасываем маппинг
    onChange({}, t === '' ? null : t);
  }
  function setCol(fieldKey: string, col: string) {
    const next = { ...mapping };
    if (col) next[fieldKey] = col;
    else delete next[fieldKey];
    onChange(next, targetFieldKey);
  }
  function setRef(f: SchemaField, column: string, match: string) {
    const next = { ...mapping };
    if (column) next[f.key] = buildRefMapping({ column, match, typeId: f.typeId! });
    else delete next[f.key];
    onChange(next, targetFieldKey);
  }

  return (
    <div className="space-y-3 text-sm">
      <div>
        <label className="block text-xs font-medium mb-1 text-fg3">
          Режим использования
        </label>
        <select
          value={targetFieldKey ?? ''}
          onChange={e => setTarget(e.target.value)}
          className="w-full border border-stroke rounded-md px-2 py-1.5 text-sm bg-surface text-fg1"
        >
          <option value="">Скалярный — первая строка заполняет отдельные поля</option>
          {arrayFields.map(f => (
            <option key={f.key} value={f.key}>Табличный → {f.title} ({f.key})</option>
          ))}
        </select>
      </div>

      <div>
        <label className="block text-xs font-medium mb-1 text-fg3">
          Маппинг колонок файла → поля
          {targetFieldKey && <span className="ml-1 font-normal text-fg4">(поля «{arrayFields.find(f => f.key === targetFieldKey)?.title ?? targetFieldKey}»)</span>}
        </label>
        <div className="space-y-1.5">
          {scalarMappable.map(f => (
            <div key={f.key} className="flex items-center gap-2">
              <span className="w-40 text-xs truncate shrink-0 text-fg2" title={`${f.title} (${f.key})`}>
                {f.title}
              </span>
              <select
                value={mapping[f.key] ?? ''}
                onChange={e => setCol(f.key, e.target.value)}
                className="flex-1 border border-stroke rounded px-2 py-1 text-xs bg-surface text-fg1"
              >
                <option value="">— не привязано —</option>
                {columnNames.map(c => <option key={c} value={c}>{c}</option>)}
              </select>
            </div>
          ))}
          {complexMappable.map(f => {
            const refMap = parseRefMapping(mapping[f.key]);
            const matchFields = matchFieldsFor(f.typeId!);
            return (
              <div key={f.key} className="flex items-center gap-2">
                <span className="w-40 text-xs truncate shrink-0 text-fg2" title={`${f.title} (${f.key}) — ссылка на каталог`}>
                  {f.title} <span className="text-fg4">↗</span>
                </span>
                <select
                  value={refMap?.column ?? ''}
                  onChange={e => setRef(f, e.target.value, refMap?.match ?? '')}
                  className="flex-1 border border-stroke rounded px-2 py-1 text-xs bg-surface text-fg1"
                  title="Колонка со значением для поиска записи в каталоге"
                >
                  <option value="">— не привязано —</option>
                  {columnNames.map(c => <option key={c} value={c}>{c}</option>)}
                </select>
                <select
                  value={refMap?.match ?? ''}
                  onChange={e => setRef(f, refMap?.column ?? '', e.target.value)}
                  disabled={!refMap?.column}
                  className="w-32 border border-stroke rounded px-2 py-1 text-xs bg-surface text-fg1 disabled:opacity-50"
                  title="Поле записи каталога для сопоставления"
                >
                  <option value="">по названию</option>
                  {matchFields.map(mf => <option key={mf.key} value={mf.key}>{mf.title}</option>)}
                </select>
              </div>
            );
          })}
        </div>
        {scalarMappable.length === 0 && complexMappable.length === 0 && (
          <p className="text-xs text-fg4">Нет доступных полей для маппинга</p>
        )}
      </div>
    </div>
  );
}

function AddBindingPanel({
  instanceId,
  setId,
  schemaFields,
  allDocTypes,
  onDone,
}: {
  instanceId: string;
  setId: string;
  schemaFields: SchemaField[];
  allDocTypes: DocumentType[];
  onDone: () => void;
}) {
  const { data: availableFiles = [], isLoading: filesLoading } = useAvailableDataSetFiles(setId);

  const allSources = useMemo(() => {
    return availableFiles.flatMap(f => f.sources.map(s => ({ ...s, file: f })));
  }, [availableFiles]);

  const [sourceId, setSourceId] = useState('');
  const [mapping, setMappingState] = useState<Record<string, string>>({});
  const [targetFieldKey, setTargetFieldKey] = useState<string | null>(null);
  const [error, setError] = useState('');

  const autoMap = useAutoMapDataSetSource();
  const create = useCreateDataSetBinding();

  const selectedSource = allSources.find(s => s.id === sourceId);
  const arrayFields = schemaFields.filter(f => f.type === 'array');
  const scalarFields = schemaFields.filter(isScalarField);

  async function handleSourceChange(id: string) {
    setSourceId(id);
    setMappingState({});
    setTargetFieldKey(null);
    if (!id) return;
    try {
      const { mapping: m } = await autoMap.mutateAsync({
        sourceId: id,
        fields: scalarFields.map(f => ({ key: f.key, title: f.title })),
      });
      setMappingState(m);
    } catch { /* авто-маппинг необязателен */ }
  }

  async function handleSave() {
    if (!sourceId) { setError('Выберите источник'); return; }
    setError('');
    try {
      await create.mutateAsync({ instanceId, sourceId, targetFieldKey, mapping });
      onDone();
    } catch (err: unknown) {
      setError(err instanceof Error ? err.message : 'Ошибка');
    }
  }

  return (
    <div className="rounded-xl p-4 space-y-4 border border-stroke bg-base">
      <div>
        <label className="block text-xs font-medium mb-1 text-fg3">
          Источник данных
        </label>
        {filesLoading ? (
          <p className="text-xs py-1 text-fg4">Загрузка...</p>
        ) : availableFiles.length === 0 ? (
          <p className="text-xs py-1 text-fg4">
            Нет загруженных наборов данных. Загрузите файлы на странице «Наборы данных» или в панели комплекта/раздела/стройки.
          </p>
        ) : (
          <select
            value={sourceId}
            onChange={e => handleSourceChange(e.target.value)}
            className="w-full border border-stroke rounded-md px-2 py-1.5 text-sm bg-surface text-fg1"
          >
            <option value="">— выберите источник —</option>
            {availableFiles.map(f => (
              <optgroup key={f.id} label={`[${SCOPE_LABELS[f.scope]}] ${f.name} (${DATA_SET_FORMAT_LABELS[f.format]})`}>
                {f.sources.map(s => (
                  <option key={s.id} value={s.id}>{s.name} · {s.cachedRowCount} строк</option>
                ))}
              </optgroup>
            ))}
          </select>
        )}
      </div>

      {selectedSource && (
        <MappingEditor
          source={selectedSource}
          schemaFields={schemaFields}
          arrayFields={arrayFields}
          allDocTypes={allDocTypes}
          mapping={mapping}
          targetFieldKey={targetFieldKey}
          onChange={(m, t) => { setMappingState(m); setTargetFieldKey(t); }}
        />
      )}

      {error && <p className="text-xs text-danger">{error}</p>}

      <div className="flex gap-2">
        <button
          onClick={handleSave}
          disabled={!sourceId || create.isPending}
          className="px-3 py-1.5 rounded-md text-sm font-medium text-white disabled:opacity-40 bg-brand"
        >
          {create.isPending ? 'Сохранение...' : 'Сохранить привязку'}
        </button>
        <button
          onClick={onDone}
          className="px-3 py-1.5 rounded-md text-sm font-medium text-fg2 bg-muted"
        >
          Отмена
        </button>
      </div>
    </div>
  );
}

function BindingRow({
  binding,
  schemaFields,
  allDocTypes,
  instanceId,
}: {
  binding: DataSetBinding;
  schemaFields: SchemaField[];
  allDocTypes: DocumentType[];
  instanceId: string;
}) {
  const [editing, setEditing] = useState(false);
  const [mapping, setMappingState] = useState<Record<string, string>>(binding.mapping ?? {});
  const [targetFieldKey, setTargetFieldKey] = useState<string | null>(binding.targetFieldKey);
  const [rowFilter, setRowFilter] = useState<RowFilterDef | null>(binding.rowFilter ?? null);
  const [computedColumns, setComputedColumns] = useState<ComputedColumn[] | null>(binding.computedColumns ?? null);
  const [confirming, setConfirming] = useState(false);
  const [filterOpen, setFilterOpen] = useState(false);
  const [transformsOpen, setTransformsOpen] = useState(false);

  const update = useUpdateDataSetBinding();
  const del = useDeleteDataSetBinding();
  const autoMap = useAutoMapDataSetSource();

  const source = binding.source;
  const file = source?.file;
  const arrayFields = schemaFields.filter(f => f.type === 'array');
  const scalarFields = schemaFields.filter(isScalarField);

  // Available column names from source schema (for filter dialog)
  const sourceColumns = useMemo(() => parseSourceColumnNames(source?.cachedSchema), [source?.cachedSchema]);

  async function handleAutoRemap() {
    if (!source) return;
    const { mapping: m } = await autoMap.mutateAsync({
      sourceId: source.id,
      fields: scalarFields.map(f => ({ key: f.key, title: f.title })),
    });
    setMappingState(m);
  }

  async function handleSave() {
    await update.mutateAsync({ id: binding.id, instanceId, targetFieldKey, mapping, rowFilter, computedColumns });
    setEditing(false);
  }

  async function handleSaveFilter(f: RowFilterDef | null) {
    setRowFilter(f);
    await update.mutateAsync({ id: binding.id, instanceId, targetFieldKey, mapping, rowFilter: f, computedColumns });
  }

  async function handleSaveTransforms(cols: ComputedColumn[] | null) {
    setComputedColumns(cols);
    await update.mutateAsync({ id: binding.id, instanceId, targetFieldKey, mapping, rowFilter, computedColumns: cols });
  }

  async function handleDelete() {
    await del.mutateAsync({ id: binding.id, instanceId });
  }

  const mappedCount = Object.keys(mapping).filter(k => mapping[k]).length;
  const filterCount = countFilterConditions(rowFilter);
  const transformCount = computedColumns?.length ?? 0;

  return (
    <div className="border-b border-stroke last:border-0">
      <div className="flex items-center gap-2 px-4 py-3">
        <Database size={14} className="text-brand shrink-0" />
        <div className="flex-1 min-w-0">
          <div className="text-sm font-medium truncate text-fg1">
            {source?.name ?? '—'}
          </div>
          <div className="flex items-center gap-2 mt-0.5 flex-wrap">
            <span className="text-xs text-fg4">
              {file?.name} · {mappedCount} пол{mappedCount === 1 ? 'е' : 'я'} привязано
              {targetFieldKey && ` · таблица: ${targetFieldKey}`}
            </span>
            {filterCount > 0 && (
              <span className="flex items-center gap-1 text-[10px] px-1.5 py-0.5 rounded-full bg-brand-subtle text-brand">
                <Filter size={9} /> {filterCount}
              </span>
            )}
            {transformCount > 0 && (
              <span className="flex items-center gap-1 text-[10px] px-1.5 py-0.5 rounded-full bg-brand-subtle text-brand">
                <FunctionSquare size={9} /> {transformCount}
              </span>
            )}
          </div>
        </div>
        {/* Filter button */}
        <button
          onClick={() => setFilterOpen(true)}
          className={`p-1.5 rounded ${filterCount > 0 ? 'text-brand' : 'text-fg4'}`}
          title="Фильтрация строк"
        >
          <Filter size={13} />
        </button>
        {/* Transforms button */}
        <button
          onClick={() => setTransformsOpen(true)}
          className={`p-1.5 rounded ${transformCount > 0 ? 'text-brand' : 'text-fg4'}`}
          title="Вычисляемые колонки"
        >
          <FunctionSquare size={13} />
        </button>
        <button
          onClick={() => setEditing(e => !e)}
          className="p-1.5 rounded text-xs text-fg3"
          title="Редактировать маппинг"
        >
          <Pencil size={13} />
        </button>
        {!confirming ? (
          <button
            onClick={() => setConfirming(true)}
            className="p-1.5 rounded text-fg4 hover:text-danger transition-colors"
            title="Удалить"
          >
            <Trash2 size={13} />
          </button>
        ) : (
          <div className="flex items-center gap-1.5 text-xs">
            <button onClick={handleDelete} disabled={del.isPending}
              className="px-2 py-0.5 rounded text-white text-xs bg-danger">
              Да
            </button>
            <button onClick={() => setConfirming(false)}
              className="px-2 py-0.5 rounded text-xs bg-muted text-fg2">
              Нет
            </button>
          </div>
        )}
      </div>

      {editing && source && (
        <div className="px-4 pb-4 space-y-3">
          <MappingEditor
            source={source}
            schemaFields={schemaFields}
            arrayFields={arrayFields}
            allDocTypes={allDocTypes}
            mapping={mapping}
            targetFieldKey={targetFieldKey}
            onChange={(m, t) => { setMappingState(m); setTargetFieldKey(t); }}
          />
          <div className="flex gap-2">
            <button onClick={handleSave} disabled={update.isPending}
              className="px-3 py-1.5 rounded-md text-sm font-medium text-white bg-brand">
              {update.isPending ? 'Сохранение...' : 'Сохранить'}
            </button>
            <button onClick={handleAutoRemap} disabled={autoMap.isPending}
              className="px-3 py-1.5 rounded-md text-sm font-medium text-fg2 bg-muted">
              Авто-маппинг
            </button>
            <button onClick={() => setEditing(false)}
              className="px-3 py-1.5 rounded-md text-sm font-medium text-fg3">
              Отмена
            </button>
          </div>
        </div>
      )}

      {filterOpen && (
        <RowFilterDialog
          columns={sourceColumns}
          initial={rowFilter}
          onSave={handleSaveFilter}
          onClose={() => setFilterOpen(false)}
        />
      )}
      {transformsOpen && (
        <ComputedColumnsDialog
          initial={computedColumns}
          onSave={handleSaveTransforms}
          onClose={() => setTransformsOpen(false)}
        />
      )}
    </div>
  );
}

function PreviewPanel({ results }: { results: DataSetBindingPreviewResult[] }) {
  if (results.length === 0)
    return <p className="text-xs py-2 text-fg4">Нет привязок для проверки</p>;

  return (
    <div className="space-y-3">
      {results.map(r => (
        <div key={r.bindingId} className="rounded-lg overflow-hidden border border-stroke">
          {/* Header */}
          <div className="flex items-center gap-2 px-3 py-2 bg-base">
            {r.error
              ? <AlertCircle size={13} className="text-danger shrink-0" />
              : <CheckCircle2 size={13} className="text-success shrink-0" />
            }
            <span className="text-xs font-medium flex-1 text-fg1">
              {r.sourceName}
              <span className="font-normal ml-1.5 text-fg4">
                {r.fileName} · {r.mode === 'scalar' ? 'скалярный' : r.mode === 'tabular' ? `табличный → ${r.targetFieldKey}` : 'ошибка'}
              </span>
            </span>
            {r.mode !== 'error' && (
              <span className="text-xs text-fg4">{r.totalRows} строк</span>
            )}
          </div>

          {/* Body */}
          {r.error ? (
            <div className="px-3 py-2 text-xs text-danger bg-surface">
              {r.error}
            </div>
          ) : r.mode === 'scalar' ? (
            <div className="px-3 py-2 overflow-x-auto bg-surface">
              <table className="text-xs w-full">
                <tbody>
                  {Object.entries(r.data as Record<string, string | null>).map(([k, v]) => (
                    <tr key={k} className="border-b border-stroke last:border-0">
                      <td className="py-1 pr-4 font-medium w-1/3 text-fg3">{k}</td>
                      <td className={`py-1 ${v == null ? 'text-fg4' : 'text-fg1'}`}>
                        {v ?? <em>null</em>}
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          ) : (
            // Tabular — show first 5 rows
            <div className="overflow-x-auto bg-surface">
              {(() => {
                const rows = r.data as Record<string, string | null>[];
                const preview = rows.slice(0, 5);
                const keys = preview.length > 0 ? Object.keys(preview[0]) : [];
                if (keys.length === 0) return (
                  <p className="px-3 py-2 text-xs text-fg4">Нет данных</p>
                );
                return (
                  <table className="text-xs w-full">
                    <thead>
                      <tr className="bg-base">
                        {keys.map(k => (
                          <th key={k} className="px-3 py-1.5 text-left font-medium whitespace-nowrap text-fg3">{k}</th>
                        ))}
                      </tr>
                    </thead>
                    <tbody>
                      {preview.map((row, i) => (
                        <tr key={i} className="border-t border-stroke">
                          {keys.map(k => (
                            <td key={k} className={`px-3 py-1.5 whitespace-nowrap ${row[k] == null ? 'text-fg4' : 'text-fg1'}`}>
                              {row[k] ?? <em>null</em>}
                            </td>
                          ))}
                        </tr>
                      ))}
                    </tbody>
                  </table>
                );
              })()}
              {(r.data as Record<string, string | null>[]).length > 5 && (
                <p className="px-3 py-1.5 text-xs border-t border-stroke text-fg4">
                  +{(r.data as Record<string, string | null>[]).length - 5} строк не показано
                </p>
              )}
            </div>
          )}
        </div>
      ))}
    </div>
  );
}

export function DataSetsTab({ instance, setId, schemaFields, allDocTypes, docType }: {
  instance: DocumentInstance; setId: string; schemaFields: SchemaField[];
  allDocTypes: DocumentType[]; docType: DocumentType | undefined;
}) {
  const { data: bindings = [], isLoading } = useListDataSetBindings(instance.id);
  const [adding, setAdding] = useState(false);
  const [applyingTemplate, setApplyingTemplate] = useState(false);
  const [showPreview, setShowPreview] = useState(false);
  const { data: previewResults, isFetching: previewing, refetch: runPreview, error: previewError } = usePreviewDataSetBindings(instance.id);

  async function handlePreview() {
    setShowPreview(true);
    await runPreview();
  }

  if (isLoading) return <div className="py-6 text-center text-sm text-fg4">Загрузка...</div>;

  return (
    <div className="space-y-4">
      <div className="rounded-xl overflow-hidden border border-stroke bg-surface">
        {bindings.length === 0 && !adding ? (
          <div className="p-6 text-center text-sm text-fg4">
            Нет привязок к наборам данных
          </div>
        ) : (
          <div>
            {bindings.map(b => (
              <BindingRow
                key={b.id}
                binding={b}
                schemaFields={schemaFields}
                allDocTypes={allDocTypes}
                instanceId={instance.id}
              />
            ))}
          </div>
        )}
      </div>

      {adding ? (
        <AddBindingPanel
          instanceId={instance.id}
          setId={setId}
          schemaFields={schemaFields}
          allDocTypes={allDocTypes}
          onDone={() => setAdding(false)}
        />
      ) : (
        <div className="flex items-center gap-3 flex-wrap">
          <button
            onClick={() => setAdding(true)}
            className="flex items-center gap-2 text-sm font-medium px-3 py-2 rounded-md transition-colors text-brand bg-brand-subtle"
          >
            <Plus size={14} /> Добавить источник данных
          </button>
          <button
            onClick={() => setApplyingTemplate(true)}
            className="flex items-center gap-2 text-sm font-medium px-3 py-2 rounded-md transition-colors text-fg2 bg-muted"
          >
            <LayoutTemplate size={14} /> Из шаблона
          </button>
          {bindings.length > 0 && (
            <button
              onClick={handlePreview}
              disabled={previewing}
              className="flex items-center gap-2 text-sm font-medium px-3 py-2 rounded-md transition-colors disabled:opacity-50 text-fg2 bg-muted"
            >
              {previewing
                ? <Loader2 size={14} className="animate-spin" />
                : <PlayCircle size={14} />
              }
              {previewing ? 'Проверка...' : 'Проверить данные'}
            </button>
          )}
        </div>
      )}

      {showPreview && (
        <div className="space-y-2">
          <div className="flex items-center justify-between">
            <span className="text-xs font-semibold uppercase tracking-wide text-fg3">
              Результат проверки
            </span>
            <button
              onClick={() => setShowPreview(false)}
              className="text-xs text-fg4"
            >
              Скрыть
            </button>
          </div>
          {previewing ? (
            <div className="py-4 text-center text-sm text-fg4">
              <Loader2 size={16} className="inline-block animate-spin mr-2" />
              Загрузка и разбор файлов...
            </div>
          ) : previewError ? (
            <p className="text-xs text-danger">
              {previewError instanceof Error ? previewError.message : 'Ошибка проверки'}
            </p>
          ) : previewResults ? (
            <PreviewPanel results={previewResults} />
          ) : null}
        </div>
      )}

      <p className="text-xs text-fg4">
        Данные загружаются из файлов (System или Комплект) и подставляются при каждой генерации.
        Управление файлами — в разделе «Наборы данных».
      </p>

      {applyingTemplate && (
        <ApplyTemplateDialog
          instanceId={instance.id}
          setId={setId}
          docType={docType}
          onDone={() => setApplyingTemplate(false)}
          onClose={() => setApplyingTemplate(false)}
        />
      )}
    </div>
  );
}
