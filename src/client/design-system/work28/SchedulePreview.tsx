import { useState } from 'react';
import { ProgressHidden } from '@/features/planning/progress/ProgressStrips';
import { formatDate, formatNumber, formatQuantityUnit } from '@/shared/format/format';
import { Banner } from '@/shared/ui/Banner';
import { Button } from '@/shared/ui/Button';
import { DataGrid, type DataGridColumn } from '@/shared/ui/DataGrid';
import { Icon } from '@/shared/ui/Icon';
import { KeyboardArrowDown, KeyboardArrowUp, LinkOff } from '@/shared/ui/icons';
import { Modal } from '@/shared/ui/Modal';
import { latest, rates, sections, works, type MonthlyRate, type Scenario, type Section, type Variant, type Work } from './sample';

const plurals = new Intl.PluralRules('ru');

function counted(value: number, words: { one: string; few: string; many: string }) {
  const form = plurals.select(value);
  return `${value} ${form === 'one' || form === 'few' ? words[form] : words.many}`;
}

function RateText({ value, unit, month = false }: { value: MonthlyRate; unit: string; month?: boolean }) {
  if (value.state === 'hidden') return <span className="text-on-surface-variant"><ProgressHidden permission="work.facts.read" /></span>;
  if (value.state === 'not-connected') return <span className="text-on-surface-variant">Учёт работ не подключён</span>;
  if (value.state !== 'ok' && value.state !== 'too-little-data') return null;
  return <span className="grid gap-0.5">
    <span className={value.state === 'ok' ? 'font-medium tabular-nums' : 'text-on-surface-variant'}>
      {value.state === 'ok' ? `${formatNumber(value.perHour)} ${unit}/ч` : 'Данных мало'}
    </span>
    <span className="text-body-small text-on-surface-variant">
      {month && <>{value.month} · </>}{counted(value.reports, { one: 'отчёт', few: 'отчёта', many: 'отчётов' })}
      {' · '}{counted(value.sites, { one: 'стройка', few: 'стройки', many: 'строек' })}
    </span>
  </span>;
}

function MonthlyDetails({ work, scenario }: { work: Work; scenario: Scenario }) {
  const months = rates(work, scenario);
  return <div className="grid min-w-0 gap-2">
    <h4 className="text-title-small">Выработка по месяцам</h4>
    {scenario === 'hidden' || scenario === 'work-off' ? <RateText value={months[0]} unit={work.unit} /> :
      <dl className="divide-y divide-outline-variant text-body-medium">
        {months.map(value => <div key={value.month} className="grid grid-cols-[minmax(0,1fr)_minmax(0,1fr)] gap-2 py-2">
          <dt className="text-on-surface-variant">{value.month}</dt>
          <dd className="min-w-0"><RateText value={value} unit={work.unit} /></dd>
        </div>)}
      </dl>}
  </div>;
}

function DateRange({ work }: { work: Work }) {
  return <>{formatDate(work.start)} — {formatDate(work.end)}</>;
}

function SelectedDetails({ work, scenario }: { work: Work; scenario: Scenario }) {
  return <>
    <dl className="grid grid-cols-2 gap-3 border-b border-outline-variant pb-3 text-body-medium">
      <div><dt className="text-body-small text-on-surface-variant">Объём по графику</dt>
        <dd>{formatQuantityUnit(work.plan, work.unit)}</dd></div>
      <div><dt className="text-body-small text-on-surface-variant">Последняя доступная выработка</dt>
        <dd><RateText value={latest(work, scenario)} unit={work.unit} /></dd></div>
    </dl>
    <MonthlyDetails work={work} scenario={scenario} />
  </>;
}

function WorkList({ rows, variant, scenario, selected, onSelect }: {
  rows: Work[]; variant: Variant; scenario: Scenario; selected?: Work; onSelect: (work: Work) => void;
}) {
  return <ul className="grid min-w-0 gap-2" aria-label="Работы графика">
    {rows.map(work => {
      const active = selected?.id === work.id;
      const inline = variant === 'c';
      return <li key={work.id} className={`min-w-0 rounded-md border ${active && variant === 'b'
        ? 'border-primary bg-secondary-container text-on-secondary-container' : 'border-outline-variant bg-surface'}`}>
        <div className="grid gap-2 p-3">
          <div className="flex flex-wrap items-start justify-between gap-2">
            <div className="min-w-0">
              <h4 className="text-title-small">{work.name}</h4>
              <p className="text-body-small"><DateRange work={work} /></p>
            </div>
            <Button variant={active && variant === 'b' ? 'tonal' : 'text'}
              aria-label={`${inline && active ? 'Свернуть' : 'Подробности'}: ${work.name}`}
              aria-expanded={inline ? active : undefined} aria-pressed={variant === 'b' ? active : undefined}
              icon={inline ? <Icon icon={active ? KeyboardArrowUp : KeyboardArrowDown} size={18} /> : undefined}
              onClick={() => onSelect(work)}>{inline && active ? 'Свернуть' : 'Подробности'}</Button>
          </div>
          <dl className={`gap-x-4 gap-y-2 text-body-medium @sm/r6:grid-cols-2 ${variant === 'b' && active ? 'hidden @2xl/r6:grid' : 'grid'}`}>
            <div><dt className="text-body-small">Объём по графику</dt><dd>{formatQuantityUnit(work.plan, work.unit)}</dd></div>
            {variant !== 'b' && <div><dt className="text-body-small">Выработка</dt>
              <dd><RateText value={latest(work, scenario)} unit={work.unit} month /></dd></div>}
          </dl>
          {variant === 'b' && active && <div className="grid gap-3 rounded-md bg-surface p-3 text-on-surface @2xl/r6:hidden">
            <SelectedDetails work={work} scenario={scenario} />
          </div>}
          {inline && active && <div className="border-t border-outline-variant pt-3"><MonthlyDetails work={work} scenario={scenario} /></div>}
        </div>
      </li>;
    })}
  </ul>;
}

export function SchedulePreview({ variant, scenario, section }: { variant: Variant; scenario: Scenario; section: Section }) {
  const rows = works.filter(work => section === 'all' || section === work.section);
  const [selection, setSelection] = useState<string | null>(variant === 'b' ? rows[0].id : null);
  const [dialog, setDialog] = useState<Work>();
  const selected = rows.find(work => work.id === selection) ?? (variant === 'b' ? rows[0] : undefined);
  if (scenario === 'plan-off') return <Banner tone="info" icon={LinkOff}>Планирование не подключено. График недоступен.</Banner>;

  const columns: DataGridColumn<Work>[] = [
    { key: 'work', label: 'Работа', rowHeader: true, wrap: true, cell: work => <>
      {work.name}<span className="block text-body-small text-on-surface-variant">{sections.find(item => item.value === work.section)?.label}</span>
    </> },
    { key: 'volume', label: 'Объём', width: 'w-28', align: 'right', cell: work => formatQuantityUnit(work.plan, work.unit) },
    { key: 'productivity', label: 'Выработка', width: 'w-48', wrap: true, cell: work =>
      <RateText value={latest(work, scenario)} unit={work.unit} month /> },
    { key: 'dates', label: 'Срок', width: 'w-44', wrap: true, cell: work => <DateRange work={work} /> },
    { key: 'details', label: 'Подробности', width: 'w-28', cell: work => <Button variant="text"
      aria-label={`Подробности: ${work.name}`} onClick={() => setDialog(work)}>Открыть</Button> },
  ];

  return <section className="grid min-w-0 gap-3" aria-labelledby="r6-schedule">
    <div><h3 id="r6-schedule" className="text-title-medium">Электромонтаж · график работ</h3>
      <p className="mt-1 text-body-small text-on-surface-variant">9–30 ноября 2026</p></div>
    {variant === 'a' && <>
      <div className="hidden min-w-0 @3xl/r6:block"><DataGrid rows={rows} columns={columns} rowKey={work => work.id} /></div>
      <div className="@3xl/r6:hidden"><WorkList variant={variant} rows={rows} scenario={scenario} onSelect={setDialog} /></div>
    </>}
    {variant === 'b' && <div className="grid min-w-0 items-start gap-4 @2xl/r6:grid-cols-[minmax(0,1fr)_minmax(0,1fr)]">
      <WorkList variant={variant} rows={rows} scenario={scenario} selected={selected} onSelect={work => setSelection(work.id)} />
      {selected && <aside className="hidden min-w-0 gap-3 rounded-md border border-outline-variant bg-surface p-4 @2xl/r6:grid" aria-label="Подробности работы">
        <h4 className="text-title-medium" aria-live="polite">{selected.name}</h4>
        <SelectedDetails work={selected} scenario={scenario} />
      </aside>}
    </div>}
    {variant === 'c' && <WorkList variant={variant} rows={rows} scenario={scenario} selected={selected}
      onSelect={work => setSelection(id => id === work.id ? null : work.id)} />}
    {dialog && <Modal open nothingToLose title={dialog.name} onOpenChange={open => !open && setDialog(undefined)}>
      <MonthlyDetails work={dialog} scenario={scenario} />
    </Modal>}
  </section>;
}
