import { useId, useState } from 'react';
import type { ProductivityMonth, ProductivitySample, ScheduleProductivity as Productivity } from '@/shared/api/planningScheduleTypes';
import { formatDatePrecise, formatNumber } from '@/shared/format/format';
import { Button } from '@/shared/ui/Button';
import { Icon } from '@/shared/ui/Icon';
import { KeyboardArrowDown, KeyboardArrowUp } from '@/shared/ui/icons';
import { ProgressHidden } from '../progress/ProgressStrips';

const plural = new Intl.PluralRules('ru');

function count(value: number, one: string, few: string, many: string) {
  const form = plural.select(value);
  return `${formatNumber(value)} ${form === 'one' ? one : form === 'few' ? few : many}`;
}

function sampleText(sample: ProductivitySample) {
  return `${count(sample.reports, 'отчёт', 'отчёта', 'отчётов')} · ${count(sample.constructions, 'стройка', 'стройки', 'строек')}`;
}

function MonthValue({ value, unit, showMonth = false }: { value: ProductivityMonth; unit: string | null; showMonth?: boolean }) {
  const rate = value.productivity.volumePerHour;
  return <span className="grid gap-1">
    <span className={rate === undefined ? 'text-on-surface-variant' : 'font-medium tabular-nums'}>
      {rate === undefined ? 'Данных мало' : `${formatNumber(rate)} ${unit ? `${unit}/ч` : 'за час'}`}
    </span>
    <span className="text-body-small text-on-surface-variant">
      {showMonth && <>{formatDatePrecise(value.month, 'month')} · </>}{sampleText(value.productivity.sample)}
    </span>
  </span>;
}

export function ScheduleProductivity({ value, unit, name }: { value: Productivity; unit: string | null; name: string }) {
  const [open, setOpen] = useState(false);
  const details = useId();
  if (!('months' in value)) return <span className="text-on-surface-variant">{value.state === 'hidden'
    ? <ProgressHidden permission={value.permission} /> : value.state === 'not-connected'
      ? 'Учёт работ не подключён' : value.text ?? 'Выработка недоступна'}</span>;

  const months = [...value.months].sort((a, b) => b.month.localeCompare(a.month));
  const latest = months.find(month => month.productivity.state === 'ok') ?? months[0];
  return <div className="grid min-w-0 gap-2">
    {latest ? <MonthValue value={latest} unit={unit} showMonth /> : <span className="grid gap-1 text-on-surface-variant">
      <span>Данных мало</span>
      <span className="text-body-small">{sampleText({ reports: 0, constructions: 0 })}</span>
    </span>}
    {months.length > 0 && <Button variant="text" className="justify-self-start" aria-expanded={open} aria-controls={details}
      aria-label={`${open ? 'Свернуть' : 'Показать'} выработку по месяцам: ${name}`}
      icon={<Icon icon={open ? KeyboardArrowUp : KeyboardArrowDown} size={18} />} onClick={() => setOpen(value => !value)}>
      {open ? 'Свернуть' : 'По месяцам'}
    </Button>}
    <div id={details} hidden={!open} className="min-w-0 border-t border-outline-variant pt-2">
      {open && <>
        <dl className="divide-y divide-outline-variant">
          {months.map(month => <div key={month.month} className="grid grid-cols-2 gap-2 py-2">
            <dt className="text-on-surface-variant">{formatDatePrecise(month.month, 'month')}</dt>
            <dd><MonthValue value={month} unit={unit} /></dd>
          </div>)}
        </dl>
        <p className="text-body-small text-on-surface-variant">Минимальная выборка за месяц · {sampleText(value.minSample)}</p>
      </>}
    </div>
  </div>;
}
