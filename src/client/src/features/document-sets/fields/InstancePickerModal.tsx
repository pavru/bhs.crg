import { useState } from 'react';
import { Icon } from '@/shared/ui/Icon';
import { Description } from '@/shared/ui/icons';
import { Modal } from '@/shared/ui/Modal';
import { useCommonDataForSet } from '@/shared/api/commonData';
import type { CommonDataEntry, DocumentInstance, DocumentType, FieldRef } from '@/shared/api/types';
import { isSubtypeOf, type SchemaField } from '@/shared/api/schema';
import { STATUS_COLORS, STATUS_LABELS } from './constants';
import { useScopeGroups } from './catalogGroups';
import { ScopeGroupList } from './ScopeGroupList';

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
    setId: setId ?? '', purpose: 'choice', enabled: open && !!setId,
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

  // Каталог показываем так же, как «Из каталога» в шапке массива, — группами по уровню с бейджами
  // (issue #751). Множество записей у обоих входов одно и то же: и for-set, и for-scope идут вверх
  // по цепочке Комплект→Раздел→Стройка→Система. Разной была только подача, и человек не мог
  // сопоставить список, открытый одной кнопкой, со списком, открытым другой.
  const { groups, isExpanded, toggle } = useScopeGroups(filteredCatalog, search.trim().length > 0);

  return (
    <Modal open={open} onOpenChange={onOpenChange} title="Выбрать документ" nothingToLose>
      <div className="space-y-4">
        <input
          value={search} onChange={e => setSearch(e.target.value)}
          placeholder="Поиск..."
          className="w-full border border-outline rounded-sm px-3 py-2 text-sm focus:outline-none focus-visible:ring-2 focus-visible:ring-primary bg-surface"
          autoFocus
        />
        {filteredCatalog.length > 0 && (
          <div>
            <p className="text-xs font-medium text-on-surface-variant uppercase tracking-wide mb-2">Каталог общих данных</p>
            <ScopeGroupList groups={groups} isExpanded={isExpanded} toggle={toggle}
              onSelect={selectCatalog} maxHeight="max-h-48" />
          </div>
        )}
        {filteredInstances.length > 0 && (
          <div>
            {/* Заголовок стоит всегда, а не только рядом с каталогом: тот же раздел в «Из каталога»
                подписан постоянно, и пропадающая подпись делала два списка неодинаковыми там, где
                они как раз про одно и то же (issue #751). */}
            <p className="text-xs font-medium text-on-surface-variant uppercase tracking-wide mb-2">Документы комплекта</p>
            <div className="space-y-1 max-h-48 overflow-y-auto">
              {filteredInstances.map(inst => {
                const dt = allDocTypes.find(t => t.id === inst.documentTypeId);
                return (
                  <button key={inst.id} onClick={() => selectInstance(inst)}
                    className="w-full flex items-center gap-3 px-3 py-2 text-sm text-left rounded-sm hover:bg-primary-container transition-colors">
                    <Icon icon={Description} size={18} className="text-on-surface-variant shrink-0" />
                    <span className="flex-1 min-w-0">
                      <span className="block font-medium text-on-surface truncate">
                        {inst.name || dt?.name || '—'}
                      </span>
                      {inst.name && (
                        <span className="block text-xs text-on-surface-variant truncate">{dt?.name}</span>
                      )}
                    </span>
                    <span className={`text-xs px-1.5 py-0.5 rounded-xs font-medium shrink-0 ${STATUS_COLORS[inst.status]}`}>
                      {STATUS_LABELS[inst.status]}
                    </span>
                  </button>
                );
              })}
            </div>
          </div>
        )}
        {!hasAny && (
          <p className="text-sm text-on-surface-variant text-center py-4">
            Нет документов для выбора.
          </p>
        )}
      </div>
    </Modal>
  );
}
