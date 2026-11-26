import { useState } from 'react';
import { Icon } from '@/shared/ui/Icon';
import { Apartment } from '@/shared/ui/icons';
import { PageHeader } from '@/shared/ui/PageHeader';
import { SegmentedButton } from '@/shared/ui/SegmentedButton';
import { Select, SelectItem } from '@/shared/ui/Select';
import { TabPanel, Tabs } from '@/shared/ui/Tabs';
import { ThemeToggle } from '@/shared/ui/ThemeToggle';
import { ProgressPreview } from './ProgressPreview';
import { SchedulePreview } from './SchedulePreview';
import { option, periods, scenarios, sections, variants, type Period, type Scenario, type Section, type Variant } from './sample';

const views = [
  { value: 'progress', label: 'Ход работ' },
  { value: 'schedule', label: 'График' },
] as const;
const widths = [
  { value: 'wide', label: 'Широкая' },
  { value: 'narrow', label: 'Узкая' },
] as const;

type View = (typeof views)[number]['value'];
type Width = (typeof widths)[number]['value'];

function remember(key: string, value: string) {
  const url = new URL(location.href);
  url.searchParams.set(key, value);
  history.replaceState(history.state, '', url);
}

function Choice<T extends string>({ label, items, value, onChange, param }: {
  label: string; items: readonly { value: T; label: string }[]; value: T; onChange: (value: T) => void; param: string;
}) {
  return <Select label={label} value={value} density="dense" onValueChange={raw => {
    const next = option(items, raw, value);
    onChange(next);
    remember(param, next);
  }}>
    {items.map(item => <SelectItem key={item.value} value={item.value}>{item.label}</SelectItem>)}
  </Select>;
}

export function Review({ params }: { params: URLSearchParams }) {
  const [variant, setVariant] = useState<Variant>(() => option(variants, params.get('page'), 'a'));
  const [scenario, setScenario] = useState<Scenario>(() => option(scenarios, params.get('scenario'), 'normal'));
  const [section, setSection] = useState<Section>(() => option(sections, params.get('scope'), 'all'));
  const [period, setPeriod] = useState<Period>(() => option(periods, params.get('period'), 'november'));
  const [view, setView] = useState<View>(() => option(views, params.get('view'), 'progress'));
  const [width, setWidth] = useState<Width>(() => option(widths, params.get('width'), 'wide'));

  return <>
    <header className="border-b border-outline-variant bg-surface">
      <div className="mx-auto grid max-w-7xl min-w-0 gap-3 px-4 py-3 md:px-6">
        <div className="flex flex-wrap items-center justify-between gap-2">
          <a className="focus-ring text-title-medium text-primary" href="./?section=mockups&project=work-28-estimate-vs-actual">
            BHS.CRG UI · Р6
          </a>
          <div className="w-36"><ThemeToggle label="Тема макета" /></div>
        </div>
        <div className="grid min-w-0 items-end gap-2 sm:grid-cols-[minmax(0,1fr)_minmax(0,1fr)] lg:grid-cols-[minmax(0,1fr)_minmax(0,1fr)_auto]">
          <Choice label="Вариант Р6" items={variants} value={variant} onChange={setVariant} param="page" />
          <Choice label="Состояние данных" items={scenarios} value={scenario} onChange={setScenario} param="scenario" />
          <SegmentedButton aria-label="Ширина макета" items={[...widths]} value={width} onChange={next => {
            setWidth(next);
            remember('width', next);
          }} />
        </div>
      </div>
    </header>
    <main className={`@container/r6 mx-auto grid min-w-0 gap-4 px-4 py-5 ${width === 'narrow' ? 'max-w-[390px]' : 'max-w-7xl md:px-6'}`}>
      <PageHeader title="Северный логистический парк" subtitle="Электромонтаж · демонстрационные данные"
        icon={<span className="grid size-10 place-items-center rounded-md bg-primary-container text-on-primary-container">
          <Icon icon={Apartment} size={24} aria-hidden />
        </span>} />
      <Tabs id="r6-view" aria-label="Экран Р6" items={[...views]} value={view} onChange={next => {
        setView(next);
        remember('view', next);
      }} />
      <div className="grid min-w-0 gap-2 @xl/r6:max-w-xl @xl/r6:grid-cols-2">
        <Choice label="Раздел стройки" items={sections} value={section} onChange={setSection} param="scope" />
        {view === 'progress' && <Choice label="Период" items={periods} value={period} onChange={setPeriod} param="period" />}
      </div>
      <TabPanel id="r6-view" value={view} className="min-w-0">
        {view === 'progress' ? <ProgressPreview variant={variant} scenario={scenario} section={section} period={period} />
          : <SchedulePreview key={variant} variant={variant} scenario={scenario} section={section} />}
      </TabPanel>
    </main>
  </>;
}
