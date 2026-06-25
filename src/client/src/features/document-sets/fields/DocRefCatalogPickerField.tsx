import { useState } from 'react';
import { FileText, Unlink } from 'lucide-react';
import type { CatalogScope, DocumentType, FieldRef } from '@/shared/api/types';
import { isFieldRef } from '@/shared/api/types';
import type { SchemaField } from '@/shared/api/schema';
import { RefPickerModal } from './RefPickerModal';

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
        <div className="flex items-center gap-2 border border-warning-border rounded-lg px-3 py-2 bg-warning-subtle">
          <FileText size={14} className="text-warning shrink-0" />
          <span className="flex-1 text-sm text-warning font-medium">{ref.displayName}</span>
          <button type="button" onClick={() => onChange(null)} title="Снять ссылку"
            className="p-1 text-warning hover:text-danger transition-colors">
            <Unlink size={13} />
          </button>
        </div>
      ) : (
        <button type="button" onClick={() => setPickerOpen(true)}
          className="flex items-center gap-2 px-3 py-2 text-sm text-warning border border-dashed border-warning-border rounded-lg hover:bg-warning-subtle transition-colors w-full">
          <FileText size={13} /> {docType ? `Выбрать: ${docType.name}...` : 'Выбрать документ...'}
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
