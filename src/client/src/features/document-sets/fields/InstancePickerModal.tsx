import { useState } from 'react';
import { FileText } from 'lucide-react';
import { Modal } from '@/shared/ui/Modal';
import { useCommonDataForSet } from '@/shared/api/commonData';
import type { CommonDataEntry, DocumentInstance, DocumentType, FieldRef } from '@/shared/api/types';
import { SCOPE_LABELS } from '@/shared/api/types';
import { isSubtypeOf, type SchemaField } from '@/shared/api/schema';
import { STATUS_COLORS, STATUS_LABELS } from './constants';

export function InstancePickerModal({ open, onOpenChange, field, allDocTypes, otherInstances, setId, onSelect }: {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  field: SchemaField;
  allDocTypes: DocumentType[];
  otherInstances: DocumentInstance[];
  setId?: string;
  onSelect: (ref: FieldRef) => void;
}) {
  const [search, setSearch] = useState('');

  const { data: setCatalogEntries = [] } = useCommonDataForSet({
    setId: setId ?? '', enabled: open && !!setId,
  });

  const filteredInstances = otherInstances.filter(inst => {
    if (field.typeId && !isSubtypeOf(inst.documentTypeId, field.typeId, allDocTypes)) return false;
    const dt = allDocTypes.find(t => t.id === inst.documentTypeId);
    const q = search.toLowerCase();
    return (inst.name ?? '').toLowerCase().includes(q) || (dt?.name ?? '').toLowerCase().includes(q);
  });

  const filteredCatalog = setCatalogEntries.filter(e => {
    if (!field.typeId || !isSubtypeOf(e.compositeTypeId, field.typeId, allDocTypes)) return false;
    return !search || e.displayName.toLowerCase().includes(search.toLowerCase());
  });

  function selectInstance(inst: DocumentInstance) {
    const dt = allDocTypes.find(t => t.id === inst.documentTypeId);
    const displayName = inst.name
      ? `${inst.name} (${dt?.name ?? 'Документ'})`
      : (dt?.name ?? 'Документ');
    onSelect({ $ref: 'instance', instanceId: inst.id, displayName });
    onOpenChange(false);
  }

  function selectCatalog(entry: CommonDataEntry) {
    onSelect({ $ref: 'catalog', entryId: entry.id, displayName: entry.displayName, scope: entry.scope });
    onOpenChange(false);
  }

  const hasAny = filteredInstances.length > 0 || filteredCatalog.length > 0;

  return (
    <Modal open={open} onOpenChange={onOpenChange} title="Выбрать документ">
      <div className="space-y-4">
        <input
          value={search} onChange={e => setSearch(e.target.value)}
          placeholder="Поиск..."
          className="w-full border border-stroke-strong rounded-md px-3 py-2 text-sm focus:outline-none focus-visible:ring-2 focus-visible:ring-brand bg-surface"
          autoFocus
        />
        {filteredCatalog.length > 0 && (
          <div>
            <p className="text-xs font-medium text-fg3 uppercase tracking-wide mb-2">Из общих данных</p>
            <div className="space-y-1 max-h-48 overflow-y-auto">
              {filteredCatalog.map(entry => (
                <button key={entry.id} onClick={() => selectCatalog(entry)}
                  className="w-full flex items-center gap-3 px-3 py-2 text-sm text-left rounded-md hover:bg-warning-subtle transition-colors">
                  <FileText size={14} className="text-warning shrink-0" />
                  <span className="flex-1 min-w-0">
                    <span className="block font-medium text-fg1 truncate">{entry.displayName}</span>
                    <span className="block text-xs text-fg4 truncate">{SCOPE_LABELS[entry.scope]}</span>
                  </span>
                </button>
              ))}
            </div>
          </div>
        )}
        {filteredInstances.length > 0 && (
          <div>
            {filteredCatalog.length > 0 && (
              <p className="text-xs font-medium text-fg3 uppercase tracking-wide mb-2">Из комплектов стройки</p>
            )}
            <div className="space-y-1 max-h-48 overflow-y-auto">
              {filteredInstances.map(inst => {
                const dt = allDocTypes.find(t => t.id === inst.documentTypeId);
                return (
                  <button key={inst.id} onClick={() => selectInstance(inst)}
                    className="w-full flex items-center gap-3 px-3 py-2 text-sm text-left rounded-md hover:bg-indigo-50 transition-colors">
                    <FileText size={14} className="text-fg4 shrink-0" />
                    <span className="flex-1 min-w-0">
                      <span className="block font-medium text-fg1 truncate">
                        {inst.name || dt?.name || '—'}
                      </span>
                      {inst.name && (
                        <span className="block text-xs text-fg4 truncate">{dt?.name}</span>
                      )}
                    </span>
                    <span className={`text-xs px-1.5 py-0.5 rounded font-medium shrink-0 ${STATUS_COLORS[inst.status]}`}>
                      {STATUS_LABELS[inst.status]}
                    </span>
                  </button>
                );
              })}
            </div>
          </div>
        )}
        {!hasAny && (
          <p className="text-sm text-fg4 text-center py-4">
            Нет документов для выбора.
          </p>
        )}
      </div>
    </Modal>
  );
}
