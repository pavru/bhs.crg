import { useState, type ReactNode } from 'react';
import { useSearchParams } from 'react-router-dom';
import { Plus, Search, ChevronDown, ChevronUp, Database, FileText, Layers, X } from 'lucide-react';
import { Modal } from '@/shared/ui/Modal';
import { Button } from '@/shared/ui/Button';
import { SearchInput } from '@/shared/ui/SearchInput';
import { EmptyState } from '@/shared/ui/EmptyState';
import { ConfirmDialog } from '@/shared/ui/ConfirmDialog';
import { useListCommonData, useDeleteCommonDataEntry, useCommonDataForScope } from '@/shared/api/commonData';
import type { CommonDataEntry, CatalogScope, DocumentType } from '@/shared/api/types';
import { CatalogEntryForm } from './index';
import { groupObjectsByType, entryMatchesQuery, ObjectRow } from './ObjectsByTypeList';

const NO_TYPE = '__no_type__';
/** Порог, с которого над списком типов появляется мини-поиск (NN/g: фасеты с поиском при большом числе). */
const TYPE_SEARCH_THRESHOLD = 12;

/**
 * Богатый браузер каталога общих данных для ЛЮБОГО scope (issue #210, ось видимости): слева — вертикальный
 * рейл типов со счётчиками («Все записи» + типы, мини-поиск при большом числе; вариант B — рейл живёт ВНУТРИ
 * компонента, один на всех 4 уровнях), справа — поиск + записи (все группами / выбранный тип плоско).
 * Выбранный тип — в URL (`?type=id`, deep-link). Область выражается положением, заголовок даёт вызывающий.
 */
export function CatalogResource({ scope, scopeId, allDocTypes }: {
  scope: CatalogScope; scopeId: string | null; allDocTypes: DocumentType[];
}) {
  const [addOpen, setAddOpen] = useState(false);
  const [editEntry, setEditEntry] = useState<CommonDataEntry | null>(null);
  const [search, setSearch] = useState('');
  const [typeSearch, setTypeSearch] = useState('');
  const [expandedTypes, setExpandedTypes] = useState<Set<string>>(new Set());
  const [deleteTarget, setDeleteTarget] = useState<CommonDataEntry | null>(null);

  // Выбранный тип — в URL (?type=id), deep-link/back-forward. Пустой = «Все записи».
  const [searchParams, setSearchParams] = useSearchParams();
  const filterTypeId = searchParams.get('type') ?? '';
  const setFilterTypeId = (id: string) => setSearchParams(prev => {
    const next = new URLSearchParams(prev);
    if (id) next.set('type', id); else next.delete('type');
    return next;
  }, { replace: true });

  function toggleType(id: string) {
    setExpandedTypes(prev => { const n = new Set(prev); n.has(id) ? n.delete(id) : n.add(id); return n; });
  }

  const { data: entries = [], isLoading } = useListCommonData({ scope, scopeId: scopeId ?? undefined });
  // Пул для резолва прокси-цели: вся scope-цепочка (текущий уровень + предки) — кросс-scope прокси (#89).
  const { data: scopeChain = [] } = useCommonDataForScope({ scope, scopeId });
  const deleteMutation = useDeleteCommonDataEntry();

  const compositeTypes = allDocTypes.filter(dt => dt.kind === 'Composite');
  const documentTypes = allDocTypes.filter(dt => dt.kind === 'Document' && !dt.isAbstract);
  const allSelectableTypes = [...compositeTypes, ...documentTypes];
  const isDocType = (id: string) => documentTypes.some(dt => dt.id === id);

  // Рейл типов: счётчики по всем записям уровня (независимо от текстового поиска и выбранного типа).
  const rail = groupObjectsByType(entries, allSelectableTypes);
  const tq = typeSearch.trim().toLowerCase();
  const railGroups = tq ? rail.groups.filter(g => g.type.name.toLowerCase().includes(tq)) : rail.groups;

  const selectedType = filterTypeId && filterTypeId !== NO_TYPE
    ? allSelectableTypes.find(t => t.id === filterTypeId) : undefined;

  // Записи справа: фильтр по тексту + выбранному типу.
  const matchType = (e: CommonDataEntry) =>
    !filterTypeId ? true
      : filterTypeId === NO_TYPE ? !allSelectableTypes.some(t => t.id === e.compositeTypeId)
        : e.compositeTypeId === filterTypeId;
  // Текстовый матч комплексный (issue #249): имя записи + имя типа + значения скалярных полей — см.
  // entryMatchesQuery. Раньше искали только по displayName (искали «орга» → тип «Организация» не находился).
  const matchText = (e: CommonDataEntry) =>
    entryMatchesQuery(e, allSelectableTypes.find(t => t.id === e.compositeTypeId)?.name, search);
  const filtered = entries.filter(e => matchText(e) && matchType(e))
    .sort((a, b) => {
      const typeCmp = (allSelectableTypes.find(t => t.id === a.compositeTypeId)?.name ?? '').localeCompare(
        allSelectableTypes.find(t => t.id === b.compositeTypeId)?.name ?? '');
      return typeCmp !== 0 ? typeCmp : a.displayName.localeCompare(b.displayName);
    });
  const { groups, noType } = groupObjectsByType(filtered, allSelectableTypes);

  const row = (entry: CommonDataEntry, siblings: CommonDataEntry[], border: boolean) => (
    <ObjectRow key={entry.id} entry={entry} siblings={siblings} resolvePool={scopeChain}
      onEdit={setEditEntry} onDelete={setDeleteTarget} deleteDisabled={deleteMutation.isPending}
      showPreview className={border ? 'border-t border-muted' : ''} />
  );

  return (
    <div className="flex gap-5 items-start">
      {/* Рейл типов */}
      <aside className="w-48 shrink-0 sticky top-0 self-start space-y-0.5">
        {rail.groups.length > TYPE_SEARCH_THRESHOLD && (
          <div className="relative mb-1">
            <Search size={13} className="absolute left-2.5 top-1/2 -translate-y-1/2 text-fg4 pointer-events-none" />
            <input value={typeSearch} onChange={e => setTypeSearch(e.target.value)} placeholder="Тип…" aria-label="Поиск типа"
              className="w-full h-8 pl-7 pr-6 rounded-md text-xs bg-surface border border-stroke text-fg1 outline-none focus-visible:ring-2 focus-visible:ring-brand placeholder:text-fg4" />
            {typeSearch && (
              <button onClick={() => setTypeSearch('')} aria-label="Очистить" className="absolute right-1.5 top-1/2 -translate-y-1/2 text-fg4 hover:text-fg2">
                <X size={12} />
              </button>
            )}
          </div>
        )}
        <TypeNavItem icon={<Layers size={15} />} label="Все записи" count={entries.length}
          active={!filterTypeId} onClick={() => setFilterTypeId('')} />
        {railGroups.map(({ type: t, items }) => (
          <TypeNavItem key={t.id}
            icon={isDocType(t.id) ? <FileText size={15} /> : <Database size={15} />}
            label={t.name} count={items.length} doc={isDocType(t.id)}
            active={filterTypeId === t.id} onClick={() => setFilterTypeId(t.id)} />
        ))}
        {rail.noType.length > 0 && !tq && (
          <TypeNavItem label="Без типа" count={rail.noType.length} muted
            active={filterTypeId === NO_TYPE} onClick={() => setFilterTypeId(NO_TYPE)} />
        )}
      </aside>

      {/* Записи */}
      <div className="flex-1 min-w-0">
        <div className="flex items-center gap-3 mb-4">
          <div className="flex-1 max-w-sm">
            <SearchInput value={search} onChange={setSearch}
              placeholder={selectedType ? `Поиск: ${selectedType.name}…` : 'Поиск по каталогу…'} />
          </div>
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
        ) : filterTypeId ? (
          // Выбран один тип — плоский список без аккордеона (тип уже виден в рейле).
          <div className="border border-stroke rounded-xl overflow-hidden">
            <div className="flex items-center gap-2 px-4 py-3 bg-surface border-b border-stroke">
              <span className="flex-1 text-sm font-medium text-fg2">{selectedType?.name ?? 'Без типа'}</span>
              {selectedType && isDocType(selectedType.id) && (
                <span className="text-xs bg-warning-subtle text-warning border border-warning-border px-1.5 py-0.5 rounded-full">внеш. документ</span>
              )}
              <span className="text-xs text-fg4">{filtered.length}</span>
            </div>
            <div>{filtered.map((e, idx) => row(e, filtered, idx > 0))}</div>
          </div>
        ) : (
          // «Все записи» — группы-аккордеоны по типу.
          <div className="space-y-2">
            {groups.map(({ type: t, items }) => {
              const isOpen = expandedTypes.has(t.id);
              return (
                <div key={t.id} className="border border-stroke rounded-xl overflow-hidden">
                  <button type="button" onClick={() => toggleType(t.id)} aria-expanded={isOpen}
                    className="w-full flex items-center gap-2 px-4 py-3 bg-surface hover:bg-base transition-colors text-left">
                    {isOpen ? <ChevronUp size={14} className="text-fg4 shrink-0" /> : <ChevronDown size={14} className="text-fg4 shrink-0" />}
                    <span className="flex-1 text-sm font-medium text-fg2">{t.name}</span>
                    {isDocType(t.id) && (
                      <span className="text-xs bg-warning-subtle text-warning border border-warning-border px-1.5 py-0.5 rounded-full">внеш. документ</span>
                    )}
                    <span className="text-xs text-fg4 font-mono">{t.code}</span>
                    <span className="text-xs text-fg4 ml-1">{items.length}</span>
                  </button>
                  {isOpen && <div className="border-t border-stroke">{items.map((e, idx) => row(e, items, idx > 0))}</div>}
                </div>
              );
            })}
            {noType.length > 0 && (() => {
              const isOpen = expandedTypes.has(NO_TYPE);
              return (
                <div className="border border-stroke rounded-xl overflow-hidden">
                  <button type="button" onClick={() => toggleType(NO_TYPE)} aria-expanded={isOpen}
                    className="w-full flex items-center gap-2 px-4 py-3 bg-surface hover:bg-base transition-colors text-left">
                    {isOpen ? <ChevronUp size={14} className="text-fg4 shrink-0" /> : <ChevronDown size={14} className="text-fg4 shrink-0" />}
                    <span className="flex-1 text-sm font-medium text-fg3 italic">Без типа</span>
                    <span className="text-xs text-fg4">{noType.length}</span>
                  </button>
                  {isOpen && <div className="border-t border-stroke">{noType.map((e, idx) => row(e, noType, idx > 0))}</div>}
                </div>
              );
            })()}
          </div>
        )}
      </div>

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

/** Пункт вертикального рейла типов (issue #210, вариант B). Активный — заливка brand-subtle;
 *  документные типы (внешний каталог) — приглушённая иконка; счётчик tabular. */
function TypeNavItem({ icon, label, count, active, doc, muted, onClick }: {
  icon?: ReactNode; label: string; count: number; active?: boolean; doc?: boolean; muted?: boolean; onClick: () => void;
}) {
  return (
    <button type="button" onClick={onClick} aria-current={active ? 'true' : undefined}
      className={`w-full flex items-center gap-2 px-2.5 h-9 rounded-lg text-left transition-colors ${
        active ? 'bg-brand-subtle text-brand-hover font-medium' : 'text-fg2 hover:bg-muted'}`}>
      {icon && <span className={`shrink-0 ${active ? 'text-brand-hover' : doc ? 'text-warning' : 'text-fg4'}`}>{icon}</span>}
      <span className={`flex-1 truncate text-sm ${muted ? 'italic text-fg3' : ''}`}>{label}</span>
      <span className="text-xs text-fg4 tabular-nums shrink-0">{count}</span>
    </button>
  );
}
