/**
 * Форма создания типа — часть страницы типов документов.
 *
 * Выделено из `DocumentTypesPage.tsx` (issue #1031).
 */
import { useState } from 'react';
import { Icon } from '@/shared/ui/Icon';
import { DataObject } from '@/shared/ui/icons';
import { Button } from '@/shared/ui/Button';
import { TypePickerField } from '@/shared/ui/TypePickerField';
import { TextField } from '@/shared/ui/TextField';
import { Select, SelectItem } from '@/shared/ui/Select';
import { Checkbox } from '@/shared/ui/Checkbox';
import { NO_ACCESS, useAccess } from '@/shared/api/access';
import { CORE_OWNER, offeredTypes, ownerOptions } from '@/shared/api/typeOwners';
import { useCreateDocumentType } from '@/shared/api/documentTypes';
import { useListPrimitiveTypes } from '@/shared/api/primitiveTypes';
import { useListEnumTypes } from '@/shared/api/enumTypes';
import type { DocumentType, DocumentTypeKind } from '@/shared/api/types';
import { resolveEffectiveFields, type SchemaField } from '@/shared/api/schema';
import { schemaToJson, validateFields, TYPE_LABELS, nextAutoKey } from './schemaConstants';
import { JsonPreview, FieldBuilder } from './FieldBuilder';
import { toParentPickTypes } from './documentTypeHelpers';

// ─── Create form ───────────────────────────────────────────────────────────────

export function CreateForm({
  kind, onClose, onCreated, allDocTypes,
}: {
  kind: DocumentTypeKind;
  onClose: () => void;
  /** Созданный тип — страница выбирает его в list-detail (issue #383: открыть на редактирование). */
  onCreated: (created: DocumentType) => void;
  allDocTypes: DocumentType[];
}) {
  const { data: primitiveTypes = [] } = useListPrimitiveTypes();
  const { data: enumTypes = [] } = useListEnumTypes();
  const { data: access } = useAccess();
  const owners = ownerOptions(access ?? NO_ACCESS);
  const [name, setName] = useState('');
  const [code, setCode] = useState('');
  const [parentId, setParentId] = useState('');
  // Владелец нового типа: ЯДРО по умолчанию (ТЗ CORE-18). Тип, заведённый человеком, не объявлен
  // ни одним модулем — значит принадлежит экземпляру целиком и не исчезает из редактора в тот
  // день, когда модуль выключат. Отдать его модулю можно потом, в параметрах типа.
  const [module, setModule] = useState(CORE_OWNER);
  const [isAbstract, setIsAbstract] = useState(false);
  const [fields, setFields] = useState<SchemaField[]>([]);
  const [showJson, setShowJson] = useState(false);
  const [error, setError] = useState('');
  const mutation = useCreateDocumentType();

  function handleNameChange(v: string) {
    setCode(nextAutoKey(code, name, v, true)); // форма создания — тип всегда новый
    setName(v);
  }

  const sameKindTypes = allDocTypes.filter(dt => dt.kind === kind);
  const compositeTypes = allDocTypes.filter(dt => dt.kind === 'Composite');
  const parentType = sameKindTypes.find(dt => dt.id === parentId) ?? null;
  const parentEffectiveFields = parentType ? resolveEffectiveFields(parentType, allDocTypes) : [];
  const inheritedKeys = new Set(parentEffectiveFields.map(f => f.key));

  async function handleSubmit(e: React.FormEvent) {
    e.preventDefault();
    setError('');
    const fieldError = validateFields(fields);
    if (fieldError) { setError(fieldError); return; }
    const conflict = fields.find(f => inheritedKeys.has(f.key.trim()));
    if (conflict) { setError(`Ключ "${conflict.key}" уже есть в родительском типе`); return; }
    try {
      const created = await mutation.mutateAsync({
        name, code, kind, module,
        parentId: parentId || null,
        schema: schemaToJson(fields, [], {}),
        isAbstract: kind === 'Document' ? isAbstract : false,
      });
      onCreated(created); // выбрать созданный тип → откроется detail-редактор (issue #383)
      onClose();
    } catch (err: unknown) {
      setError(err instanceof Error ? err.message : 'Ошибка сохранения');
    }
  }

  return (
    <form onSubmit={handleSubmit} className="flex flex-col min-h-0 flex-1">
      <div className="flex-1 min-h-0 overflow-y-auto px-6 py-4 space-y-5">
      <div className="grid grid-cols-2 gap-4">
        <TextField label="Наименование" value={name} onChange={e => handleNameChange(e.target.value)} required />
        <TextField label="Код" value={code} onChange={e => setCode(e.target.value)}
          required spellCheck={false} className="font-mono" />
      </div>

      {kind === 'Document' && (
        <Checkbox checked={isAbstract} onChange={setIsAbstract}
          label="Абстрактный тип" description="Нельзя добавить в комплект напрямую" />
      )}

      <Select label="Владелец" value={module} onValueChange={setModule}>
        {owners.map(o => <SelectItem key={o.code} value={o.code}>{o.title}</SelectItem>)}
      </Select>
      <p className="-mt-3 text-body-small text-on-surface-variant">
        Типы выключенного модуля не предлагаются в редакторе. Тип, заведённый здесь, принадлежит
        ядру: его не объявлял ни один модуль.
      </p>

      {sameKindTypes.length > 0 && (
        <TypePickerField className="w-full" label="Родительский тип (наследование)" title="Родительский тип"
          placeholder="— без родителя —" clearable={{ label: 'Без родителя' }}
          types={toParentPickTypes(offeredTypes(sameKindTypes, access))} value={parentId || undefined}
          onChange={id => setParentId(id ?? '')} />
      )}

      {parentEffectiveFields.length > 0 && (
        <div>
          <p className="text-title-small text-on-surface-variant mb-2">
            Наследуемые поля от «{parentType?.name}» ({parentEffectiveFields.length})
          </p>
          <div className="border border-outline-variant rounded-md bg-surface-container-low px-3 py-2 space-y-1">
            {parentEffectiveFields.map(f => (
              <div key={f.key} className="flex items-center gap-3 text-body-small text-on-surface-variant">
                <span className="font-mono text-on-surface-variant w-36 truncate">{f.key}</span>
                <span className="flex-1 truncate">{f.title}</span>
                <span className="text-on-surface-variant">
                  {f.type === 'complex'
                    ? (compositeTypes.find(c => c.id === f.typeId)?.name ?? 'Составной')
                    : (TYPE_LABELS[f.type] ?? f.type)}
                </span>
                <span className={f.required ? 'text-error' : 'text-on-surface-variant'}>
                  {f.required ? 'обязат.' : 'опц.'}
                </span>
              </div>
            ))}
          </div>
          <p className="text-body-small text-on-surface-variant mt-1">
            Управление унаследованными полями — после создания типа.
          </p>
        </div>
      )}

      <div>
        <div className="flex items-center justify-between mb-3">
          <label className="text-body-medium font-medium text-on-surface-variant">
            {parentEffectiveFields.length > 0 ? 'Собственные поля' : 'Поля'}
          </label>
          {fields.length > 0 && (
            <button type="button" onClick={() => setShowJson(v => !v)}
              className={`flex items-center gap-1.5 text-body-small px-2 py-1 rounded-xs ${
                showJson ? 'bg-on-surface text-surface-container' : 'text-on-surface-variant hover:text-on-surface state-layer'
              }`}>
              <Icon icon={DataObject} size={18} /> JSON
            </button>
          )}
        </div>
        {showJson
          ? <JsonPreview fields={fields} groups={[]} excludedFields={[]} fieldOverrides={{}} />
          : <FieldBuilder fields={fields} onChange={setFields} disabledKeys={inheritedKeys} compositeTypes={compositeTypes} primitiveTypes={primitiveTypes} enumTypes={enumTypes} allDocTypes={allDocTypes} />}
      </div>

      {error && <p className="text-body-medium text-error">{error}</p>}
      </div>
      <div className="shrink-0 px-6 py-3 border-t border-outline-variant flex justify-end gap-3">
        <Button type="button" variant="text" data-modal-exit="" onClick={onClose}>Отмена</Button>
        <Button type="submit" variant="filled" loading={mutation.isPending}>
          {mutation.isPending ? 'Создание…' : 'Создать'}
        </Button>
      </div>
    </form>
  );
}
