import { useState } from 'react';
import { ListItem } from '@/shared/ui/ListItem';
import { Description } from '@/shared/ui/icons';
import { Modal } from '@/shared/ui/Modal';
import { useCommonDataForSet } from '@/shared/api/commonData';
import type { CommonDataEntry, DocumentInstance, DocumentType, FieldRef } from '@/shared/api/types';
import { isSubtypeOf, type SchemaField } from '@/shared/api/schema';
import { useScopeGroups } from './catalogGroups';
import { ScopeGroupList } from './ScopeGroupList';
import { DocumentStatusText } from './DocumentStatusText';
import { SearchInput } from '@/shared/ui/SearchInput';

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
        <SearchInput value={search} onChange={setSearch} ariaLabel="Поиск документа" autoFocus />
        {filteredCatalog.length > 0 && (
          <div>
            <p className="text-title-small text-on-surface-variant mb-2">Каталог общих данных</p>
            <ScopeGroupList groups={groups} isExpanded={isExpanded} toggle={toggle}
              onSelect={selectCatalog} maxHeight="max-h-48" />
          </div>
        )}
        {filteredInstances.length > 0 && (
          <div>
            {/* Заголовок стоит всегда, а не только рядом с каталогом: тот же раздел в «Из каталога»
                подписан постоянно, и пропадающая подпись делала два списка неодинаковыми там, где
                они как раз про одно и то же (issue #751). */}
            <p className="text-title-small text-on-surface-variant mb-2">Документы комплекта</p>
            {/* Строки — ListItem, как у каталога над ними ([UI-84]): два списка одного окна выглядят одинаково. */}
            <div className="-mx-4 max-h-48 overflow-y-auto">
              {filteredInstances.map(inst => {
                const dt = allDocTypes.find(t => t.id === inst.documentTypeId);
                return (
                  <ListItem key={inst.id} icon={Description} headline={inst.name || dt?.name || '—'}
                    supporting={inst.name ? dt?.name : undefined} trailing={<DocumentStatusText status={inst.status} />}
                    onClick={() => selectInstance(inst)} />
                );
              })}
            </div>
          </div>
        )}
        {!hasAny && (
          <p className="text-body-medium text-on-surface-variant text-center py-4">
            Нет документов для выбора.
          </p>
        )}
      </div>
    </Modal>
  );
}
