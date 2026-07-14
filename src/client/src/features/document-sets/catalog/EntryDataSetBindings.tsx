import { useMemo, useState } from 'react';
import { Database, Pencil, Trash2, Plus } from 'lucide-react';
import { IconButton } from '@/shared/ui/Button';
import {
  useListDataSetFiles, useAvailableDataSetFiles,
  useCreateDataSetBinding, useUpdateDataSetBinding, useDeleteDataSetBinding, useAutoMapDataSetSource,
} from '@/shared/api/datasets';
import { MappingEditor } from '@/features/document-sets/editor/DataSetsTab';
import type { CatalogScope, DataSetBinding, DataSetFile, DocumentType } from '@/shared/api/types';
import { DATA_SET_FORMAT_LABELS, SCOPE_LABELS } from '@/shared/api/types';
import { isScalarField, type SchemaField } from '@/shared/api/schema';

/// Записи каталога не всегда живут внутри комплекта (System/Section/Construction-скоуп
/// без setId) — для них берём файлы напрямую по scope/scopeId + System, тем же паттерном,
/// что уже используется в этом файле для parentEntries (см. CatalogEntryForm).
function useEntryAvailableFiles(setId: string | undefined, scope: CatalogScope, scopeId: string | null) {
  const chain = useAvailableDataSetFiles(setId ?? '');
  const own = useListDataSetFiles(scope, scopeId ?? undefined);
  const system = useListDataSetFiles('System');
  if (setId) return { data: chain.data ?? [], isLoading: chain.isLoading };
  const merged: DataSetFile[] = scope === 'System'
    ? (system.data ?? [])
    : [...(own.data ?? []), ...(system.data ?? []).filter(f => !(own.data ?? []).some(o => o.id === f.id))];
  return { data: merged, isLoading: own.isLoading || system.isLoading };
}

function AddEntryBindingPanel({
  entryId, files, schemaFields, allDocTypes, onDone,
}: {
  entryId: string; files: DataSetFile[]; schemaFields: SchemaField[]; allDocTypes: DocumentType[];
  onDone: () => void;
}) {
  const allSources = useMemo(() => files.flatMap(f => f.sources.map(s => ({ ...s, file: f }))), [files]);
  const [sourceId, setSourceId] = useState('');
  const [mapping, setMappingState] = useState<Record<string, string>>({});
  const [targetFieldKey, setTargetFieldKey] = useState<string | null>(null);
  const [error, setError] = useState('');

  const autoMap = useAutoMapDataSetSource();
  const create = useCreateDataSetBinding();

  const selectedSource = allSources.find(s => s.id === sourceId);
  const tabularFields = schemaFields.filter(f => f.type === 'array' || f.type === 'doc-array');
  const scalarFields = schemaFields.filter(f => isScalarField(f) && f.type !== 'file');

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
      await create.mutateAsync({ ownerId: entryId, sourceId, targetFieldKey, mapping });
      onDone();
    } catch (err: unknown) {
      setError(err instanceof Error ? err.message : 'Ошибка');
    }
  }

  return (
    <div className="rounded-xl p-4 space-y-4 border border-stroke bg-base">
      <div>
        <label className="block text-xs font-medium mb-1 text-fg3">Источник данных</label>
        {files.length === 0 ? (
          <p className="text-xs py-1 text-fg4">
            Нет загруженных наборов данных. Загрузите файлы на странице «Наборы данных».
          </p>
        ) : (
          <select
            value={sourceId}
            onChange={e => handleSourceChange(e.target.value)}
            className="w-full border border-stroke rounded-md px-2 py-1.5 text-sm bg-surface text-fg1"
          >
            <option value="">— выберите источник —</option>
            {files.map(f => (
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
          tabularFields={tabularFields}
          allDocTypes={allDocTypes}
          mapping={mapping}
          targetFieldKey={targetFieldKey}
          onChange={(m, t) => { setMappingState(m); setTargetFieldKey(t); }}
        />
      )}

      {error && <p className="text-xs text-danger">{error}</p>}

      <div className="flex gap-2">
        <button
          type="button"
          onClick={handleSave}
          disabled={!sourceId || create.isPending}
          className="px-3 py-1.5 rounded-md text-sm font-medium text-white disabled:opacity-40 bg-brand"
        >
          {create.isPending ? 'Сохранение...' : 'Сохранить привязку'}
        </button>
        <button type="button" onClick={onDone} className="px-3 py-1.5 rounded-md text-sm font-medium text-fg2 bg-muted">
          Отмена
        </button>
      </div>
    </div>
  );
}

function EntryBindingRow({
  binding, entryId, schemaFields, allDocTypes,
}: {
  binding: DataSetBinding; entryId: string; schemaFields: SchemaField[]; allDocTypes: DocumentType[];
}) {
  const [editing, setEditing] = useState(false);
  const [mapping, setMappingState] = useState<Record<string, string>>(binding.mapping ?? {});
  const [targetFieldKey, setTargetFieldKey] = useState<string | null>(binding.targetFieldKey);
  const [confirming, setConfirming] = useState(false);

  const update = useUpdateDataSetBinding();
  const del = useDeleteDataSetBinding();

  const source = binding.source;
  const file = source?.file;
  const tabularFields = schemaFields.filter(f => f.type === 'array' || f.type === 'doc-array');
  const mappedCount = Object.keys(mapping).filter(k => mapping[k]).length;

  async function handleSave() {
    await update.mutateAsync({ id: binding.id, ownerId: entryId, targetFieldKey, mapping });
    setEditing(false);
  }
  async function handleDelete() {
    await del.mutateAsync({ id: binding.id, ownerId: entryId });
  }

  return (
    <div className="border-b border-stroke last:border-0">
      <div className="flex items-center gap-2 px-4 py-3">
        <Database size={14} className="text-brand shrink-0" />
        <div className="flex-1 min-w-0">
          <div className="text-sm font-medium truncate text-fg1">{source?.name ?? '—'}</div>
          <div className="text-xs text-fg4 mt-0.5">
            {file?.name} · {source?.materializeTypeId
              ? `материализация → ${allDocTypes.find(t => t.id === source.materializeTypeId)?.name ?? 'тип'}`
              : `${mappedCount} пол${mappedCount === 1 ? 'е' : 'я'} привязано`}
            {targetFieldKey && ` · таблица: ${targetFieldKey}`}
          </div>
        </div>
        <IconButton label="Редактировать маппинг" size="sm" onClick={() => setEditing(e => !e)}>
          <Pencil size={13} />
        </IconButton>
        {!confirming ? (
          <IconButton label="Удалить" size="sm" danger onClick={() => setConfirming(true)}>
            <Trash2 size={13} />
          </IconButton>
        ) : (
          <div className="flex items-center gap-1.5 text-xs">
            <button type="button" onClick={handleDelete} disabled={del.isPending} className="px-2 py-0.5 rounded text-white text-xs bg-danger">Да</button>
            <button type="button" onClick={() => setConfirming(false)} className="px-2 py-0.5 rounded text-xs bg-muted text-fg2">Нет</button>
          </div>
        )}
      </div>

      {editing && source && (
        <div className="px-4 pb-4 space-y-3">
          <MappingEditor
            source={source}
            schemaFields={schemaFields}
            tabularFields={tabularFields}
            allDocTypes={allDocTypes}
            mapping={mapping}
            targetFieldKey={targetFieldKey}
            onChange={(m, t) => { setMappingState(m); setTargetFieldKey(t); }}
          />
          <div className="flex gap-2">
            <button type="button" onClick={handleSave} disabled={update.isPending} className="px-3 py-1.5 rounded-md text-sm font-medium text-white bg-brand">
              {update.isPending ? 'Сохранение...' : 'Сохранить'}
            </button>
            <button type="button" onClick={() => setEditing(false)} className="px-3 py-1.5 rounded-md text-sm font-medium text-fg3">
              Отмена
            </button>
          </div>
        </div>
      )}
    </div>
  );
}

export function EntryDataSetBindings({
  entryId, bindings, schemaFields, allDocTypes, setId, scope, scopeId,
}: {
  entryId: string; bindings: DataSetBinding[]; schemaFields: SchemaField[]; allDocTypes: DocumentType[];
  setId?: string; scope: CatalogScope; scopeId: string | null;
}) {
  const [adding, setAdding] = useState(false);
  const { data: files } = useEntryAvailableFiles(setId, scope, scopeId);

  return (
    <div className="rounded-lg border border-stroke p-3 space-y-2">
      <p className="text-xs font-semibold text-fg3 uppercase tracking-wide">Наборы данных</p>

      {bindings.length > 0 && (
        <div className="rounded-xl overflow-hidden border border-stroke bg-surface">
          {bindings.map(b => (
            <EntryBindingRow key={b.id} binding={b} entryId={entryId} schemaFields={schemaFields} allDocTypes={allDocTypes} />
          ))}
        </div>
      )}

      {adding ? (
        <AddEntryBindingPanel
          entryId={entryId} files={files} schemaFields={schemaFields} allDocTypes={allDocTypes}
          onDone={() => setAdding(false)}
        />
      ) : (
        <button type="button" onClick={() => setAdding(true)}
          className="flex items-center gap-1.5 text-sm text-brand hover:text-brand-hover transition-colors">
          <Plus size={14} /> Привязать источник данных
        </button>
      )}
    </div>
  );
}
