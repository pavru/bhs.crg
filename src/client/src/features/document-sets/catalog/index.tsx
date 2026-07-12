import { useState } from 'react';
import {
  ChevronDown, ChevronUp, Plus, Pencil, Trash2, FileText, Database, ShieldCheck, Loader2,
  DatabaseZap, RefreshCw, X,
} from 'lucide-react';
import { Modal } from '@/shared/ui/Modal';
import { ConfirmDialog } from '@/shared/ui/ConfirmDialog';
import {
  useListCommonData, useCommonDataForSet, useCreateCommonDataEntry,
  useUpdateCommonDataEntry, useDeleteCommonDataEntry,
} from '@/shared/api/commonData';
import type { CommonDataEntry, CatalogScope, DocumentType, PrimitiveTypeDef, EnumTypeDef } from '@/shared/api/types';
import { SCOPE_LABELS, SCOPE_PRIORITY } from '@/shared/api/types';
import { useListPrimitiveTypes } from '@/shared/api/primitiveTypes';
import { useListEnumTypes } from '@/shared/api/enumTypes';
import {
  resolveEffectiveFields, parseSchemaFields, groupEffectiveFields,
  getDefaultValues, findTaggedFieldPath, type SchemaField,
} from '@/shared/api/schema';
import { isFileAttachment, formatBytes } from '@/shared/api/attachments';
import { recognizeDocument } from '@/shared/api/qualityDocs';
import { flattenLeaves, applyRecognized } from '@/features/quality-docs/QualityDocForm';
import { FUNCTIONAL_TAG } from '@/shared/api/tags';
import { useListDataSetBindings, usePreviewDataSetBindings } from '@/shared/api/datasets';
import { computeBoundFieldKeys, mergeBindingPreviewsIntoValues } from '@/shared/api/datasetHelpers';
import { EntryDataSetBindings } from './EntryDataSetBindings';
import {
  SCOPE_COLORS, ComplexFieldGroup, ArrayFieldEditor, DocRefCatalogPickerField,
  PrimitiveInput, FileField, ImageField,
  BaseInstancePanel, SCOPE_TIER, type BaseCandidate,
} from '../fields';

export function ScopedCatalogPanel({ scope, scopeId, allDocTypes, setId }: {
  scope: CatalogScope; scopeId: string | null; allDocTypes: DocumentType[];
  setId?: string;
}) {
  const [expanded, setExpanded] = useState(false);
  const [expandedTypes, setExpandedTypes] = useState<Set<string>>(new Set());
  const [addOpen, setAddOpen] = useState(false);
  const [editEntry, setEditEntry] = useState<CommonDataEntry | null>(null);
  const [deleteTarget, setDeleteTarget] = useState<CommonDataEntry | null>(null);

  const { data: entries = [], isLoading } = useListCommonData({
    scope, scopeId: scopeId ?? undefined, enabled: expanded,
  });
  const compositeTypes = allDocTypes.filter(dt => dt.kind === 'Composite');
  const documentTypes = allDocTypes.filter(dt => dt.kind === 'Document' && !dt.isAbstract);
  const allSelectableTypes = [...compositeTypes, ...documentTypes];
  const deleteMutation = useDeleteCommonDataEntry();

  const grouped = allSelectableTypes
    .map(ct => ({ ct, items: [...entries].filter(e => e.compositeTypeId === ct.id).sort((a, b) => a.displayName.localeCompare(b.displayName)) }))
    .filter(g => g.items.length > 0);
  const noType = [...entries].filter(e => !allSelectableTypes.find(ct => ct.id === e.compositeTypeId)).sort((a, b) => a.displayName.localeCompare(b.displayName));

  function toggleType(id: string) {
    setExpandedTypes(prev => { const n = new Set(prev); n.has(id) ? n.delete(id) : n.add(id); return n; });
  }

  function renderEntries(items: CommonDataEntry[]) {
    return items.map((entry, idx) => {
      const isDocKind = documentTypes.some(dt => dt.id === entry.compositeTypeId);
      return (
        <div key={entry.id}
          className={`flex items-center gap-3 pl-3 pr-3 py-2 group hover:bg-muted transition-colors ${idx > 0 ? 'border-t border-stroke' : ''}`}>
          {isDocKind && <FileText size={12} className="text-warning shrink-0" />}
          <span className="flex-1 text-sm text-fg1 truncate">{entry.displayName}</span>
          {isDocKind && <span className="text-xs px-1.5 py-0.5 rounded bg-warning-subtle text-warning font-medium shrink-0">внеш. документ</span>}
          <button onClick={() => setEditEntry(entry)}
            className="p-1 text-stroke-strong hover:text-fg2 opacity-0 group-hover:opacity-100 transition-all">
            <Pencil size={12} />
          </button>
          <button
            onClick={() => setDeleteTarget(entry)}
            disabled={deleteMutation.isPending}
            className="p-1 text-stroke-strong hover:text-danger opacity-0 group-hover:opacity-100 transition-all disabled:opacity-30">
            <Trash2 size={12} />
          </button>
        </div>
      );
    });
  }

  // Группа типа: uppercase-микрозаголовок на заливке bg-base (сильный «заголовочный» сигнал),
  // записи — плоские, на bg-surface, с левым рейлом-коннектором. Три канала различия уровней
  // (типографика + заливка + рейл) — issue #8, слабая визуальная иерархия.
  function renderGroup(key: string, label: string, items: CommonDataEntry[], muted = false) {
    const isOpen = expandedTypes.has(key);
    return (
      <div key={key} className="border border-stroke rounded-lg overflow-hidden bg-surface">
        <button onClick={() => toggleType(key)} aria-expanded={isOpen}
          className="w-full flex items-center gap-2 px-3 py-1.5 bg-base hover:bg-muted transition-colors text-left">
          {isOpen
            ? <ChevronUp size={12} className="text-fg4 shrink-0" />
            : <ChevronDown size={12} className="text-fg4 shrink-0" />}
          <span className={`flex-1 text-xs font-semibold uppercase tracking-wide ${muted ? 'text-fg4' : 'text-fg2'}`}>{label}</span>
          <span className="text-xs text-fg4">{items.length}</span>
        </button>
        {isOpen && (
          <div className="bg-surface">
            <div className="ml-3 border-l border-stroke">{renderEntries(items)}</div>
          </div>
        )}
      </div>
    );
  }

  return (
    <div className="border border-stroke rounded-xl overflow-hidden">
      <button onClick={() => setExpanded(v => !v)} aria-expanded={expanded}
        className="w-full flex items-center gap-3 px-4 py-3 bg-surface hover:bg-base transition-colors text-left">
        <Database size={16} className="text-fg4" />
        <span className={`shrink-0 text-[10px] px-1.5 py-0.5 rounded-full font-medium ${SCOPE_COLORS[scope]}`}>
          {SCOPE_LABELS[scope]}
        </span>
        <span className="text-sm font-medium text-fg2">Каталог общих данных</span>
        {!expanded && entries.length > 0 && (
          <span className="text-xs text-fg4">{entries.length} записей</span>
        )}
        {expanded
          ? <ChevronUp size={14} className="text-fg4 ml-auto" />
          : <ChevronDown size={14} className="text-fg4 ml-auto" />}
      </button>
      {expanded && (
        <div className="border-t border-stroke bg-base px-4 py-4 space-y-2">
          {isLoading ? (
            <p className="text-sm text-fg4 text-center py-2">Загрузка...</p>
          ) : entries.length === 0 ? (
            <p className="text-sm text-fg4 text-center py-2">Записей нет</p>
          ) : (
            <div className="space-y-1">
              {grouped.map(({ ct, items }) => renderGroup(ct.id, ct.name, items))}
              {noType.length > 0 && renderGroup('__no_type__', 'Без типа', noType, true)}
            </div>
          )}
          <button onClick={() => setAddOpen(true)}
            className="flex items-center gap-1.5 text-sm text-brand hover:text-brand-hover transition-colors pt-1">
            <Plus size={14} /> Добавить запись
          </button>
        </div>
      )}
      <Modal open={addOpen} onOpenChange={setAddOpen} title="Новая запись каталога" wide flushBody>
        {addOpen && (
          <CatalogEntryForm compositeTypes={compositeTypes} documentTypes={documentTypes} allDocTypes={allDocTypes}
            scope={scope} scopeId={scopeId} setId={setId} onClose={() => setAddOpen(false)} />
        )}
      </Modal>
      <Modal open={!!editEntry} onOpenChange={o => { if (!o) setEditEntry(null); }} title="Редактировать запись" wide flushBody>
        {editEntry && (
          <CatalogEntryForm entry={editEntry} compositeTypes={compositeTypes} documentTypes={documentTypes} allDocTypes={allDocTypes}
            scope={scope} scopeId={scopeId} setId={setId} onClose={() => setEditEntry(null)} />
        )}
      </Modal>
      <ConfirmDialog
        open={!!deleteTarget}
        onOpenChange={o => { if (!o) setDeleteTarget(null); }}
        title={`Удалить «${deleteTarget?.displayName ?? ''}»?`}
        confirmLabel="Удалить"
        onConfirm={() => { if (deleteTarget) deleteMutation.mutate(deleteTarget.id); }}
      />
    </div>
  );
}

// Базовый экземпляр каталога использует общий BaseCandidatePicker (issue #73, шаг 2) —
// кандидаты (записи родительского типа по скопам) строятся ниже из parentEntries.

// ─── Catalog entry form (create + edit, shared by ScopedCatalogPanel) ────────

export function CatalogEntryForm({
  entry, compositeTypes, documentTypes = [], allDocTypes, scope, scopeId, setId, onClose,
}: {
  entry?: CommonDataEntry | null;
  compositeTypes: DocumentType[];
  documentTypes?: DocumentType[];
  allDocTypes: DocumentType[];
  scope: CatalogScope;
  scopeId: string | null;
  setId?: string;
  onClose: () => void;
}) {
  const [displayName, setDisplayName] = useState(entry?.displayName ?? '');
  const [aliases, setAliases] = useState<string[]>(entry?.aliases ?? []);
  const [aliasDraft, setAliasDraft] = useState('');
  function addAlias() {
    const t = aliasDraft.trim();
    if (t && !aliases.some(a => a.toLowerCase() === t.toLowerCase())) setAliases(prev => [...prev, t]);
    setAliasDraft('');
  }
  const [typeId, setTypeId] = useState(entry?.compositeTypeId ?? '');
  const [values, setValues] = useState<Record<string, unknown>>(() => entry?.data ?? {});
  const [error, setError] = useState('');
  const [expandedGroups, setExpandedGroups] = useState<Set<string>>(new Set());
  const [recognizing, setRecognizing] = useState(false);
  const createMutation = useCreateCommonDataEntry();
  const updateMutation = useUpdateCommonDataEntry();
  const { data: primitiveTypes = [] } = useListPrimitiveTypes();
  const { data: enumTypes = [] } = useListEnumTypes();

  const getPrimitiveDef = (f: SchemaField): PrimitiveTypeDef | undefined =>
    f.type === 'primitive' ? primitiveTypes.find(pt => pt.id === f.typeId) : undefined;
  const getEnumDef = (f: SchemaField): EnumTypeDef | undefined =>
    f.type === 'enum' ? enumTypes.find(et => et.id === f.typeId) : undefined;

  const allSelectableTypes = [...compositeTypes, ...documentTypes];
  const selectedType = allSelectableTypes.find(t => t.id === typeId) ?? null;

  const parentType = selectedType?.parentId
    ? allDocTypes.find(dt => dt.id === selectedType.parentId) ?? null
    : null;
  const baseRefId = typeof values._baseRef === 'string' ? values._baseRef : undefined;
  const ownFields = selectedType ? parseSchemaFields(selectedType.schema) : [];
  const effectiveFields = selectedType ? resolveEffectiveFields(selectedType, allDocTypes) : [];
  const displayFields = (parentType && baseRefId) ? ownFields : effectiveFields;
  const sections = selectedType ? groupEffectiveFields(displayFields, selectedType.schema) : [];

  // Распознавание: берём первое поле-файл с загруженным вложением (обычно единственное —
  // "Файл"). Заполняет только простые поля (flattenLeaves пропускает array/doc-ref/complex-с-
  // ссылкой) — составные/ссылочные остаются ручными, этого достаточно для «выборочности».
  const fileFieldValue = effectiveFields
    .map(f => (f.type === 'file' ? values[f.key] : undefined))
    .find(v => isFileAttachment(v));
  const attachment = isFileAttachment(fileFieldValue) ? fileFieldValue : null;

  const { data: allParentEntries = [] } = useCommonDataForSet({
    setId: setId ?? '', typeId: parentType?.id, enabled: !!parentType && !!setId,
  });
  const { data: scopeParentEntries = [] } = useListCommonData({
    scope, scopeId: scopeId ?? undefined, typeId: parentType?.id,
    enabled: !!parentType && !setId && scope !== 'System',
  });
  const { data: systemParentEntries = [] } = useListCommonData({
    scope: 'System', typeId: parentType?.id, enabled: !!parentType,
  });
  const parentEntries: CommonDataEntry[] = setId
    ? allParentEntries
    : [...scopeParentEntries, ...systemParentEntries.filter(e => !scopeParentEntries.some(s => s.id === e.id))];
  // Кандидаты базы для общего пикера (issue #73, шаг 2): записи родительского типа по скопам.
  const baseCandidates: BaseCandidate[] = parentEntries
    .map(e => ({
      kind: 'catalog' as const, id: e.id, name: e.displayName, typeId: e.compositeTypeId,
      tier: SCOPE_TIER[e.scope], scopeLabel: SCOPE_LABELS[e.scope], dist: 0,
    }))
    .sort((a, b) => a.tier - b.tier || a.name.localeCompare(b.name, 'ru'));

  // Наборы данных: биндинги существуют только у уже сохранённой записи (нужен id-владелец).
  const { data: bindings = [] } = useListDataSetBindings({ commonDataEntryId: entry?.id });
  const { scalarKeys: boundFieldKeys, arrayKeys: boundArrayKeys } = computeBoundFieldKeys(bindings);
  const { refetch: refetchBindingPreview, isFetching: refreshingFromSource } =
    usePreviewDataSetBindings({ commonDataEntryId: entry?.id });

  async function handleRefreshFromSource() {
    const { data: previews } = await refetchBindingPreview();
    if (previews) setValues(v => mergeBindingPreviewsIntoValues(v, previews));
  }

  function setValue(key: string, val: unknown) {
    setValues(p => {
      if (val === undefined) { const n = { ...p }; delete n[key]; return n; }
      return { ...p, [key]: val };
    });
  }
  function toggleGroup(key: string) {
    setExpandedGroups(prev => { const n = new Set(prev); n.has(key) ? n.delete(key) : n.add(key); return n; });
  }

  async function handleRecognize() {
    if (!attachment || !selectedType) return;
    setRecognizing(true); setError('');
    try {
      const rec = await recognizeDocument({
        blobPath: attachment.blobPath, mimeType: attachment.mimeType,
        fields: flattenLeaves(effectiveFields, allDocTypes),
        promptKind: 'titleblock',
      });
      let next = applyRecognized(values, rec.values);
      // Число страниц надёжнее брать из самого файла, чем просить модель прочитать его на штампе.
      if (rec.pageCount != null) {
        const p = findTaggedFieldPath(selectedType, FUNCTIONAL_TAG.docPageCount, allDocTypes);
        if (p) next = applyRecognized(next, { [p.join('.')]: String(rec.pageCount) });
      }
      setValues(next);
    } catch (e: unknown) {
      const resp = (e as { response?: { data?: { error?: string; limit?: boolean } } })?.response;
      if (resp?.data?.limit) setError('Лимит LLM исчерпан — повторите распознавание позже.');
      else setError(resp?.data?.error ?? (e instanceof Error ? e.message : 'Ошибка распознавания'));
    } finally { setRecognizing(false); }
  }

  async function handleSubmit(e: React.FormEvent) {
    e.preventDefault();
    setError('');
    if (!displayName.trim() || !typeId) { setError('Укажите название и тип'); return; }
    try {
      if (entry) {
        await updateMutation.mutateAsync({ id: entry.id, displayName, data: JSON.stringify(values), aliases });
      } else {
        await createMutation.mutateAsync({ displayName, compositeTypeId: typeId, data: JSON.stringify(values), scope, scopeId, aliases });
      }
      onClose();
    } catch (err: unknown) { setError(err instanceof Error ? err.message : 'Ошибка'); }
  }

  function renderFields(sectionFields: SchemaField[]) {
    return (
      <div className="space-y-4">
        {sectionFields.map(field => {
          const val = values[field.key];
          const isBoundArray = field.type === 'array' && boundArrayKeys.has(field.key);
          const isBoundScalar = field.type !== 'array' && boundFieldKeys.has(field.key);

          if (isBoundArray) {
            const rows = Array.isArray(val) ? val as Record<string, unknown>[] : [];
            const cols = rows.length > 0 ? Object.keys(rows[0]) : [];
            return (
              <div key={field.key}>
                <label className="flex items-center gap-1.5 text-sm font-medium text-fg2 mb-1">
                  {field.title}
                  <span title="Значения подставляются из источника данных"><DatabaseZap size={12} className="text-brand" /></span>
                </label>
                <div className="rounded-md border border-stroke overflow-x-auto bg-muted">
                  {rows.length === 0 ? (
                    <p className="px-3 py-2 text-xs text-fg4">Нет данных из источника</p>
                  ) : (
                    <table className="text-xs w-full">
                      <thead>
                        <tr className="bg-base">
                          {cols.map(k => <th key={k} className="px-3 py-1.5 text-left font-medium whitespace-nowrap text-fg3">{k}</th>)}
                        </tr>
                      </thead>
                      <tbody>
                        {rows.map((row, i) => (
                          <tr key={i} className="border-t border-stroke">
                            {cols.map(k => (
                              <td key={k} className="px-3 py-1.5 whitespace-nowrap text-fg1">{String(row[k] ?? '')}</td>
                            ))}
                          </tr>
                        ))}
                      </tbody>
                    </table>
                  )}
                </div>
              </div>
            );
          }

          if (isBoundScalar) {
            const display = val === undefined || val === null || val === ''
              ? null
              : isFileAttachment(val)
                ? `📎 ${val.fileName} (${formatBytes(val.size)})`
                : (typeof val === 'string' ? val : JSON.stringify(val));
            return (
              <div key={field.key}>
                <label className="flex items-center gap-1.5 text-sm font-medium text-fg2 mb-1">
                  {field.title}
                  <span title="Значение подставляется из источника данных"><DatabaseZap size={12} className="text-brand" /></span>
                </label>
                <div className="w-full border border-stroke rounded-md px-3 py-2 text-sm bg-muted text-fg2">
                  {display ?? <em className="text-fg4">нет данных</em>}
                </div>
              </div>
            );
          }

          return (
            <div key={field.key}>
              {field.type === 'complex' || field.type === 'array' ? (
                <div>
                  <label className="block text-sm font-medium text-fg2 mb-1">
                    {field.title}
                    {field.required && <span className="ml-0.5 text-danger">*</span>}
                  </label>
                  {field.type === 'array' ? (
                    <ArrayFieldEditor
                      field={field} allDocTypes={allDocTypes} value={val}
                      onChange={v => setValue(field.key, v)} showValidation={false}
                      setId={setId} scope={scope} scopeId={scopeId}
                    />
                  ) : (
                    <ComplexFieldGroup
                      field={field} allDocTypes={allDocTypes} value={val}
                      onChange={v => setValue(field.key, v)} showValidation={false}
                      setId={setId} scope={scope} scopeId={scopeId}
                    />
                  )}
                </div>
              ) : field.type === 'doc-ref' ? (
                <div>
                  <label className="block text-sm font-medium text-fg2 mb-1">
                    {field.title}
                    {field.required && <span className="ml-0.5 text-danger">*</span>}
                  </label>
                  <DocRefCatalogPickerField field={field} allDocTypes={allDocTypes} value={val}
                    onChange={v => setValue(field.key, v ?? undefined)}
                    setId={setId} scope={scope} scopeId={scopeId} />
                </div>
              ) : (
                <>
                  {field.type !== 'boolean' && (
                    <label className="block text-sm font-medium text-fg2 mb-1">
                      {field.title}
                      {field.required && <span className="ml-0.5 text-danger">*</span>}
                    </label>
                  )}
                  {field.type === 'image' ? (
                    <ImageField value={val} onChange={v => setValue(field.key, v)} />
                  ) : field.type === 'file' ? (
                    <FileField value={val} onChange={v => setValue(field.key, v)} />
                  ) : (
                    <PrimitiveInput field={field} value={val} onChange={v => setValue(field.key, v)} invalid={false}
                      primitiveTypeDef={getPrimitiveDef(field)} enumTypeDef={getEnumDef(field)} />
                  )}
                </>
              )}
            </div>
          );
        })}
      </div>
    );
  }

  const isPending = createMutation.isPending || updateMutation.isPending;

  return (
    <form onSubmit={handleSubmit} className="flex flex-col min-h-0 flex-1">
      <div className="flex-1 min-h-0 overflow-y-auto px-6 py-4 space-y-4">
      <div className="flex items-center gap-2">
        <span className={`text-xs px-1.5 py-0.5 rounded font-medium ${SCOPE_COLORS[scope]}`}>
          {SCOPE_LABELS[scope]}
        </span>
        <span className="text-xs text-fg3">приоритет {SCOPE_PRIORITY[scope]}</span>
      </div>

      <div>
        <label className="block text-sm font-medium text-fg2 mb-1">Наименование</label>
        <input value={displayName} onChange={e => setDisplayName(e.target.value)} required autoFocus
          className="w-full border border-stroke-strong rounded-md px-3 py-2 text-sm focus:outline-none focus-visible:ring-2 focus-visible:ring-brand bg-surface" />
      </div>

      {/* Алиасы (issue #74) — доп. имена для поиска записи при связывании с источниками */}
      <div>
        <label className="block text-sm font-medium text-fg2 mb-1">
          Псевдонимы <span className="text-xs text-fg4 font-normal">(для поиска при связывании с источниками)</span>
        </label>
        {aliases.length > 0 && (
          <div className="flex flex-wrap gap-1.5 mb-1.5">
            {aliases.map(a => (
              <span key={a} className="inline-flex items-center gap-1 text-xs bg-muted text-fg2 rounded-full pl-2.5 pr-1 py-0.5">
                {a}
                <button type="button" onClick={() => setAliases(prev => prev.filter(x => x !== a))}
                  className="text-fg4 hover:text-danger transition-colors" title="Удалить">
                  <X size={11} />
                </button>
              </span>
            ))}
          </div>
        )}
        <input value={aliasDraft} onChange={e => setAliasDraft(e.target.value)}
          onKeyDown={e => { if (e.key === 'Enter' || e.key === ',') { e.preventDefault(); addAlias(); } }}
          onBlur={addAlias}
          placeholder="Добавить псевдоним и Enter..."
          className="w-full border border-stroke-strong rounded-md px-3 py-2 text-sm focus:outline-none focus-visible:ring-2 focus-visible:ring-brand bg-surface" />
      </div>

      {!entry ? (
        <div>
          <label className="block text-sm font-medium text-fg2 mb-1">Тип</label>
          <select value={typeId} onChange={e => {
              const newId = e.target.value;
              setTypeId(newId);
              const t = allSelectableTypes.find(c => c.id === newId);
              setValues(t ? getDefaultValues(resolveEffectiveFields(t, allDocTypes)) : {});
            }} required
            className="w-full border border-stroke-strong rounded-md px-3 py-2 text-sm focus:outline-none focus-visible:ring-2 focus-visible:ring-brand bg-surface">
            <option value="">Выберите тип...</option>
            {compositeTypes.length > 0 && (
              <optgroup label="Составные типы">
                {compositeTypes.map(ct => <option key={ct.id} value={ct.id}>{ct.name} ({ct.code})</option>)}
              </optgroup>
            )}
            {documentTypes.length > 0 && (
              <optgroup label="Типы документов (внешние)">
                {documentTypes.map(dt => <option key={dt.id} value={dt.id}>{dt.name} ({dt.code})</option>)}
              </optgroup>
            )}
          </select>
        </div>
      ) : (
        <p className="text-sm text-fg3">
          Тип: <span className="font-medium text-fg2">
            {allSelectableTypes.find(ct => ct.id === entry.compositeTypeId)?.name ?? entry.compositeTypeId}
          </span>
        </p>
      )}

      {parentType && (
        <BaseInstancePanel
          title={parentType.name}
          candidates={baseCandidates}
          selected={baseCandidates.find(c => c.id === baseRefId)}
          missing={!!baseRefId && !baseCandidates.some(c => c.id === baseRefId)}
          manualHint={ownFields.length < effectiveFields.length
            ? `Без базового экземпляра все ${effectiveFields.length} полей заполняются вручную.` : undefined}
          onSelect={c => setValue('_baseRef', c.id)}
          onClear={() => setValue('_baseRef', undefined)}
        />
      )}

      {attachment && (
        <div className="flex items-center gap-2">
          <button type="button" onClick={handleRecognize} disabled={recognizing}
            title="Заполнит простые поля по штампу/реквизитам файла — ссылочные и составные поля не тронет"
            className="flex items-center gap-1.5 text-sm px-3 py-1.5 rounded-md bg-brand-subtle text-brand hover:bg-brand-subtle disabled:opacity-50">
            {recognizing ? <Loader2 size={14} className="animate-spin" /> : <ShieldCheck size={14} />}
            Распознать «{attachment.fileName}»
          </button>
          {recognizing && (
            <span className="flex items-center gap-1.5 text-xs text-fg3">
              <Loader2 size={12} className="animate-spin" /> Идёт распознавание — дождитесь завершения перед сохранением…
            </span>
          )}
        </div>
      )}

      {entry ? (
        <div className="space-y-2">
          <EntryDataSetBindings
            entryId={entry.id}
            bindings={bindings}
            schemaFields={effectiveFields}
            allDocTypes={allDocTypes}
            setId={setId}
            scope={scope}
            scopeId={scopeId}
          />
          {bindings.length > 0 && (
            <button type="button" onClick={handleRefreshFromSource} disabled={refreshingFromSource}
              className="flex items-center gap-1.5 text-sm px-3 py-1.5 rounded-md bg-muted text-fg2 hover:bg-stroke disabled:opacity-50">
              {refreshingFromSource ? <Loader2 size={14} className="animate-spin" /> : <RefreshCw size={14} />}
              Обновить из источника
            </button>
          )}
        </div>
      ) : (
        <p className="text-xs text-fg4">Сохраните запись, чтобы привязать источники данных.</p>
      )}

      {selectedType && sections.length > 0 && (
        <div className="space-y-3 pt-1 border-t border-muted">
          {sections.map(section => {
            if (!section.title) {
              return <div key={section.key}>{renderFields(section.fields)}</div>;
            }
            const isExpanded = expandedGroups.has(section.key);
            return (
              <div key={section.key} className="border border-stroke rounded-lg overflow-hidden">
                <button type="button" onClick={() => toggleGroup(section.key)}
                  className="w-full flex items-center gap-2 px-3 py-2.5 bg-base hover:bg-muted transition-colors text-left">
                  {isExpanded
                    ? <ChevronUp size={13} className="text-fg4 shrink-0" />
                    : <ChevronDown size={13} className="text-fg4 shrink-0" />}
                  <span className="text-xs font-semibold uppercase tracking-wide text-fg2 flex-1">{section.title}</span>
                  <span className="text-xs text-fg4">{section.fields.length} п.</span>
                </button>
                {isExpanded && <div className="px-3 py-3">{renderFields(section.fields)}</div>}
              </div>
            );
          })}
        </div>
      )}

      {error && <p className="text-sm text-danger">{error}</p>}
      </div>
      <div className="shrink-0 px-6 py-3 border-t border-stroke flex justify-end gap-3">
        <button type="button" onClick={onClose} className="px-4 py-2 text-sm text-fg2 hover:bg-muted rounded-md">Отмена</button>
        <button type="submit" disabled={isPending || recognizing}
          className="px-4 py-2 text-sm bg-brand hover:bg-brand-hover text-white rounded-md disabled:opacity-50">
          {isPending ? 'Сохранение...' : entry ? 'Сохранить' : 'Создать'}
        </button>
      </div>
    </form>
  );
}
