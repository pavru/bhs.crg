import { useState } from 'react';
import { Icon } from '@/shared/ui/Icon';
import { Database, Description } from '@/shared/ui/icons';
import type { CatalogScope, DocumentType, FieldRef } from '@/shared/api/types';
import { isFieldRef } from '@/shared/api/types';
import type { SchemaField } from '@/shared/api/schema';
import { RefPickerModal } from './RefPickerModal';
import { Chip } from '@/shared/ui/Chip';

export function DocRefCatalogPickerField({ field, allDocTypes, value, onChange, setId, scope, scopeId }: {
  field: SchemaField; allDocTypes: DocumentType[]; value: unknown;
  onChange: (v: FieldRef | null) => void;
  setId?: string; scope: CatalogScope; scopeId: string | null;
}) {
  const [pickerOpen, setPickerOpen] = useState(false);
  const ref = isFieldRef(value) && value.$ref === 'catalog' ? value : null;
  const docType = allDocTypes.find(dt => dt.id === field.typeId) ?? null;
  return (
    <div>
      {ref ? (
        // Значение — input chip со знаком общих данных (Р4): вид ссылки говорит знак, а не заливка
        // warning-container (тревоги в ней не было). «Заменить» — щелчок по чипу (issue #749): без неё
        // замена шла в два шага, причём первый стирал то, на что смотришь, выбирая второй.
        <Chip icon={Database} title="Заменить ссылку" onClick={() => setPickerOpen(true)}
          onRemove={() => onChange(null)} removeLabel="Снять ссылку" className="max-w-full">{ref.displayName}</Chip>
      ) : (
        <button type="button" onClick={() => setPickerOpen(true)}
          className="flex items-center gap-2 px-3 py-2 text-body-medium text-warning border border-dashed border-outline-variant rounded-md hover:bg-warning-container transition-colors w-full">
          <Icon icon={Description} size={18} /> {docType ? `Выбрать: ${docType.name}...` : 'Выбрать документ...'}
        </button>
      )}
      <RefPickerModal
        open={pickerOpen} onOpenChange={setPickerOpen}
        compositeType={docType}
        setId={setId} scope={scope} scopeId={scopeId}
        allDocTypes={allDocTypes}
        onSelect={r => onChange(r)}
      />
    </div>
  );
}
