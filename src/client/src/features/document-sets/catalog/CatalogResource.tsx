import { useState } from 'react';
import { Plus, Search, ChevronDown, ChevronUp, Database } from 'lucide-react';
import { Modal } from '@/shared/ui/Modal';
import { Button } from '@/shared/ui/Button';
import { EmptyState } from '@/shared/ui/EmptyState';
import { Select, SelectItem, SelectGroup } from '@/shared/ui/Select';
import { ConfirmDialog } from '@/shared/ui/ConfirmDialog';
import { useListCommonData, useDeleteCommonDataEntry } from '@/shared/api/commonData';
import type { CommonDataEntry, CatalogScope, DocumentType } from '@/shared/api/types';
import { CatalogEntryForm } from './index';
import { groupObjectsByType, ObjectRow } from './ObjectsByTypeList';

/** Sentinel для «Все типы» — Radix Select запрещает пустую строку как value. */
const ALL_TYPES = '__all__';

/**
 * Богатый браузер каталога общих данных для ЛЮБОГО scope (issue #210, ось видимости): поиск + фильтр по
 * типу + группы по типу + add/edit/delete. Единый компонент для system-страницы и scoped-панелей — область
 * выражается положением (страница/панель), НЕ чипом. Заголовок/контекст даёт вызывающий.
 */
export function CatalogResource({ scope, scopeId, allDocTypes }: {
  scope: CatalogScope; scopeId: string | null; allDocTypes: DocumentType[];
}) {
  const [addOpen, setAddOpen] = useState(false);
  const [editEntry, setEditEntry] = useState<CommonDataEntry | null>(null);
  const [search, setSearch] = useState('');
  const [filterTypeId, setFilterTypeId] = useState('');
  const [expandedTypes, setExpandedTypes] = useState<Set<string>>(new Set());
  const [deleteTarget, setDeleteTarget] = useState<CommonDataEntry | null>(null);

  function toggleType(id: string) {
    setExpandedTypes(prev => { const n = new Set(prev); n.has(id) ? n.delete(id) : n.add(id); return n; });
  }

  const { data: entries = [], isLoading } = useListCommonData({ scope, scopeId: scopeId ?? undefined });
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
  const { groups, noType } = groupObjectsByType(filtered, allSelectableTypes);

  return (
    <div>
      {/* Панель инструментов: поиск + фильтр по типу + «Добавить» */}
      <div className="flex items-center gap-3 mb-4">
        <div className="relative flex-1 max-w-sm">
          <Search size={14} className="absolute left-3 top-1/2 -translate-y-1/2 text-fg4" />
          <input value={search} onChange={e => setSearch(e.target.value)} placeholder="Поиск по названию..."
            className="w-full border border-stroke-strong rounded-md pl-9 pr-3 py-2 text-sm focus:outline-none focus-visible:ring-2 focus-visible:ring-brand bg-surface" />
        </div>
        {allSelectableTypes.length > 0 && (
          <Select value={filterTypeId || ALL_TYPES} onValueChange={v => setFilterTypeId(v === ALL_TYPES ? '' : v)}
            aria-label="Фильтр по типу" className="w-56">
            <SelectItem value={ALL_TYPES}>Все типы</SelectItem>
            {compositeTypes.length > 0 && (
              <SelectGroup label="Составные типы">
                {compositeTypes.map(ct => <SelectItem key={ct.id} value={ct.id}>{ct.name}</SelectItem>)}
              </SelectGroup>
            )}
            {documentTypes.length > 0 && (
              <SelectGroup label="Типы документов">
                {documentTypes.map(dt => <SelectItem key={dt.id} value={dt.id}>{dt.name}</SelectItem>)}
              </SelectGroup>
            )}
          </Select>
        )}
        <span className="flex-1" />
        <Button variant="filled" size="sm" icon={<Plus size={16} />} onClick={() => setAddOpen(true)}>Добавить запись</Button>
      </div>

      {isLoading ? (
        <div className="text-center py-12 text-fg4 text-sm">Загрузка...</div>
      ) : entries.length === 0 ? (
        <EmptyState icon={<Database size={30} />} title="Каталог пуст"
          description="Добавьте справочные данные (организации, лица, объекты) этого уровня."
          action={<Button variant="filled" icon={<Plus size={16} />} onClick={() => setAddOpen(true)}>Добавить запись</Button>} />
      ) : filtered.length === 0 ? (
        <div className="text-center py-10 text-fg4 text-sm">Ничего не найдено</div>
      ) : (
        <div className="space-y-2">
          {groups.map(({ type: t, items }) => {
            const isOpen = expandedTypes.has(t.id);
            return (
              <div key={t.id} className="border border-stroke rounded-xl overflow-hidden">
                <button type="button" onClick={() => toggleType(t.id)} aria-expanded={isOpen}
                  className="w-full flex items-center gap-2 px-4 py-3 bg-surface hover:bg-base transition-colors text-left">
                  {isOpen ? <ChevronUp size={14} className="text-fg4 shrink-0" /> : <ChevronDown size={14} className="text-fg4 shrink-0" />}
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
                      <ObjectRow key={entry.id} entry={entry} siblings={items}
                        onEdit={setEditEntry} onDelete={setDeleteTarget} deleteDisabled={deleteMutation.isPending}
                        showPreview className={idx > 0 ? 'border-t border-muted' : ''} />
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
                <button type="button" onClick={() => toggleType('__no_type__')} aria-expanded={isOpen}
                  className="w-full flex items-center gap-2 px-4 py-3 bg-surface hover:bg-base transition-colors text-left">
                  {isOpen ? <ChevronUp size={14} className="text-fg4 shrink-0" /> : <ChevronDown size={14} className="text-fg4 shrink-0" />}
                  <span className="flex-1 text-sm font-medium text-fg3 italic">Без типа</span>
                  <span className="text-xs text-fg4">{noType.length}</span>
                </button>
                {isOpen && (
                  <div className="border-t border-stroke">
                    {noType.map((entry, idx) => (
                      <ObjectRow key={entry.id} entry={entry} siblings={noType}
                        onEdit={setEditEntry} onDelete={setDeleteTarget} deleteDisabled={deleteMutation.isPending}
                        className={idx > 0 ? 'border-t border-muted' : ''} />
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
          <CatalogEntryForm entry={null} compositeTypes={compositeTypes} documentTypes={documentTypes}
            allDocTypes={allDocTypes} scope={scope} scopeId={scopeId} onClose={() => setAddOpen(false)} />
        )}
      </Modal>
      <Modal open={!!editEntry} onOpenChange={o => { if (!o) setEditEntry(null); }} title="Редактировать запись" wide flushBody>
        {editEntry && (
          <CatalogEntryForm entry={editEntry} compositeTypes={compositeTypes} documentTypes={documentTypes}
            allDocTypes={allDocTypes} scope={scope} scopeId={scopeId} onClose={() => setEditEntry(null)} />
        )}
      </Modal>
      <ConfirmDialog open={!!deleteTarget} onOpenChange={o => { if (!o) setDeleteTarget(null); }}
        title={`Удалить «${deleteTarget?.displayName ?? ''}»?`} confirmLabel="Удалить"
        onConfirm={() => { if (deleteTarget) deleteMutation.mutate(deleteTarget.id); }} />
    </div>
  );
}
