import type { EntryCatalogScope, WorkTypeOption } from '@/shared/api/workEntryCatalog';
import { Select, SelectItem } from './Select';
import { WorkCatalogPicker } from './WorkCatalogPicker';

export function WorkTypePicker({ scope, label, value, type, disabled, onPick }: {
  scope: EntryCatalogScope; label: string; value: string; type?: WorkTypeOption; disabled?: boolean;
  onPick: (item: WorkTypeOption) => void;
}) {
  return <WorkCatalogPicker scope={scope} kind="work-types" label={label} title="Выберите вид работы"
    display={type ? workTypeName(type) : value ? 'Вид работы недоступен' : 'Выбрать вид'}
    name={workTypeName} currentId={value} disabled={disabled} field onPick={onPick} />;
}

function workTypeName(type: WorkTypeOption) {
  return `${type.displayName}${type.archived ? ' · в архиве' : ''}`;
}

export function WorkVariantPicker({ type, value, disabled, onChange }: {
  type?: WorkTypeOption; value: string | null; disabled?: boolean; onChange: (value: string | null) => void;
}) {
  const variants = type?.variants.filter(item => !item.archived || item.id === value) ?? [];
  if (!variants.length && !value) return null;
  const missing = value && !variants.some(item => item.id === value);
  return <Select label="Вариант" value={value ?? undefined} placeholder="Не выбран" disabled={disabled}
    emptyItem="Без варианта" onValueChange={id => onChange(id || null)}>
    {missing && <SelectItem value={value} disabled>Вариант недоступен</SelectItem>}
    {variants.map(item => <SelectItem key={item.id} value={item.id} disabled={item.archived}>
      {item.name}{item.archived ? ' · в архиве' : ''}
    </SelectItem>)}
  </Select>;
}
