import { ProgressGrid } from '@/features/planning/progress/ProgressGrid';
import { ProgressHidden, ProgressStrips } from '@/features/planning/progress/ProgressStrips';
import type { ProgressStrip } from '@/shared/api/progress';
import { formatNumber } from '@/shared/format/format';
import { Banner } from '@/shared/ui/Banner';
import { LinkOff, VisibilityOff } from '@/shared/ui/icons';
import { StatusText } from '@/shared/ui/StatusText';
import { costs, labor, progress, type Labor, type Period, type Scenario, type Section, type Variant } from './sample';

function laborStrips(result: Labor): ProgressStrip[] {
  if ('permission' in result) return [{ code: 'work-labor', state: 'hidden', text: '', permission: result.permission }];
  const text = 'message' in result ? result.message
    : `По смете ${formatNumber(result.estimate)} чел.-ч — фактически ${formatNumber(result.actual)} ч${result.distributed ? ' · расчётная' : ''}`;
  const residual = 'residual' in result ? result.residual : undefined;
  return [text, result.extra, residual].flatMap((line, index) => line ? [{
    code: `work-labor-${index}`, state: residual ? 'partial' : 'ok', text: line,
  }] : []);
}

function LaborDetails({ result, visual }: { result: Labor; visual: boolean }) {
  const values = 'estimate' in result ? [
    { title: 'По смете', value: result.estimate, unit: 'чел.-ч', color: 'bg-primary' },
    { title: 'Фактически', value: result.actual, unit: 'ч', color: 'bg-tertiary' },
  ] : [];
  const scale = Math.max(...values.map(item => item.value), 1);
  return <section aria-label="Трудозатраты" className="grid min-w-0 gap-3 rounded-md border border-outline-variant bg-surface p-4">
    <div className="flex flex-wrap items-center gap-x-3 gap-y-1">
      <h3 className="text-title-medium">Трудозатраты</h3>
      {'distributed' in result && result.distributed && <StatusText>Расчётная</StatusText>}
    </div>
    {'permission' in result && <Banner tone="info" icon={VisibilityOff}>
      Сметные нормы. <ProgressHidden permission={result.permission} />
    </Banner>}
    {'message' in result && <p role="status" className="text-body-medium text-on-surface-variant">{result.message}</p>}
    {values.length > 0 && <dl className={visual ? 'grid gap-3 @sm/r6:grid-cols-2' : 'grid divide-y divide-outline-variant'}>
      {values.map(item => <div key={item.title} className={visual ? 'grid gap-1.5' : 'flex flex-wrap justify-between gap-2 py-2'}>
        <dt className="text-body-medium text-on-surface-variant">{item.title}</dt>
        <dd className="text-title-medium tabular-nums">{formatNumber(item.value)} <span className="text-body-medium">{item.unit}</span>
          {visual && <div aria-hidden className="mt-2 h-1 overflow-hidden rounded-full bg-surface-container-highest">
            <div className={`h-full rounded-full ${item.color}`} style={{ width: `${100 * item.value / scale}%` }} />
          </div>}
        </dd>
      </div>)}
    </dl>}
    {'extra' in result && result.extra && <p className="border-t border-outline-variant pt-2 text-body-medium">{result.extra}</p>}
    {'residual' in result && result.residual && <p className="text-body-medium text-on-surface-variant">{result.residual}</p>}
  </section>;
}

export function ProgressPreview({ variant, scenario, section, period }: {
  variant: Variant; scenario: Scenario; section: Section; period: Period;
}) {
  const { columns, rows } = progress(scenario, section);
  const sectionId = section === 'all' ? null : section;
  const result = scenario === 'work-off' ? null : labor(scenario, section, period);
  const strips: ProgressStrip[] = scenario === 'plan-off' ? [] : [
    { code: 'estimate', text: 'План по смете «Электроснабжение» · редакция 3', state: 'ok' },
  ];
  if (variant === 'a' && result) strips.push(...laborStrips(result));
  const details = result && variant !== 'a' ? <LaborDetails result={result} visual={variant === 'c'} /> : null;

  return <div className="grid min-w-0 gap-4">
    {scenario === 'work-off' && <Banner tone="info" icon={LinkOff}>Учёт работ не подключён. План и сроки доступны.</Banner>}
    <ProgressStrips columns={columns} costs={costs} partial={false} strips={strips} sectionId={sectionId} />
    {variant === 'c' && details}
    <div className={variant === 'b' && details
      ? 'grid min-w-0 items-start gap-4 @3xl/r6:grid-cols-[minmax(0,2fr)_minmax(0,1fr)]' : 'grid min-w-0 gap-4'}>
      <section className="grid min-w-0 gap-3" aria-labelledby="r6-work-types">
        <h3 id="r6-work-types" className="text-title-medium">Виды работ</h3>
        <ProgressGrid columns={columns} rows={rows} section={sectionId !== null} />
      </section>
      {variant === 'b' && <div className="order-first @3xl/r6:order-last">{details}</div>}
    </div>
  </div>;
}
