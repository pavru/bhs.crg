import { useState } from 'react';
import { Plus, Pencil, Trash2, Search, ChevronDown, ChevronUp } from 'lucide-react';
import { Modal } from '@/shared/ui/Modal';
import { ConfirmDialog } from '@/shared/ui/ConfirmDialog';
import { useListDocumentTypes } from '@/shared/api/documentTypes';
import { useListCommonData, useDeleteCommonDataEntry } from '@/shared/api/commonData';
import type { CommonDataEntry } from '@/shared/api/types';
import { CatalogEntryForm } from '../document-sets/catalog';

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
          <CatalogEntryForm entry={null} compositeTypes={compositeTypes} documentTypes={documentTypes} allDocTypes={allDocTypes} scope="System" scopeId={null} onClose={() => setAddOpen(false)} />
        )}
      </Modal>
      <Modal open={!!editEntry} onOpenChange={o => { if (!o) setEditEntry(null); }} title="Редактировать запись" wide flushBody>
        {editEntry && (
          <CatalogEntryForm entry={editEntry} compositeTypes={compositeTypes} documentTypes={documentTypes} allDocTypes={allDocTypes} scope="System" scopeId={null} onClose={() => setEditEntry(null)} />
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
