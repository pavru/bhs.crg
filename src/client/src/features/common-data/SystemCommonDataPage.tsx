import { useState } from 'react';
import { Plus, Pencil, Trash2, Search, Link2, Unlink, ChevronDown, ChevronUp } from 'lucide-react';
import { Modal } from '@/shared/ui/Modal';
import { ConfirmDialog } from '@/shared/ui/ConfirmDialog';
import { useListDocumentTypes } from '@/shared/api/documentTypes';
import { useListEnumTypes } from '@/shared/api/enumTypes';
import { useListCommonData, useCreateCommonDataEntry, useUpdateCommonDataEntry, useDeleteCommonDataEntry } from '@/shared/api/commonData';
import type { CommonDataEntry, DocumentType, EnumTypeDef } from '@/shared/api/types';
import { resolveEffectiveFields, groupEffectiveFields, parseSchemaFields, getDefaultValues, type SchemaField } from '@/shared/api/schema';
import { PrimitiveInput, FileField, ImageField, SystemArrayFieldEditor, SystemComplexField, DocRefCatalogField, BaseEntryPickerModal } from './systemFields';

// ─── Entry form (add / edit) ──────────────────────────────────────────────────

function EntryForm({
  entry, compositeTypes, documentTypes, allDocTypes, enumTypes, onClose,
}: {
  entry: CommonDataEntry | null;
  compositeTypes: DocumentType[];
  documentTypes: DocumentType[];
  allDocTypes: DocumentType[];
  enumTypes: EnumTypeDef[];
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

  // Базовый тип (родитель) — для поддержки _baseRef
  const parentType = selectedType?.parentId
    ? allDocTypes.find(dt => dt.id === selectedType.parentId) ?? null
    : null;

  // Текущий _baseRef (ID базового экземпляра)
  const baseRefId = typeof values._baseRef === 'string' ? values._baseRef : undefined;

  // Когда _baseRef задан — показываем только собственные поля этого типа,
  // наследуемые поля придут из базового экземпляра через EntityResolver
  const ownFields = selectedType ? parseSchemaFields(selectedType.schema) : [];
  const effectiveFields = selectedType ? resolveEffectiveFields(selectedType, allDocTypes) : [];
  const displayFields = (parentType && baseRefId) ? ownFields : effectiveFields;
  const sections = selectedType
    ? groupEffectiveFields(displayFields, selectedType.schema)
    : [];

  // Записи базового типа для пикера
  const { data: parentEntries = [] } = useListCommonData({
    scope: 'System',
    typeId: parentType?.id,
    enabled: !!parentType,
  });
  const baseEntry = parentEntries.find(e => e.id === baseRefId);

  function setValue(key: string, v: unknown) {
    setValues(p => {
      if (v === undefined) { const n = { ...p }; delete n[key]; return n; }
      return { ...p, [key]: v };
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
        await createMutation.mutateAsync({ displayName, compositeTypeId: typeId, data: JSON.stringify(values), scope: 'System', scopeId: null });
      }
      onClose();
    } catch (err: unknown) { setError(err instanceof Error ? err.message : 'Ошибка'); }
  }

  function renderFields(sectionFields: SchemaField[]) {
    return (
      <div className="space-y-4">
        {sectionFields.map(field => {
          // doc-array links to multiple document instances — not applicable in catalog context
          if (field.type === 'doc-array') return null;
          return (
          <div key={field.key}>
            {field.type === 'complex' || field.type === 'array' ? (
              <div>
                <label className="block text-sm font-medium text-fg2 mb-1">
                  {field.title}{field.required && <span className="ml-0.5 text-danger">*</span>}
                </label>
                {field.type === 'array' ? (
                  <SystemArrayFieldEditor field={field} allDocTypes={allDocTypes} enumTypes={enumTypes}
                    value={values[field.key]} onChange={v => setValue(field.key, v)} />
                ) : (
                  <SystemComplexField field={field} allDocTypes={allDocTypes} enumTypes={enumTypes}
                    value={values[field.key]} onChange={v => setValue(field.key, v)} />
                )}
              </div>
            ) : field.type === 'doc-ref' ? (
              <div>
                <label className="block text-sm font-medium text-fg2 mb-1">
                  {field.title}{field.required && <span className="ml-0.5 text-danger">*</span>}
                </label>
                <DocRefCatalogField field={field} allDocTypes={allDocTypes}
                  value={values[field.key]} onChange={v => setValue(field.key, v ?? undefined)} />
              </div>
            ) : (
              <>
                {field.type !== 'boolean' && (
                  <label className="block text-sm font-medium text-fg2 mb-1">
                    {field.title}{field.required && <span className="ml-0.5 text-danger">*</span>}
                  </label>
                )}
                {field.type === 'image' ? (
                  <ImageField value={values[field.key]} onChange={v => setValue(field.key, v)} />
                ) : field.type === 'file' ? (
                  <FileField value={values[field.key]} onChange={v => setValue(field.key, v)} />
                ) : (
                  <PrimitiveInput field={field} value={values[field.key]}
                    enumTypeDef={field.type === 'enum' ? enumTypes.find(et => et.id === field.typeId) : undefined}
                    onChange={v => setValue(field.key, v)} />
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
            className="w-full border border-stroke-strong rounded-md px-3 py-2 text-sm text-fg1 bg-surface focus:outline-none focus-visible:ring-2 focus-visible:ring-brand">
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
            {allSelectableTypes.find(t => t.id === entry.compositeTypeId)?.name ?? entry.compositeTypeId}
          </span>
          {allDocTypes.find(t => t.id === entry.compositeTypeId)?.kind === 'Document' && (
            <span className="ml-2 text-xs bg-warning-subtle text-warning border border-warning-border px-1.5 py-0.5 rounded-full">внеш. документ</span>
          )}
        </p>
      )}

      {/* Базовый экземпляр — показываем, если тип имеет родителя */}
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
          <BaseEntryPickerModal
            open={basePickerOpen}
            onOpenChange={setBasePickerOpen}
            parentType={parentType}
            onSelect={e => setValue('_baseRef', e.id)}
          />
        </div>
      )}

      {selectedType && sections.length > 0 && displayFields.length > 0 && (
        <div className="space-y-3 pt-1 border-t border-muted">
          {sections.map(section => {
            if (!section.title) return <div key={section.key}>{renderFields(section.fields)}</div>;
            const isExpanded = expandedGroups.has(section.key);
            return (
              <div key={section.key} className="border border-stroke rounded-lg overflow-hidden">
                <button type="button" onClick={() => toggleGroup(section.key)}
                  className="w-full flex items-center gap-2 px-3 py-2.5 bg-base hover:bg-muted transition-colors text-left">
                  {isExpanded ? <ChevronUp size={13} className="text-fg4 shrink-0" /> : <ChevronDown size={13} className="text-fg4 shrink-0" />}
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
          {isPending ? (entry ? 'Сохранение...' : 'Создание...') : (entry ? 'Сохранить' : 'Создать')}
        </button>
      </div>
    </form>
  );
}

// ─── Page ─────────────────────────────────────────────────────────────────────

export function SystemCommonDataPage() {
  const [addOpen, setAddOpen] = useState(false);
  const [editEntry, setEditEntry] = useState<CommonDataEntry | null>(null);
  const [search, setSearch] = useState('');
  const [filterTypeId, setFilterTypeId] = useState('');
  const [expandedTypes, setExpandedTypes] = useState<Set<string>>(new Set());
  const [deleteTarget, setDeleteTarget] = useState<CommonDataEntry | null>(null);

  function toggleType(id: string) {
    setExpandedTypes(prev => { const n = new Set(prev); n.has(id) ? n.delete(id) : n.add(id); return n; });
  }

  const { data: allDocTypes = [] } = useListDocumentTypes();
  const { data: enumTypes = [] } = useListEnumTypes();
  const { data: entries = [], isLoading } = useListCommonData({ scope: 'System' });
  const deleteMutation = useDeleteCommonDataEntry();

  const compositeTypes = allDocTypes.filter(dt => dt.kind === 'Composite');
  const documentTypes = allDocTypes.filter(dt => dt.kind === 'Document' && !dt.isAbstract);
  const allSelectableTypes = [...compositeTypes, ...documentTypes];

  const filtered = entries.filter(e => {
    const matchSearch = !search || e.displayName.toLowerCase().includes(search.toLowerCase());
    const matchType = !filterTypeId || e.compositeTypeId === filterTypeId;
    return matchSearch && matchType;
  }).sort((a, b) => {
    const typeCmp = (allSelectableTypes.find(t => t.id === a.compositeTypeId)?.name ?? '').localeCompare(
      allSelectableTypes.find(t => t.id === b.compositeTypeId)?.name ?? '');
    return typeCmp !== 0 ? typeCmp : a.displayName.localeCompare(b.displayName);
  });

  // Group by type
  const grouped = allSelectableTypes
    .map(t => ({ t, items: filtered.filter(e => e.compositeTypeId === t.id) }))
    .filter(g => g.items.length > 0);
  const noType = filtered.filter(e => !allSelectableTypes.find(t => t.id === e.compositeTypeId));

  return (
    <div className="px-6 py-4 max-w-4xl mx-auto">
      <div className="flex items-center justify-between mb-4">
        <div>
          <h1 className="text-xl font-semibold text-fg1">Системный каталог</h1>
          <p className="text-xs text-fg3 mt-0.5">Общие данные, доступные во всех проектах (приоритет 5)</p>
        </div>
        <button onClick={() => setAddOpen(true)}
          className="flex items-center gap-2 bg-brand hover:bg-brand-hover text-white text-sm font-medium px-4 py-2 rounded-md transition-colors">
          <Plus size={16} /> Добавить запись
        </button>
      </div>

      {/* Filters */}
      <div className="flex gap-3 mb-5">
        <div className="relative flex-1 max-w-sm">
          <Search size={14} className="absolute left-3 top-1/2 -translate-y-1/2 text-fg4" />
          <input value={search} onChange={e => setSearch(e.target.value)}
            placeholder="Поиск по названию..."
            className="w-full border border-stroke-strong rounded-md pl-9 pr-3 py-2 text-sm focus:outline-none focus-visible:ring-2 focus-visible:ring-brand bg-surface" />
        </div>
        {allSelectableTypes.length > 0 && (
          <select value={filterTypeId} onChange={e => setFilterTypeId(e.target.value)}
            className="border border-stroke-strong rounded-md px-3 py-2 text-sm text-fg1 bg-surface focus:outline-none focus-visible:ring-2 focus-visible:ring-brand">
            <option value="">Все типы</option>
            {compositeTypes.length > 0 && (
              <optgroup label="Составные типы">
                {compositeTypes.map(ct => <option key={ct.id} value={ct.id}>{ct.name}</option>)}
              </optgroup>
            )}
            {documentTypes.length > 0 && (
              <optgroup label="Типы документов">
                {documentTypes.map(dt => <option key={dt.id} value={dt.id}>{dt.name}</option>)}
              </optgroup>
            )}
          </select>
        )}
      </div>

      {isLoading ? (
        <div className="text-center py-12 text-fg4 text-sm">Загрузка...</div>
      ) : entries.length === 0 ? (
        <div className="text-center py-16">
          <p className="text-fg4 text-sm mb-2">Системный каталог пуст</p>
          <p className="text-xs text-stroke-strong">Добавьте справочные данные, которые будут доступны во всех проектах</p>
        </div>
      ) : filtered.length === 0 ? (
        <div className="text-center py-10 text-fg4 text-sm">Ничего не найдено</div>
      ) : (
        <div className="space-y-2">
          {grouped.map(({ t, items }) => {
            const isOpen = expandedTypes.has(t.id);
            return (
              <div key={t.id} className="border border-stroke rounded-xl overflow-hidden">
                <button onClick={() => toggleType(t.id)}
                  className="w-full flex items-center gap-2 px-4 py-3 bg-surface hover:bg-base transition-colors text-left">
                  {isOpen
                    ? <ChevronUp size={14} className="text-fg4 shrink-0" />
                    : <ChevronDown size={14} className="text-fg4 shrink-0" />}
                  <span className="flex-1 text-sm font-medium text-fg2">{t.name}</span>
                  {t.kind === 'Document' && (
                    <span className="text-xs bg-warning-subtle text-warning border border-warning-border px-1.5 py-0.5 rounded-full">внеш. документ</span>
                  )}
                  <span className="text-xs text-fg4 font-mono">{t.code}</span>
                  <span className="text-xs text-fg4 ml-1">{items.length}</span>
                </button>
                {isOpen && (
                  <div className="border-t border-stroke">
                    {items.map((entry, idx) => (
                      <div key={entry.id}
                        className={`flex items-center gap-4 px-4 py-3 group hover:bg-base transition-colors ${idx > 0 ? 'border-t border-muted' : ''}`}>
                        <span className="flex-1 text-sm font-medium text-fg1 truncate">{entry.displayName}</span>
                        {Object.keys(entry.data).length > 0 && (
                          <span className="text-xs text-fg4 truncate max-w-xs hidden sm:block">
                            {Object.entries(entry.data).filter(([, v]) => v != null && v !== '').slice(0, 3).map(([k, v]) => `${k}: ${v}`).join(' · ')}
                          </span>
                        )}
                        <div className="flex items-center gap-1 opacity-0 group-hover:opacity-100 transition-opacity shrink-0">
                          <button onClick={() => setEditEntry(entry)}
                            className="p-1.5 text-fg4 hover:text-fg2 rounded transition-colors" title="Редактировать">
                            <Pencil size={13} />
                          </button>
                          <button
                            onClick={() => setDeleteTarget(entry)}
                            disabled={deleteMutation.isPending}
                            className="p-1.5 text-fg4 hover:text-danger rounded transition-colors disabled:opacity-30" title="Удалить">
                            <Trash2 size={13} />
                          </button>
                        </div>
                      </div>
                    ))}
                  </div>
                )}
              </div>
            );
          })}
          {noType.length > 0 && (() => {
            const isOpen = expandedTypes.has('__no_type__');
            return (
              <div className="border border-stroke rounded-xl overflow-hidden">
                <button onClick={() => toggleType('__no_type__')}
                  className="w-full flex items-center gap-2 px-4 py-3 bg-surface hover:bg-base transition-colors text-left">
                  {isOpen
                    ? <ChevronUp size={14} className="text-fg4 shrink-0" />
                    : <ChevronDown size={14} className="text-fg4 shrink-0" />}
                  <span className="flex-1 text-sm font-medium text-fg3 italic">Без типа</span>
                  <span className="text-xs text-fg4">{noType.length}</span>
                </button>
                {isOpen && (
                  <div className="border-t border-stroke">
                    {noType.map((entry, idx) => (
                      <div key={entry.id}
                        className={`flex items-center gap-4 px-4 py-3 group hover:bg-base ${idx > 0 ? 'border-t border-muted' : ''}`}>
                        <span className="flex-1 text-sm font-medium text-fg1">{entry.displayName}</span>
                        <div className="flex gap-1 opacity-0 group-hover:opacity-100 shrink-0">
                          <button onClick={() => setEditEntry(entry)} className="p-1.5 text-fg4 hover:text-fg2 rounded"><Pencil size={13} /></button>
                          <button onClick={() => setDeleteTarget(entry)} className="p-1.5 text-fg4 hover:text-danger rounded"><Trash2 size={13} /></button>
                        </div>
                      </div>
                    ))}
                  </div>
                )}
              </div>
            );
          })()}
        </div>
      )}

      <Modal open={addOpen} onOpenChange={setAddOpen} title="Новая запись" wide flushBody>
        {addOpen && (
          <EntryForm entry={null} compositeTypes={compositeTypes} documentTypes={documentTypes} allDocTypes={allDocTypes} enumTypes={enumTypes} onClose={() => setAddOpen(false)} />
        )}
      </Modal>
      <Modal open={!!editEntry} onOpenChange={o => { if (!o) setEditEntry(null); }} title="Редактировать запись" wide flushBody>
        {editEntry && (
          <EntryForm entry={editEntry} compositeTypes={compositeTypes} documentTypes={documentTypes} allDocTypes={allDocTypes} enumTypes={enumTypes} onClose={() => setEditEntry(null)} />
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
