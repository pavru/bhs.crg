import { useState } from 'react';
import { FileText } from 'lucide-react';
import { Modal } from '@/shared/ui/Modal';
import { useCommonDataForSet, useListCommonData } from '@/shared/api/commonData';
import type {
  CommonDataEntry, CommonDataEntryWithScope, DocumentInstance, DocumentType, FieldRef, CatalogScope,
} from '@/shared/api/types';
import { SCOPE_LABELS } from '@/shared/api/types';
import { resolveEffectiveFields, isSubtypeOf, type SchemaField } from '@/shared/api/schema';
import { SCOPE_COLORS } from './constants';

export function RefPickerModal({
  open, onOpenChange, compositeType,
  setId, scope, scopeId,
  otherInstances = [], allDocTypes, onSelect,
}: {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  compositeType: DocumentType | null;
  setId?: string;
  scope?: CatalogScope;
  scopeId?: string | null;
  otherInstances?: DocumentInstance[];
  allDocTypes: DocumentType[];
  onSelect: (ref: FieldRef) => void;
}) {
  const [search, setSearch] = useState('');

  const { data: setCatalogEntries = [] } = useCommonDataForSet({
    setId: setId ?? '',
    enabled: open && !!setId,
  });

  const { data: scopeEntries = [] } = useListCommonData({
    scope, scopeId: scopeId ?? undefined,
    enabled: open && !setId && !!scope && scope !== 'System',
  });
  const { data: systemFallback = [] } = useListCommonData({
    scope: 'System',
    enabled: open && !setId,
  });

  const catalogEntries: CommonDataEntry[] = setId
    ? setCatalogEntries
    : [...scopeEntries, ...systemFallback.filter(e => !scopeEntries.some(s => s.id === e.id))];

  const filtered = catalogEntries.filter(e => {
    if (compositeType && !isSubtypeOf(e.compositeTypeId, compositeType.id, allDocTypes)) return false;
    return e.displayName.toLowerCase().includes(search.toLowerCase());
  });

  const docSources = compositeType && setId
    ? otherInstances.flatMap(inst => {
        const dt = allDocTypes.find(t => t.id === inst.documentTypeId);
        if (!dt) return [];
        const fields = resolveEffectiveFields(dt, allDocTypes).filter(
          f => f.type === 'complex' && f.typeId === compositeType.id,
        );
        return fields.map(f => ({ inst, dt, field: f }));
      })
    : [];

  function selectCatalog(entry: CommonDataEntry) {
    onSelect({
      $ref: 'catalog',
      entryId: entry.id,
      displayName: entry.displayName,
      scope: entry.scope,
    });
    onOpenChange(false);
  }

  function selectDocument(inst: DocumentInstance, dt: DocumentType, field: SchemaField) {
    onSelect({
      $ref: 'document',
      instanceId: inst.id,
      fieldKey: field.key,
      displayName: `${dt.name} → ${field.title}`,
    });
    onOpenChange(false);
  }

  return (
    <Modal open={open} onOpenChange={onOpenChange} title="Выбрать объект">
      <div className="space-y-4">
        <input
          value={search} onChange={e => setSearch(e.target.value)}
          placeholder="Поиск..."
          className="w-full border border-stroke-strong rounded-md px-3 py-2 text-sm focus:outline-none focus-visible:ring-2 focus-visible:ring-brand bg-surface"
          autoFocus
        />

        {filtered.length > 0 && (
          <div>
            <p className="text-xs font-medium text-fg3 uppercase tracking-wide mb-2">
              Каталог общих данных
            </p>
            <div className="space-y-1 max-h-64 overflow-y-auto">
              {filtered.map(entry => (
                <button key={entry.id} onClick={() => selectCatalog(entry)}
                  className="w-full flex items-center gap-3 px-3 py-2 text-sm text-left rounded-md hover:bg-brand-subtle transition-colors">
                  <span className={`text-xs px-1.5 py-0.5 rounded font-medium shrink-0 ${SCOPE_COLORS[entry.scope]}`}>
                    {SCOPE_LABELS[entry.scope]}
                  </span>
                  <span className="flex-1 font-medium text-fg1 truncate">{entry.displayName}</span>
                  {'priority' in entry && (
                    <span className="text-xs text-fg4 shrink-0">приоритет {(entry as CommonDataEntryWithScope).priority}</span>
                  )}
                </button>
              ))}
            </div>
          </div>
        )}

        {docSources.length > 0 && (
          <div>
            <p className="text-xs font-medium text-fg3 uppercase tracking-wide mb-2">
              Из других документов комплекта
            </p>
            <div className="space-y-1">
              {docSources.map(({ inst, dt, field }) => (
                <button key={`${inst.id}-${field.key}`}
                  onClick={() => selectDocument(inst, dt, field)}
                  className="w-full flex items-center gap-3 px-3 py-2 text-sm text-left rounded-md hover:bg-brand-subtle transition-colors">
                  <FileText size={14} className="text-fg4 shrink-0" />
                  <span className="flex-1 font-medium text-fg1 truncate">
                    {dt.name} → {field.title}
                  </span>
                </button>
              ))}
            </div>
          </div>
        )}

        {filtered.length === 0 && docSources.length === 0 && (
          <p className="text-sm text-fg4 text-center py-4">
            Нет объектов доступных для ссылки.
            <br />
            <span className="text-xs">Добавьте записи в каталог общих данных.</span>
          </p>
        )}
      </div>
    </Modal>
  );
}
