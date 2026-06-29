import { useState } from 'react';
import { Link2, Unlink, ChevronDown, ChevronUp, Plus, Pencil, Trash2, FileText, Database } from 'lucide-react';
import { Modal } from '@/shared/ui/Modal';
import {
  useListCommonData, useCommonDataForSet, useCreateCommonDataEntry,
  useUpdateCommonDataEntry, useDeleteCommonDataEntry,
} from '@/shared/api/commonData';
import type { CommonDataEntry, CatalogScope, DocumentType } from '@/shared/api/types';
import { SCOPE_LABELS, SCOPE_PRIORITY } from '@/shared/api/types';
import {
  resolveEffectiveFields, parseSchemaFields, groupEffectiveFields,
  getDefaultValues, type SchemaField,
} from '@/shared/api/schema';
import {
  SCOPE_COLORS, ComplexFieldGroup, ArrayFieldEditor, DocRefCatalogPickerField,
  PrimitiveInput, FileField, ImageField,
} from '../fields';

export function ScopedCatalogPanel({ scope, scopeId, allDocTypes, setId }: {
  scope: CatalogScope; scopeId: string | null; allDocTypes: DocumentType[];
  setId?: string;
}) {
  const [expanded, setExpanded] = useState(false);
  const [expandedTypes, setExpandedTypes] = useState<Set<string>>(new Set());
  const [addOpen, setAddOpen] = useState(false);
  const [editEntry, setEditEntry] = useState<CommonDataEntry | null>(null);

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
          className={`flex items-center gap-3 pl-6 pr-3 py-2 group hover:bg-muted transition-colors ${idx > 0 ? 'border-t border-stroke' : ''}`}>
          {isDocKind && <FileText size={12} className="text-warning shrink-0" />}
          <span className="flex-1 text-sm font-medium text-fg1 truncate">{entry.displayName}</span>
          {isDocKind && <span className="text-xs px-1.5 py-0.5 rounded bg-warning-subtle text-warning font-medium shrink-0">внеш. документ</span>}
          <button onClick={() => setEditEntry(entry)}
            className="p-1 text-stroke-strong hover:text-fg2 opacity-0 group-hover:opacity-100 transition-all">
            <Pencil size={12} />
          </button>
          <button
            onClick={() => { if (!confirm(`Удалить «${entry.displayName}»?`)) return; deleteMutation.mutate(entry.id); }}
            disabled={deleteMutation.isPending}
            className="p-1 text-stroke-strong hover:text-danger opacity-0 group-hover:opacity-100 transition-all disabled:opacity-30">
            <Trash2 size={12} />
          </button>
        </div>
      );
    });
  }

  return (
    <div className="border border-stroke rounded-xl overflow-hidden">
      <button onClick={() => setExpanded(v => !v)}
        className="w-full flex items-center gap-3 px-4 py-3 bg-surface hover:bg-base transition-colors text-left">
        <Database size={16} className="text-fg4" />
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
              {grouped.map(({ ct, items }) => {
                const isOpen = expandedTypes.has(ct.id);
                return (
                  <div key={ct.id} className="border border-stroke rounded-lg overflow-hidden bg-surface">
                    <button onClick={() => toggleType(ct.id)}
                      className="w-full flex items-center gap-2 px-3 py-2 hover:bg-base transition-colors text-left">
                      {isOpen
                        ? <ChevronUp size={12} className="text-fg4 shrink-0" />
                        : <ChevronDown size={12} className="text-fg4 shrink-0" />}
                      <span className="flex-1 text-sm font-medium text-fg2">{ct.name}</span>
                      <span className="text-xs text-fg4">{items.length}</span>
                    </button>
                    {isOpen && <div className="border-t border-stroke">{renderEntries(items)}</div>}
                  </div>
                );
              })}
              {noType.length > 0 && (() => {
                const isOpen = expandedTypes.has('__no_type__');
                return (
                  <div className="border border-stroke rounded-lg overflow-hidden bg-surface">
                    <button onClick={() => toggleType('__no_type__')}
                      className="w-full flex items-center gap-2 px-3 py-2 hover:bg-base transition-colors text-left">
                      {isOpen
                        ? <ChevronUp size={12} className="text-fg4 shrink-0" />
                        : <ChevronDown size={12} className="text-fg4 shrink-0" />}
                      <span className="flex-1 text-sm font-medium text-fg3 italic">Без типа</span>
                      <span className="text-xs text-fg4">{noType.length}</span>
                    </button>
                    {isOpen && <div className="border-t border-stroke">{renderEntries(noType)}</div>}
                  </div>
                );
              })()}
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
    </div>
  );
}

// ─── Base entry picker for catalog entry form ─────────────────────────────────

function CatalogBaseEntryPicker({ open, onOpenChange, parentType, setId, scope, scopeId, onSelect }: {
  open: boolean; onOpenChange: (o: boolean) => void;
  parentType: DocumentType; setId?: string;
  scope: CatalogScope; scopeId: string | null;
  onSelect: (entry: CommonDataEntry) => void;
}) {
  const [search, setSearch] = useState('');
  const { data: setCatalogEntries = [] } = useCommonDataForSet({
    setId: setId ?? '', typeId: parentType.id, enabled: open && !!setId,
  });
  const { data: scopeEntries = [] } = useListCommonData({
    scope, scopeId: scopeId ?? undefined, typeId: parentType.id,
    enabled: open && !setId && scope !== 'System',
  });
  const { data: systemEntries = [] } = useListCommonData({
    scope: 'System', typeId: parentType.id, enabled: open,
  });
  const allEntries: CommonDataEntry[] = setId
    ? setCatalogEntries
    : [...scopeEntries, ...systemEntries.filter(e => !scopeEntries.some(s => s.id === e.id))];
  const filtered = allEntries.filter(e => e.displayName.toLowerCase().includes(search.toLowerCase()));

  return (
    <Modal open={open} onOpenChange={onOpenChange} title={`Базовый экземпляр: ${parentType.name}`}>
      <div className="space-y-4">
        <input value={search} onChange={e => setSearch(e.target.value)} placeholder="Поиск..." autoFocus
          className="w-full border border-stroke-strong rounded-md px-3 py-2 text-sm focus:outline-none focus-visible:ring-2 focus-visible:ring-brand bg-surface" />
        {filtered.length === 0 ? (
          <p className="text-sm text-fg4 text-center py-4">
            Нет записей типа «{parentType.name}».
          </p>
        ) : (
          <div className="space-y-1 max-h-72 overflow-y-auto">
            {filtered.map(entry => (
              <button key={entry.id} type="button" onClick={() => { onSelect(entry); onOpenChange(false); }}
                className="w-full flex items-center gap-3 px-3 py-2 text-sm text-left rounded-md hover:bg-brand-subtle transition-colors">
                <Link2 size={13} className="text-brand shrink-0" />
                <span className="flex-1 font-medium text-fg1 truncate">{entry.displayName}</span>
              </button>
            ))}
          </div>
        )}
      </div>
    </Modal>
  );
}

// ─── Catalog entry form (create + edit, shared by ScopedCatalogPanel) ────────

function CatalogEntryForm({
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
  const [typeId, setTypeId] = useState(entry?.compositeTypeId ?? '');
  const [values, setValues] = useState<Record<string, unknown>>(() => entry?.data ?? {});
  const [error, setError] = useState('');
  const [expandedGroups, setExpandedGroups] = useState<Set<string>>(new Set());
  const [basePickerOpen, setBasePickerOpen] = useState(false);
  const createMutation = useCreateCommonDataEntry();
  const updateMutation = useUpdateCommonDataEntry();

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
  const baseEntry = parentEntries.find(e => e.id === baseRefId);

  function setValue(key: string, val: unknown) {
    setValues(p => {
      if (val === undefined) { const n = { ...p }; delete n[key]; return n; }
      return { ...p, [key]: val };
    });
  }
  function toggleGroup(key: string) {
    setExpandedGroups(prev => { const n = new Set(prev); n.has(key) ? n.delete(key) : n.add(key); return n; });
  }

  async function handleSubmit(e: React.FormEvent) {
    e.preventDefault();
    setError('');
    if (!displayName.trim() || !typeId) { setError('Укажите название и тип'); return; }
    try {
      if (entry) {
        await updateMutation.mutateAsync({ id: entry.id, displayName, data: JSON.stringify(values) });
      } else {
        await createMutation.mutateAsync({ displayName, compositeTypeId: typeId, data: JSON.stringify(values), scope, scopeId });
      }
      onClose();
    } catch (err: unknown) { setError(err instanceof Error ? err.message : 'Ошибка'); }
  }

  function renderFields(sectionFields: SchemaField[]) {
    return (
      <div className="space-y-4">
        {sectionFields.map(field => {
          const val = values[field.key];
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
                    <PrimitiveInput field={field} value={val} onChange={v => setValue(field.key, v)} invalid={false} />
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
        <div className="rounded-lg border border-stroke p-3 space-y-2">
          <p className="text-xs font-semibold text-fg3 uppercase tracking-wide">
            Базовый экземпляр
            <span className="normal-case font-normal ml-1 text-fg4">({parentType.name})</span>
          </p>
          {baseRefId && baseEntry ? (
            <div className="flex items-center gap-2 rounded-md border border-brand-subtle bg-brand-subtle px-3 py-2">
              <Link2 size={14} className="text-brand shrink-0" />
              <span className="flex-1 text-sm font-medium text-brand-hover truncate">{baseEntry.displayName}</span>
              <button type="button" onClick={() => setValue('_baseRef', undefined)}
                className="text-brand hover:text-danger transition-colors" title="Снять ссылку">
                <Unlink size={13} />
              </button>
            </div>
          ) : (
            <button type="button" onClick={() => setBasePickerOpen(true)}
              className="flex items-center gap-2 text-sm text-brand hover:text-brand-hover border border-dashed border-brand-subtle rounded-md px-3 py-2 w-full hover:bg-brand-subtle transition-colors">
              <Link2 size={14} />
              Выбрать из «{parentType.name}»...
            </button>
          )}
          {!baseRefId && ownFields.length < effectiveFields.length && (
            <p className="text-xs text-fg4">
              Без базового экземпляра все {effectiveFields.length} полей заполняются вручную.
            </p>
          )}
          <CatalogBaseEntryPicker
            open={basePickerOpen}
            onOpenChange={setBasePickerOpen}
            parentType={parentType}
            setId={setId}
            scope={scope}
            scopeId={scopeId}
            onSelect={e => setValue('_baseRef', e.id)}
          />
        </div>
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
        <button type="submit" disabled={isPending}
          className="px-4 py-2 text-sm bg-brand hover:bg-brand-hover text-white rounded-md disabled:opacity-50">
          {isPending ? 'Сохранение...' : entry ? 'Сохранить' : 'Создать'}
        </button>
      </div>
    </form>
  );
}
