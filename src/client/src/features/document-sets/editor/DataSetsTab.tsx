import { useState, useMemo } from 'react';
import { Database, Pencil, Trash2, Plus, LayoutTemplate, PlayCircle, Loader2, AlertCircle, CheckCircle2 } from 'lucide-react';
import { dtTable, dtTh, dtTd, dtRow } from '@/shared/ui/dataTable';
import { ApplyTemplateDialog } from './ApplyTemplateDialog';
import {
  useAvailableDataSetFiles, useListDataSetBindings,
  useCreateDataSetBinding, useUpdateDataSetBinding, useDeleteDataSetBinding,
  useAutoMapDataSetSource, usePreviewDataSetBindings,
} from '@/shared/api/datasets';
import type { DocumentInstance, DocumentType, DataSetSource, DataSetBinding, DataSetBindingPreviewResult } from '@/shared/api/types';
import { DATA_SET_FORMAT_LABELS, SCOPE_LABELS } from '@/shared/api/types';
import { resolveEffectiveFields, isScalarField, type SchemaField } from '@/shared/api/schema';
import { parseSourceColumnNames, parseRefMapping, buildRefMappingByName, buildRefMappingByIdentity, parseFileMapping, buildFileMapping } from '@/shared/api/datasetHelpers';
import { FUNCTIONAL_TAG } from '@/shared/api/tags';
import { isFileAttachment, formatBytes } from '@/shared/api/attachments';
/** Совместимость по наследованию: childId == ancestorId либо childId — потомок ancestorId по parentId. */
function isSameOrDescendant(childId: string, ancestorId: string, allDocTypes: DocumentType[]): boolean {
  let cur: string | null = childId;
  let guard = 0;
  while (cur && guard++ < 32) {
    if (cur === ancestorId) return true;
    cur = allDocTypes.find(t => t.id === cur)?.parentId ?? null;
  }
  return false;
}

/** Типы полей, куда можно направить материализованный источник (составная сущность/массив/ссылка). */
const MATERIALIZABLE_FIELD_TYPES: readonly string[] = ['array', 'doc-array', 'complex', 'doc-ref'];

export function MappingEditor({
  source,
  schemaFields,
  tabularFields,
  allDocTypes,
  mapping,
  targetFieldKey,
  onChange,
  hideModeSelector = false,
}: {
  source: DataSetSource;
  schemaFields: SchemaField[];
  tabularFields: SchemaField[];
  allDocTypes: DocumentType[];
  mapping: Record<string, string>;
  targetFieldKey: string | null;
  onChange: (m: Record<string, string>, t: string | null) => void;
  /** Скрыть селектор «Режим использования» (для материализации на источнике — режима нет). */
  hideModeSelector?: boolean;
}) {
  // Вычисляемые колонки (Transformation) не персистятся в cachedSchema — доступны для маппинга только
  // если их алиасы явно домешать (issue #49; тот же паттерн, что в SourcesExpander.tsx).
  const columnNames = useMemo(() => {
    const computedAliases = (source.computedColumns ?? []).map(c => c.alias).filter(Boolean);
    return [...new Set([...parseSourceColumnNames(source.cachedSchema), ...computedAliases])];
  }, [source.cachedSchema, source.computedColumns]);

  // Поля, доступные для маппинга: для скалярного режима — поля документа,
  // для табличного — поля типа элемента (составной тип для `array`;
  // тип-документ, на который ссылается `doc-array`, — строки источника
  // разворачиваются в объекты его формы, см. DataSetResolver).
  const effectiveFields = useMemo(() => {
    if (targetFieldKey === null) return schemaFields;
    const tabularField = tabularFields.find(f => f.key === targetFieldKey);
    if (!tabularField?.typeId) return [];
    const elementType = allDocTypes.find(dt => dt.id === tabularField.typeId);
    if (!elementType) return [];
    return resolveEffectiveFields(elementType, allDocTypes);
  }, [targetFieldKey, tabularFields, allDocTypes, schemaFields]);

  // UI-режим резолва ref-поля (по имени/по идентификатору) — держим отдельно, т.к. пустой маппинг
  // (ещё не выбраны колонки) не отличает режимы; при загрузке выводится из самого маппинга.
  const [refMode, setRefMode] = useState<Record<string, 'name' | 'identity'>>({});

  const scalarMappable = effectiveFields.filter(f => isScalarField(f) && f.type !== 'file');
  // Составные поля заполняются ссылкой на запись каталога (по значению колонки).
  const complexMappable = effectiveFields.filter(f => f.type === 'complex' && f.typeId);
  // Файловые поля заполняются вложением, синтезированным из колонки-пути (+ опц. колонка-размер) той же строки.
  const fileMappable = effectiveFields.filter(f => f.type === 'file');

  // Identity-поля типа-цели (тэг `identity`, в порядке схемы) — по ним резолвится составной ключ (#243).
  function identityFieldsFor(typeId: string): SchemaField[] {
    const ct = allDocTypes.find(dt => dt.id === typeId);
    if (!ct) return [];
    return resolveEffectiveFields(ct, allDocTypes)
      .filter(f => isScalarField(f) && f.tags?.includes(FUNCTIONAL_TAG.identity));
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
  // Резолв ref-поля по имени: одна колонка.
  function setRefName(f: SchemaField, column: string) {
    const next = { ...mapping };
    if (column) next[f.key] = buildRefMappingByName(f.typeId!, column);
    else delete next[f.key];
    onChange(next, targetFieldKey);
  }
  // Резолв ref-поля по идентификатору: колонка на одно identity-поле (частичный маппинг допустим,
  // пустые компоненты просто не дадут матча на бэке — строгий composite-ключ).
  function setRefIdentityCol(f: SchemaField, current: Record<string, string>, idKey: string, column: string) {
    const cols = { ...current };
    if (column) cols[idKey] = column; else delete cols[idKey];
    const next = { ...mapping };
    if (Object.keys(cols).length > 0) next[f.key] = buildRefMappingByIdentity(f.typeId!, cols);
    else delete next[f.key];
    onChange(next, targetFieldKey);
  }
  // Переключение режима ref-поля (по имени ↔ по идентификатору) — сбрасываем текущий маппинг поля;
  // режим держим в UI-стейте, т.к. пустой маппинг сам по себе не хранит выбор.
  function switchRefMode(f: SchemaField, mode: 'name' | 'identity') {
    setRefMode(prev => ({ ...prev, [f.key]: mode }));
    const next = { ...mapping };
    delete next[f.key];
    onChange(next, targetFieldKey);
  }
  function setFile(f: SchemaField, column: string, sizeColumn: string) {
    const next = { ...mapping };
    if (column) next[f.key] = buildFileMapping({ column, sizeColumn });
    else delete next[f.key];
    onChange(next, targetFieldKey);
  }

  return (
    <div className="space-y-3 text-sm">
      {!hideModeSelector && (
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
            {tabularFields.map(f => (
              <option key={f.key} value={f.key}>
                Табличный → {f.title} ({f.key}){f.type === 'doc-array' ? ' — документы' : ''}
              </option>
            ))}
          </select>
        </div>
      )}

      <div>
        <label className="block text-xs font-medium mb-1 text-fg3">
          Маппинг колонок файла → поля
          {targetFieldKey && <span className="ml-1 font-normal text-fg4">(поля «{tabularFields.find(f => f.key === targetFieldKey)?.title ?? targetFieldKey}»)</span>}
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
            const idFields = identityFieldsFor(f.typeId!);
            const hasIdentity = idFields.length > 0;
            // Режим: явный UI-выбор → иначе из маппинга (identityColumns → identity, иначе name).
            const mode = refMode[f.key] ?? (refMap?.identityColumns ? 'identity' : 'name');
            const idCols = refMap?.identityColumns ?? {};
            return (
              <div key={f.key} className="space-y-1">
                <div className="flex items-center gap-2">
                  <span className="w-40 text-xs truncate shrink-0 text-fg2" title={`${f.title} (${f.key}) — ссылка на каталог`}>
                    {f.title} <span className="text-fg4">↗</span>
                  </span>
                  {/* Колонка — в общей позиции (как у остальных строк); режим резолва — справа. */}
                  {mode === 'name' ? (
                    <select value={refMap?.identityColumns ? '' : (refMap?.column ?? '')}
                      onChange={e => setRefName(f, e.target.value)}
                      className="flex-1 border border-stroke rounded px-2 py-1 text-xs bg-surface text-fg1"
                      title="Колонка с именем записи каталога">
                      <option value="">— не привязано —</option>
                      {columnNames.map(c => <option key={c} value={c}>{c}</option>)}
                    </select>
                  ) : (
                    <span className="flex-1" />
                  )}
                  {hasIdentity ? (
                    <select value={mode} onChange={e => switchRefMode(f, e.target.value as 'name' | 'identity')}
                      className="w-36 shrink-0 border border-stroke rounded px-2 py-1 text-xs bg-surface text-fg1"
                      title="Как искать запись каталога">
                      <option value="name">по имени</option>
                      <option value="identity">по идентификатору</option>
                    </select>
                  ) : (
                    <span className="w-36 shrink-0 text-xs text-fg4 px-2 text-right" title="У типа нет полей-идентификаторов — только по имени">по имени</span>
                  )}
                </div>
                {mode === 'identity' && (
                  <div className="ml-40 pl-2 space-y-1 border-l border-stroke">
                    {idFields.map(idf => (
                      <div key={idf.key} className="flex items-center gap-2">
                        <span className="w-32 shrink-0 text-[11px] text-fg4 truncate" title={idf.key}>{idf.title}</span>
                        <select value={idCols[idf.key] ?? ''}
                          onChange={e => setRefIdentityCol(f, idCols, idf.key, e.target.value)}
                          className="flex-1 border border-stroke rounded px-2 py-1 text-xs bg-surface text-fg1"
                          title={`Колонка для identity-поля «${idf.title}»`}>
                          <option value="">— колонка —</option>
                          {columnNames.map(c => <option key={c} value={c}>{c}</option>)}
                        </select>
                      </div>
                    ))}
                  </div>
                )}
              </div>
            );
          })}
          {fileMappable.map(f => {
            const fileMap = parseFileMapping(mapping[f.key]);
            return (
              <div key={f.key} className="flex items-center gap-2">
                <span className="w-40 text-xs truncate shrink-0 text-fg2" title={`${f.title} (${f.key}) — файл-вложение`}>
                  {f.title} <span className="text-fg4">📎</span>
                </span>
                <select
                  value={fileMap?.column ?? ''}
                  onChange={e => setFile(f, e.target.value, fileMap?.sizeColumn ?? '')}
                  className="flex-1 border border-stroke rounded px-2 py-1 text-xs bg-surface text-fg1"
                  title="Колонка с путём к файлу (blob)"
                >
                  <option value="">— не привязано —</option>
                  {columnNames.map(c => <option key={c} value={c}>{c}</option>)}
                </select>
                <select
                  value={fileMap?.sizeColumn ?? ''}
                  onChange={e => setFile(f, fileMap?.column ?? '', e.target.value)}
                  disabled={!fileMap?.column}
                  className="w-32 border border-stroke rounded px-2 py-1 text-xs bg-surface text-fg1 disabled:opacity-50"
                  title="Колонка с размером файла в байтах (необязательно)"
                >
                  <option value="">без размера</option>
                  {columnNames.map(c => <option key={c} value={c}>{c}</option>)}
                </select>
              </div>
            );
          })}
        </div>
        {scalarMappable.length === 0 && complexMappable.length === 0 && fileMappable.length === 0 && (
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
  // Цели табличного режима: inline-массив (`array`) и список ссылок на документы
  // (`doc-array`) — строки источника разворачиваются в объекты формы элемента-типа.
  const tabularFields = schemaFields.filter(f => f.type === 'array' || f.type === 'doc-array');
  const scalarFields = schemaFields.filter(f => isScalarField(f) && f.type !== 'file');

  // Материализованный источник (issue #19): привязка — типизированный указатель без маппинга.
  // Поля-цели — те, чей тип совместим (тип источника == тип поля или его потомок).
  const materializeTypeId = selectedSource?.materializeTypeId ?? null;
  const compatibleFields = useMemo(() =>
    materializeTypeId
      ? schemaFields.filter(f => f.typeId && MATERIALIZABLE_FIELD_TYPES.includes(f.type)
          && isSameOrDescendant(materializeTypeId, f.typeId, allDocTypes))
      : [],
    [materializeTypeId, schemaFields, allDocTypes]);

  async function handleSourceChange(id: string) {
    setSourceId(id);
    setMappingState({});
    setTargetFieldKey(null);
    if (!id) return;
    // Материализованный источник маппинга не требует (тип↔тип) — авто-маппинг пропускаем.
    if (allSources.find(s => s.id === id)?.materializeTypeId) return;
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
    if (materializeTypeId && !targetFieldKey) { setError('Выберите поле-цель'); return; }
    setError('');
    try {
      // Материализованный источник — маппинг пустой (резолвер берёт маппинг с источника).
      await create.mutateAsync({ ownerId: instanceId, sourceId, targetFieldKey, mapping: materializeTypeId ? {} : mapping });
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
        materializeTypeId ? (
          <div className="rounded-lg border border-brand/40 bg-brand/5 p-3 space-y-2">
            <p className="text-xs text-fg3">
              Источник материализуется в тип <b>{allDocTypes.find(t => t.id === materializeTypeId)?.name ?? '—'}</b>.
              Маппинг задан на источнике — выберите только поле-цель совместимого типа.
            </p>
            <select
              value={targetFieldKey ?? ''}
              onChange={e => setTargetFieldKey(e.target.value || null)}
              className="w-full border border-stroke rounded-md px-2 py-1.5 text-sm bg-surface text-fg1"
            >
              <option value="">— выберите поле —</option>
              {compatibleFields.map(f => (
                <option key={f.key} value={f.key}>{f.title} ({f.key})</option>
              ))}
            </select>
            {compatibleFields.length === 0 && (
              <p className="text-xs text-warning">Нет полей документа, совместимых с типом источника.</p>
            )}
          </div>
        ) : (
          <MappingEditor
            source={selectedSource}
            schemaFields={schemaFields}
            tabularFields={tabularFields}
            allDocTypes={allDocTypes}
            mapping={mapping}
            targetFieldKey={targetFieldKey}
            onChange={(m, t) => { setMappingState(m); setTargetFieldKey(t); }}
          />
        )
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
  const [confirming, setConfirming] = useState(false);

  const update = useUpdateDataSetBinding();
  const del = useDeleteDataSetBinding();
  const autoMap = useAutoMapDataSetSource();

  const source = binding.source;
  const file = source?.file;
  // Цели табличного режима: inline-массив (`array`) и список ссылок на документы
  // (`doc-array`) — строки источника разворачиваются в объекты формы элемента-типа.
  const tabularFields = schemaFields.filter(f => f.type === 'array' || f.type === 'doc-array');
  const scalarFields = schemaFields.filter(f => isScalarField(f) && f.type !== 'file');

  async function handleAutoRemap() {
    if (!source) return;
    const { mapping: m } = await autoMap.mutateAsync({
      sourceId: source.id,
      fields: scalarFields.map(f => ({ key: f.key, title: f.title })),
    });
    setMappingState(m);
  }

  async function handleSave() {
    await update.mutateAsync({ id: binding.id, ownerId: instanceId, targetFieldKey, mapping });
    setEditing(false);
  }

  async function handleDelete() {
    await del.mutateAsync({ id: binding.id, ownerId: instanceId });
  }

  const mappedCount = Object.keys(mapping).filter(k => mapping[k]).length;

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
              {file?.name} · {source?.materializeTypeId
                ? `материализация → ${allDocTypes.find(t => t.id === source.materializeTypeId)?.name ?? 'тип'}`
                : `${mappedCount} пол${mappedCount === 1 ? 'е' : 'я'} привязано`}
              {targetFieldKey && ` · таблица: ${targetFieldKey}`}
            </span>
          </div>
        </div>
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
            tabularFields={tabularFields}
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
    </div>
  );
}

/** Ячейка превью: строка как есть, FileAttachment (файловый маппинг) — имя + размер, иначе null. */
function renderCellValue(v: unknown) {
  if (v == null) return <em>null</em>;
  if (isFileAttachment(v)) return <>📎 {v.fileName} <span className="text-fg4">({formatBytes(v.size)})</span></>;
  return String(v);
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
                  {Object.entries(r.data as Record<string, unknown>).map(([k, v]) => (
                    <tr key={k} className="border-b border-stroke last:border-0">
                      <td className="py-1 pr-4 font-medium w-1/3 text-fg3">{k}</td>
                      <td className={`py-1 ${v == null ? 'text-fg4' : 'text-fg1'}`}>
                        {renderCellValue(v)}
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
                const rows = r.data as Record<string, unknown>[];
                const preview = rows.slice(0, 5);
                const keys = preview.length > 0 ? Object.keys(preview[0]) : [];
                if (keys.length === 0) return (
                  <p className="px-3 py-2 text-xs text-fg4">Нет данных</p>
                );
                return (
                  <table className={dtTable}>
                    <thead>
                      <tr>
                        {keys.map(k => (
                          <th key={k} className={dtTh}>{k}</th>
                        ))}
                      </tr>
                    </thead>
                    <tbody>
                      {preview.map((row, i) => (
                        <tr key={i} className={dtRow}>
                          {keys.map(k => (
                            <td key={k} className={`${dtTd} whitespace-nowrap ${row[k] == null ? 'text-fg4' : 'text-fg1'}`}>
                              {renderCellValue(row[k])}
                            </td>
                          ))}
                        </tr>
                      ))}
                    </tbody>
                  </table>
                );
              })()}
              {(r.data as Record<string, unknown>[]).length > 5 && (
                <p className="px-3 py-1.5 text-xs border-t border-stroke text-fg4">
                  +{(r.data as Record<string, unknown>[]).length - 5} строк не показано
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
  const { data: bindings = [], isLoading } = useListDataSetBindings({ ownerId: instance.id });
  const [adding, setAdding] = useState(false);
  const [applyingTemplate, setApplyingTemplate] = useState(false);
  const [showPreview, setShowPreview] = useState(false);
  const { data: previewResults, isFetching: previewing, refetch: runPreview, error: previewError } = usePreviewDataSetBindings({ ownerId: instance.id });

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
