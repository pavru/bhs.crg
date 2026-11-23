import { useState } from 'react';
import { Button, IconButton } from '@/shared/ui/Button';
import { DataGrid, type DataGridColumn } from '@/shared/ui/DataGrid';
import { DateRange } from '@/shared/ui/DateRange';
import { DarkMode, LightMode } from '@/shared/ui/icons';
import { PageHeader } from '@/shared/ui/PageHeader';
import { Select, SelectItem } from '@/shared/ui/Select';
import { Tooltip } from '@/shared/ui/Tooltip';
import { useTheme } from '@/shared/ui/themeContext';
import { WORK_SUMMARY_STATES } from '@/shared/api/work';
import { NARROW, useMedia } from '@/shared/hooks/useMedia';
import { SummaryDayPicker } from '@/features/work/summary/SummaryDayPicker';
import { days, people, sites, states, variants, type Entry, type Person } from './model';

function dayLabel(day: string) {
  return `${day.slice(8)}.${day.slice(5, 7)}`;
}

function statusText(entry: Entry) {
  if (entry.state === 'draft') return 'Черновик. Автоподача 06.10 в 00:00 по времени стройки.';
  if (entry.hours !== undefined) return `${entry.hours.toLocaleString('ru')} ч · ${entry.status}`;
  return states[entry.state].label;
}

export function SummaryMockup({ page }: { page: string | null }) {
  const [variant, setVariant] = useState(variants.find(v => v.id === page) ?? variants[0]);
  const [site, setSite] = useState('all');
  const [from, setFrom] = useState(days[0]);
  const [to, setTo] = useState(days[30]);
  const [chosen, setChosen] = useState({ person: people[0], day: days[0], entries: people[0].firstDays[0] });
  const [selectedDay, setSelectedDay] = useState(days[0]);
  const { resolvedTheme, setTheme } = useTheme();
  const narrow = useMedia(NARROW);
  const words = variant.id === 'b';
  const details = variant.id === 'c';
  const period = days.filter(day => (!from || day >= from) && (!to || day <= to));
  const day = period.includes(selectedDay) ? selectedDay : period[0];
  const mobile = details && narrow && !!day;
  const shown = mobile ? [day] : period;
  const columns: DataGridColumn<Person>[] = [
    { key: 'employee', label: 'Сотрудник', width: 'w-32 md:w-48', rowHeader: true, wrap: true },
    { key: 'site', label: 'Стройка', width: 'w-24 md:w-40', wrap: true },
    ...shown.map((day): DataGridColumn<Person> => ({
      key: day, label: dayLabel(day), width: words ? 'w-28' : 'w-20',
      cell: person => {
        const entries = person.firstDays[days.indexOf(day)] ?? [{ state: 'future' }];
        return <div className="flex flex-col gap-1">
          {entries.map(entry => {
            const status = states[entry.state];
            const value = entry.hours === undefined ? status.symbol : `${entry.hours.toLocaleString('ru')} ч`;
            const label = `${entry.night ? 'Ночная' : 'Дневная'} смена. ${statusText(entry)}`;
            return <Tooltip key={entry.night ? 'night' : 'day'} content={label}>
              <Button variant="text" aria-label={label} className={`w-full !min-h-7 !px-1 !py-0 tabular-nums ${status.tone}`}
                onClick={() => setChosen({ person, day, entries })}>
                {entry.night && <span aria-hidden>☾</span>}
                {words && entry.hours === undefined ? status.short : value}
              </Button>
            </Tooltip>;
          })}
        </div>;
      },
    })),
  ];

  const legend = <ul aria-label="Легенда" className="flex flex-wrap gap-x-4 gap-y-2 text-body-small">
    {WORK_SUMMARY_STATES.map(code => <li key={code} className="flex items-center gap-2">
      <span aria-hidden className={`grid size-6 place-items-center rounded-sm ${states[code].tone}`}>{states[code].symbol}</span>
      {states[code].label}
    </li>)}
    <li className="flex items-center gap-2"><span aria-hidden className="grid size-6 place-items-center">☾</span>Ночная смена</li>
  </ul>;
  const panel = <section aria-label="Подробности клетки" className="rounded-md border border-outline-variant bg-surface p-4">
    <h2 className="text-title-medium">{chosen.person.employee} · {dayLabel(mobile ? day : chosen.day)}</h2>
    <ul className="mt-2 grid gap-2 text-body-medium">
      {(mobile ? chosen.person.firstDays[days.indexOf(day)] ?? [{ state: 'future' } as Entry] : chosen.entries)
        .map(entry => <li key={entry.night ? 'night' : 'day'}>
          {entry.night ? '☾ Ночная' : 'Дневная'} смена · {statusText(entry)}
        </li>)}
    </ul>
  </section>;

  return <main className="mx-auto grid min-w-0 max-w-[1440px] gap-4 p-3 sm:p-6">
    <div className="flex flex-wrap items-center justify-between gap-2">
      <nav aria-label="Варианты Р5" className="flex flex-wrap gap-1">
        {variants.map(item => <Button key={item.id} variant={variant === item ? 'tonal' : 'text'}
          aria-current={variant === item ? 'page' : undefined} onClick={() => setVariant(item)}>{item.name}</Button>)}
      </nav>
      <IconButton icon={resolvedTheme === 'dark' ? LightMode : DarkMode}
        label={resolvedTheme === 'dark' ? 'Включить светлую тему' : 'Включить тёмную тему'}
        onClick={() => setTheme(resolvedTheme === 'dark' ? 'light' : 'dark')} />
    </div>
    <PageHeader title="Сводка" subtitle="Отчёты ваших строек (2) · связь с устройствами не отслеживается" />
    <div className="flex flex-wrap items-end gap-3">
      <div className="w-full sm:w-56">
        <Select label="Стройка" density="dense" value={site} onValueChange={setSite}>
          <SelectItem value="all">Все доступные стройки</SelectItem>
          {sites.map(name => <SelectItem key={name} value={name}>{name}</SelectItem>)}
        </Select>
      </div>
      <DateRange label="Период" from={from} to={to} onChange={(f, t) => { setFrom(f); setTo(t); }}
        required ordered className="w-full sm:w-[25rem]" />
    </div>
    {words && <div className="rounded-md bg-surface-container p-3">{legend}</div>}
    {mobile && <><SummaryDayPicker days={period} day={day} onChange={setSelectedDay} />{panel}</>}
    <div className="min-w-0">
      <DataGrid columns={columns} rows={people.filter(person => site === 'all' || person.site === site)}
        rowKey={person => person.id} fixed pinned={2} density={words ? 'regular' : 'compact'} />
    </div>
    {details && !mobile && panel}
    {!words && legend}
  </main>;
}
