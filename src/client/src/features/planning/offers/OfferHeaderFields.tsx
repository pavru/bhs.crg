import { DateField } from '@/shared/ui/DateField';
import { NumberInput } from '@/shared/ui/NumberInput';
import { Select, SelectItem } from '@/shared/ui/Select';
import type { OfferVatMode } from '@/shared/api/planningOfferTypes';
import { VAT_LABELS } from './offerFormat';

export function OfferHeaderFields({ date, setDate, onDateIncomplete, mode, setMode, rate, setRate, disabled }: {
  date: string; setDate: (value: string) => void; onDateIncomplete: (value: boolean) => void;
  mode: OfferVatMode; setMode: (value: OfferVatMode) => void; rate: string; setRate: (value: string) => void; disabled: boolean;
}) {
  return <div className="grid gap-3">
    <DateField label="Дата КП" value={date} onChange={setDate} onIncomplete={value => onDateIncomplete(Boolean(value))} required disabled={disabled} />
    <Select label="Режим НДС" value={mode} disabled={disabled} onValueChange={value => setMode(value as OfferVatMode)}>
      {(Object.entries(VAT_LABELS) as [OfferVatMode, string][]).map(([value, label]) => <SelectItem key={value} value={value}>{label}</SelectItem>)}
    </Select>
    {mode !== 'none' && <NumberField label="Ставка НДС" value={rate} onChange={setRate} disabled={disabled} />}
  </div>;
}

export function NumberField({ label, value, onChange, disabled }: {
  label: string; value: string; onChange: (value: string) => void; disabled: boolean;
}) {
  return <div className="grid max-w-52 gap-1">
    <span aria-hidden="true" className="text-label-large">{label}</span>
    <NumberInput label={label} value={value} onChange={onChange} disabled={disabled} unit="%" />
  </div>;
}
