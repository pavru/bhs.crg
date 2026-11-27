import type { ProgressStrip } from '@/shared/api/progress';
import { formatNumber, formatQuantityUnit } from '@/shared/format/format';
import { Banner } from '@/shared/ui/Banner';
import { Card } from '@/shared/ui/Card';
import { LinkOff, VisibilityOff } from '@/shared/ui/icons';
import { StatusText } from '@/shared/ui/StatusText';

export const LABOR_STRIP = 'work-labor';

export function ProgressLaborStrip({ strip }: { strip: ProgressStrip }) {
  const labor = strip.labor;
  const values = labor ? [
    { title: 'По смете', value: labor.estimatedHours, unit: 'чел.-ч', color: 'bg-primary' },
    { title: 'Фактически', value: labor.actualHours, unit: 'ч', color: 'bg-tertiary' },
  ] : [];
  const scale = Math.max(...values.map(item => Math.abs(item.value ?? 0)), 1);
  const numbers = strip.state === 'ok' && labor;
  return <section aria-label="Трудозатраты" className="min-w-0"><Card className="@container">
    <div className="flex flex-wrap items-center gap-x-3 gap-y-1">
      <h3 className="text-title-medium text-on-surface">Трудозатраты</h3>
      {numbers && labor.distributed && <StatusText>Расчётная</StatusText>}
    </div>
    {!numbers && <Banner tone="info" icon={strip.state === 'hidden' ? VisibilityOff : strip.state === 'not-connected' ? LinkOff : undefined}>
      {strip.text}
    </Banner>}
    {numbers && <dl className="grid gap-3 @sm:grid-cols-2">
      {values.map(item => <div key={item.title} className="grid gap-1.5">
        <dt className="text-body-medium text-on-surface-variant">{item.title}</dt>
        <dd className="text-title-medium text-on-surface tabular-nums">{item.value === null ? 'Нет значения' : <>
          {formatNumber(item.value)} <span className="text-body-medium">{item.unit}</span>
          <div aria-hidden className="mt-2 h-1 overflow-hidden rounded-full bg-surface-container-highest">
            <div className={`h-full rounded-full ${item.color}`} style={{ width: `${100 * Math.abs(item.value) / scale}%` }} />
          </div>
        </>}</dd>
      </div>)}
    </dl>}
    {labor && (labor.offEstimateHours !== null || labor.offEstimateWorks.length > 0) &&
      <div className="grid gap-1 border-t border-outline-variant pt-2 text-body-medium">
        <p>Несметные работы{labor.offEstimateHours !== null && ` · ${formatNumber(labor.offEstimateHours)} ч`}</p>
        {labor.offEstimateWorks.length > 0 && <ul className="grid gap-1 text-on-surface-variant">
          {labor.offEstimateWorks.map((work, index) => <li key={index}>
            {work.name ?? 'Вид работы недоступен'} · {formatQuantityUnit(work.volume, work.unit)}
          </li>)}
        </ul>}
      </div>}
    {labor?.unallocatedHours != null && <p className="text-body-medium text-on-surface-variant">
      Неразнесённые часы · {formatNumber(labor.unallocatedHours)} ч в остатке стройки
    </p>}
    {labor?.incomplete && <p className="text-body-medium text-on-surface-variant">
      Сравнение неполное. Есть отчёты без часов или бригадные итоги, которые нельзя разнести.
    </p>}
  </Card></section>;
}
