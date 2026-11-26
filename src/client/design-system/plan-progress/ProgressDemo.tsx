import { useState } from 'react';
import { Banner } from '@/shared/ui/Banner';
import { Icon } from '@/shared/ui/Icon';
import { Apartment, ViewList, VisibilityOff } from '@/shared/ui/icons';
import { Modal } from '@/shared/ui/Modal';
import { PageHeader } from '@/shared/ui/PageHeader';
import { Select, SelectItem } from '@/shared/ui/Select';
import { ThemeToggle } from '@/shared/ui/ThemeToggle';
import { ProgressStrips } from './ProgressStrips';
import { ProgressTables } from './ProgressTables';
import { progressSample, scenarios, sections, variants, type Estimate, type Scenario, type Section, type Variant } from './sample';

const catalog = './?section=mockups&project=plan-25-progress';

function option<T extends string>(items: readonly { value: T }[], value: string | null, fallback: T) {
  return items.find(item => item.value === value)?.value ?? fallback;
}

function remember(key: string, value: string) {
  const url = new URL(location.href);
  url.searchParams.set(key, value);
  history.replaceState(history.state, '', url);
}

export function ProgressDemo({ params }: { params: URLSearchParams }) {
  const [variant, setVariant] = useState<Variant>(() => option(variants, params.get('page'), 'a'));
  const [scenario, setScenario] = useState<Scenario>(() => option(scenarios, params.get('scenario'), 'partial'));
  const [section, setSection] = useState<Section>(() => option(sections, params.get('scope'), 'all'));
  const [dialog, setDialog] = useState<Estimate | 'distribution'>();
  const sample = progressSample(scenario, section);

  return <>
    <header className="p5-toolbar">
      <div className="p5-boundary p5-toolbar-inner">
        <a className="p5-catalog-link focus-ring" href={catalog}>
          <Icon icon={ViewList} size={24} aria-hidden />
          <span>BHS.CRG UI<span className="text-on-surface-variant"> · Макеты</span></span>
        </a>
        <div className="p5-preview-controls">
          <Select label="Вариант П5" value={variant} density="dense" onValueChange={value => {
            const next = option(variants, value, 'a');
            setVariant(next);
            remember('page', next);
          }}>
            {variants.map(item => <SelectItem key={item.value} value={item.value}>{item.label}</SelectItem>)}
          </Select>
          <Select label="Данные и права" value={scenario} density="dense" onValueChange={value => {
            const next = option(scenarios, value, 'partial');
            setScenario(next);
            remember('scenario', next);
          }}>
            {scenarios.map(item => <SelectItem key={item.value} value={item.value}>{item.label}</SelectItem>)}
          </Select>
          <div className="p5-theme"><ThemeToggle label="Тема макета" /></div>
        </div>
      </div>
    </header>
    <main className="p5-boundary p5-main">
      <PageHeader className="p5-heading" title="Ход работ" subtitle="Стройка «Северный логистический парк» · демонстрационные данные"
        icon={<span className="p5-site-icon"><Icon icon={Apartment} size={24} aria-hidden /></span>}
        actions={sample.permitted && <div className="p5-section">
          <Select label="Раздел стройки" value={section} density="dense" onValueChange={value => {
            const next = option(sections, value, 'all');
            setSection(next);
            remember('scope', next);
          }}>
            {sections.map(item => <SelectItem key={item.value} value={item.value}>{item.label}</SelectItem>)}
          </Select>
        </div>} />
      {sample.permitted ? <>
        <ProgressStrips sample={sample} section={section} onEstimate={setDialog}
          onDistribution={() => setDialog('distribution')} />
        <section className="grid min-w-0 gap-3" aria-labelledby="p5-works-heading">
          <h2 id="p5-works-heading" className="text-title-medium">Виды работ</h2>
          <ProgressTables key={variant} variant={variant} sample={sample} />
        </section>
      </> : <Banner tone="info" icon={VisibilityOff} className="p5-strip">
        <strong className="block text-label-large">Ход работ недоступен</strong>
        Для просмотра нужно право на чтение смет, графика, факта или затрат.
      </Banner>}
    </main>
    {dialog && <Modal open nothingToLose onOpenChange={open => !open && setDialog(undefined)}
      title={dialog === 'distribution' ? 'План без распределения по разделам' : `${dialog.name} · действующая редакция ${dialog.revision}`}>
      {dialog === 'distribution' ? <div className="grid gap-3 text-body-medium">
        <p>В плане всей стройки остаётся 400 м по трём работам.</p>
        <ul className="list-disc pl-5">
          <li>Прокладка силового кабеля · 240 м</li>
          <li>Прокладка кабеля управления · 100 м</li>
          <li>Прокладка контрольного кабеля · 60 м</li>
        </ul>
      </div> : <div className="grid gap-3 text-body-medium">
        <p><strong>Очередь «Не сопоставлены»</strong> · строк в редакции: {dialog.unmatched}</p>
        <ul className="list-disc pl-5">{dialog.examples.map(name => <li key={name}>{name}</li>)}</ul>
      </div>}
    </Modal>}
  </>;
}
