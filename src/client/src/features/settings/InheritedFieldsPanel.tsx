/**
 * Панель унаследованных полей и отметка обязательности — часть страницы типов документов.
 *
 * Выделено из `DocumentTypesPage.tsx` (issue #1031). Самый нижний слой страницы: о редакторах,
 * которые его зовут, не знает.
 */
import { Menu, MenuItem, MenuRadioGroup, MenuRadioItem, MenuSeparator } from '@/shared/ui/Menu';
import { Icon } from '@/shared/ui/Icon';
import { RestartAlt, VisibilityOff } from '@/shared/ui/icons';
import { Switch } from '@/shared/ui/Switch';
import type { DocumentType, EnumTypeDef } from '@/shared/api/types';
import { type SchemaField } from '@/shared/api/schema';
import { TYPE_LABELS } from './schemaConstants';
import { DefaultValueCell } from './FieldBuilder';
import { Chip } from '@/shared/ui/Chip';

// Реестр редакторов / диалог-гард / карточка-секция — общие для list-detail страниц (см. typeEditorShell).

export function InheritedFieldsPanel({
  parentEffectiveFields, excludedFields, fieldOverrides, compositeTypes, enumTypes,
  onExclude, onInclude, onOverrideRequired, onOverrideDefaultValue, onResetOverride,
}: {
  parentEffectiveFields: SchemaField[];
  excludedFields: string[];
  fieldOverrides: Record<string, { required?: boolean; defaultValue?: unknown }>;
  compositeTypes: DocumentType[];
  enumTypes: EnumTypeDef[];
  onExclude: (key: string) => void;
  onInclude: (key: string) => void;
  onOverrideRequired: (key: string, required: boolean) => void;
  onOverrideDefaultValue: (key: string, value: unknown) => void;
  onResetOverride: (key: string) => void;
}) {
  const excludedSet = new Set(excludedFields);

  if (parentEffectiveFields.length === 0) {
    return <p className="text-body-small text-on-surface-variant py-1">Родительский тип не содержит полей.</p>;
  }

  function fieldTypeLabel(f: SchemaField) {
    if (f.type === 'complex' || f.type === 'array') {
      const ct = compositeTypes.find(c => c.id === f.typeId);
      return ct ? ct.name : (f.type === 'array' ? 'Массив' : 'Составной');
    }
    return TYPE_LABELS[f.type] ?? f.type;
  }

  const cols = 'grid grid-cols-[1fr_1fr_110px_160px_120px_64px] gap-2 items-center';
  return (
    <div className="space-y-0.5">
      <div className={`${cols} px-2 pb-1`}>
        <span className="text-body-small font-medium text-on-surface-variant">Ключ</span>
        <span className="text-body-small font-medium text-on-surface-variant">Название</span>
        <span className="text-body-small font-medium text-on-surface-variant">Тип</span>
        <span className="text-body-small font-medium text-on-surface-variant">Обязательность</span>
        <span className="text-body-small font-medium text-on-surface-variant">Дефолт</span>
        <span className="text-body-small font-medium text-on-surface-variant text-center">Вкл.</span>
      </div>
      {parentEffectiveFields.map(field => {
        const isExcluded = excludedSet.has(field.key);
        const override = fieldOverrides[field.key];
        return (
          <div key={field.key} className={`${cols} rounded-sm px-2 py-2 state-layer ${isExcluded ? 'opacity-55' : ''}`}>
            <span className="flex items-center gap-1.5 min-w-0">
              {isExcluded && <Icon icon={VisibilityOff} size={18} className="text-on-surface-variant shrink-0" />}
              <span className={`text-body-medium font-mono truncate ${isExcluded ? 'line-through text-on-surface-variant' : 'text-on-surface-variant'}`}>{field.key}</span>
            </span>
            <span className="text-body-medium text-on-surface-variant truncate">{field.title}</span>
            <span className="text-body-small text-on-surface-variant truncate">{fieldTypeLabel(field)}</span>
            {isExcluded
              ? <span className="text-body-small text-on-surface-variant">—</span>
              : <RequiredChip field={field} override={override}
                  onOverride={r => onOverrideRequired(field.key, r)} onReset={() => onResetOverride(field.key)} />}
            {isExcluded
              ? <span />
              : <DefaultValueCell field={field} override={override} enumTypes={enumTypes} onOverrideDefaultValue={onOverrideDefaultValue} />}
            <div className="flex justify-center">
              <Switch checked={!isExcluded}
                onChange={on => on ? onInclude(field.key) : onExclude(field.key)}
                title={isExcluded ? 'Включить поле' : 'Исключить поле'} label={`Поле ${field.key}: включено`} />
            </div>
          </div>
        );
      })}
    </div>
  );
}

/** Интерактивный chip обязательности унаследованного поля (issue #197): меню как-у-родителя/обяз/опц/сброс. */
function RequiredChip({ field, override, onOverride, onReset }: {
  field: SchemaField;
  override?: { required?: boolean };
  onOverride: (required: boolean) => void;
  onReset: () => void;
}) {
  const overridden = override?.required !== undefined;
  const effective = overridden ? override!.required! : field.required;
  const parentLabel = field.required ? 'обяз.' : 'опц.';
  return (
    <Menu dense trigger={
      // Выбор из меню — `Chip menu` ([UI-42]): переопределённое отмечено цветом выбранного чипа.
      <Chip menu selected={overridden}>
        {overridden ? `${parentLabel} → ${effective ? 'обяз.' : 'опц.'}` : (effective ? 'обяз.' : 'опц.')}
      </Chip>
    }>
      <MenuRadioGroup value={!overridden ? 'parent' : effective ? 'required' : 'optional'}>
        <MenuRadioItem value="parent" onSelect={onReset}>Как у родителя ({parentLabel})</MenuRadioItem>
        <MenuRadioItem value="required" onSelect={() => onOverride(true)}>Обязательное</MenuRadioItem>
        <MenuRadioItem value="optional" onSelect={() => onOverride(false)}>Опциональное</MenuRadioItem>
      </MenuRadioGroup>
      {overridden && (
        <>
          <MenuSeparator />
          <MenuItem icon={RestartAlt} onSelect={onReset}>Сбросить переопределение</MenuItem>
        </>
      )}
    </Menu>
  );
}
