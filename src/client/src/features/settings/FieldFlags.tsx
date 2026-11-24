import type { SchemaField } from '@/shared/api/schema';
import { Checkbox } from '@/shared/ui/Checkbox';
import { Icon } from '@/shared/ui/Icon';
import { FunctionIcon } from '@/shared/ui/icons';
import { Tooltip } from '@/shared/ui/Tooltip';

type Props = { field: SchemaField; onChange: (patch: Partial<SchemaField>) => void };

export function RequiredFieldFlag({ field, onChange }: Props) {
  return field.computed ? (
    <Tooltip content="Расчётное поле — значение вычисляется формулой">
      <span className="flex items-center justify-center gap-1 text-label-small text-primary">
        <Icon icon={FunctionIcon} size={18} /> вычисл.
      </span>
    </Tooltip>
  ) : (
    <label className="flex items-center justify-center gap-1.5 cursor-pointer">
      <Checkbox checked={field.required} onChange={required => onChange({ required })} />
      <span className="text-body-small text-on-surface-variant">да</span>
    </label>
  );
}

export function ComputedFieldFlag({ field, onChange }: Props) {
  return <label className="flex items-center gap-2 cursor-pointer w-fit">
    <Checkbox checked={!!field.computed}
      onChange={on => onChange(on
        ? { computed: true, required: false, forAcceptance: undefined, defaultValue: undefined }
        : { computed: undefined, expression: undefined })} />
    <span className="text-body-small text-on-surface-variant flex items-center gap-1">
      <Icon icon={FunctionIcon} size={18} className="text-primary" /> Вычисляемое (формула)
    </span>
  </label>;
}

export function AcceptanceFieldFlag({ field, onChange }: Props) {
  return <div className="grid w-32 shrink-0 place-items-center">
    <Checkbox checked={field.forAcceptance === true} disabled={field.origin === 'module' || !!field.computed}
      aria-label={`Нужно к приёмке · ${field.title || field.key || 'Новое поле'}`}
      onChange={forAcceptance => onChange({ forAcceptance: forAcceptance || undefined })} />
  </div>;
}
