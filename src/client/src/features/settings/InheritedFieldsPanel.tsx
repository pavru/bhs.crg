/**
 * Панель унаследованных полей и отметка обязательности — часть страницы типов документов.
 *
 * Выделено из `DocumentTypesPage.tsx` (issue #1031). Самый нижний слой страницы: о редакторах,
 * которые его зовут, не знает.
 */
import * as DropdownMenu from '@radix-ui/react-dropdown-menu';
import { EyeOff, Check, RotateCcw } from 'lucide-react';
import { Switch } from '@/shared/ui/Switch';
import type { DocumentType, EnumTypeDef } from '@/shared/api/types';
import { type SchemaField } from '@/shared/api/schema';
import { TYPE_LABELS } from './schemaConstants';
import { DefaultValueCell } from './FieldBuilder';

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
    return <p className="text-xs text-fg4 py-1">Родительский тип не содержит полей.</p>;
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
        <span className="text-xs font-medium text-fg3">Ключ</span>
        <span className="text-xs font-medium text-fg3">Название</span>
        <span className="text-xs font-medium text-fg3">Тип</span>
        <span className="text-xs font-medium text-fg3">Обязательность</span>
        <span className="text-xs font-medium text-fg3">Дефолт</span>
        <span className="text-xs font-medium text-fg3 text-center">Вкл.</span>
      </div>
      {parentEffectiveFields.map(field => {
        const isExcluded = excludedSet.has(field.key);
        const override = fieldOverrides[field.key];
        return (
          <div key={field.key} className={`${cols} rounded-md px-2 py-2 hover:bg-muted/50 transition-colors ${isExcluded ? 'opacity-55' : ''}`}>
            <span className="flex items-center gap-1.5 min-w-0">
              {isExcluded && <EyeOff size={14} className="text-fg4 shrink-0" />}
              <span className={`text-sm font-mono truncate ${isExcluded ? 'line-through text-fg4' : 'text-fg2'}`}>{field.key}</span>
            </span>
            <span className="text-sm text-fg2 truncate">{field.title}</span>
            <span className="text-xs text-fg4 truncate">{fieldTypeLabel(field)}</span>
            {isExcluded
              ? <span className="text-xs text-fg4">—</span>
              : <RequiredChip field={field} override={override}
                  onOverride={r => onOverrideRequired(field.key, r)} onReset={() => onResetOverride(field.key)} />}
            {isExcluded
              ? <span />
              : <DefaultValueCell field={field} override={override} enumTypes={enumTypes} onOverrideDefaultValue={onOverrideDefaultValue} />}
            <div className="flex justify-center">
              <Switch size="sm" checked={!isExcluded}
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
    <DropdownMenu.Root>
      <DropdownMenu.Trigger asChild>
        <button type="button"
          className={`inline-flex items-center gap-1.5 text-xs px-2 py-1 rounded-full transition-colors ${
            overridden ? 'bg-brand-subtle text-brand font-medium' : 'text-fg3 hover:bg-muted'}`}>
          {overridden && <span className="w-1.5 h-1.5 rounded-full bg-brand shrink-0" />}
          {overridden ? `${parentLabel} → ${effective ? 'обяз.' : 'опц.'}` : (effective ? 'обяз.' : 'опц.')}
        </button>
      </DropdownMenu.Trigger>
      <DropdownMenu.Portal>
        <DropdownMenu.Content align="start" sideOffset={4}
          className="z-50 min-w-[210px] rounded-xl border border-stroke bg-surface p-1 text-sm text-fg1"
          style={{ boxShadow: 'var(--f-shadow16)' }}>
          <ReqItem onSelect={onReset} active={!overridden}>Как у родителя ({parentLabel})</ReqItem>
          <ReqItem onSelect={() => onOverride(true)} active={overridden && effective}>Обязательное</ReqItem>
          <ReqItem onSelect={() => onOverride(false)} active={overridden && !effective}>Опциональное</ReqItem>
          {overridden && (
            <>
              <DropdownMenu.Separator className="my-1 h-px bg-stroke" />
              <ReqItem onSelect={onReset}><RotateCcw size={13} className="text-fg4" /> Сбросить переопределение</ReqItem>
            </>
          )}
        </DropdownMenu.Content>
      </DropdownMenu.Portal>
    </DropdownMenu.Root>
  );
}

function ReqItem({ children, onSelect, active }: { children: React.ReactNode; onSelect: () => void; active?: boolean }) {
  return (
    <DropdownMenu.Item onSelect={onSelect}
      className="flex items-center gap-2 px-2.5 py-1.5 rounded-lg cursor-pointer outline-none data-[highlighted]:bg-muted">
      <Check size={14} className={active ? 'text-brand' : 'invisible'} />
      <span className="flex-1">{children}</span>
    </DropdownMenu.Item>
  );
}
