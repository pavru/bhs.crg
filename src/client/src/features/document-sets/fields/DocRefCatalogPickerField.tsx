import { useState } from 'react';
import { Icon } from '@/shared/ui/Icon';
import { Description, LinkOff, Refresh } from '@/shared/ui/icons';
import type { CatalogScope, DocumentType, FieldRef } from '@/shared/api/types';
import { isFieldRef } from '@/shared/api/types';
import type { SchemaField } from '@/shared/api/schema';
import { RefPickerModal } from './RefPickerModal';
import { Tooltip } from '@/shared/ui/Tooltip';

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
        <div className="flex items-center gap-2 border border-outline-variant rounded-md px-3 py-2 bg-warning-container">
          <Icon icon={Description} size={18} className="text-warning shrink-0" />
          <span className="flex-1 text-body-medium text-warning font-medium">{ref.displayName}</span>
          {/* «Заменить» рядом со «Снять» (issue #749): без неё замена шла в два шага, причём первый
              стирал то, на что смотришь, выбирая второй. Пикер тот же, что и при первом выборе. */}
          <Tooltip content="Заменить ссылку" name>
            <button type="button" onClick={() => setPickerOpen(true)}
              className="p-1 text-warning hover:text-primary transition-colors shrink-0">
              <Icon icon={Refresh} size={18} />
            </button>
          </Tooltip>
          <Tooltip content="Снять ссылку" name>
            <button type="button" onClick={() => onChange(null)}
              className="p-1 text-warning hover:text-error transition-colors">
              <Icon icon={LinkOff} size={18} />
            </button>
          </Tooltip>
        </div>
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
