import { useCallback, useId, useState, type KeyboardEvent } from 'react';
import { useSearchParams } from 'react-router';
import { useCan } from '@/shared/api/access';
import { usePeriods } from '@/shared/api/periods';
import { useWorkSummary, type SummaryRow } from '@/shared/api/workSummary';
import { formatCount, formatDate, formatDayMonth } from '@/shared/format/format';
import { NARROW, useMedia } from '@/shared/hooks/useMedia';
import { Banner } from '@/shared/ui/Banner';
import { IconButton } from '@/shared/ui/Button';
import { Card } from '@/shared/ui/Card';
import { DataGrid, type DataGridColumn } from '@/shared/ui/DataGrid';
import { DateRange } from '@/shared/ui/DateRange';
import { useDocumentTitle } from '@/shared/ui/DocumentTitle';
import { EmptyState } from '@/shared/ui/EmptyState';
import { Icon } from '@/shared/ui/Icon';
import { ChevronLeft, ChevronRight, TableChart } from '@/shared/ui/icons';
import { PageHeader } from '@/shared/ui/PageHeader';
import { recordScreen, type RecordRoute } from '@/shared/ui/recordRoutes';
import { Select, SelectItem } from '@/shared/ui/Select';
import { apiError } from '@/shared/utils/apiError';
import { daysInMonth, parseISO, toISO } from '@/shared/utils/date';
import { SummaryCell } from './SummaryCell';
import { SummaryDayPicker } from './SummaryDayPicker';
import { SummaryLegend } from './SummaryLegend';
import { summaryEntryLabel } from './summaryPresentation';

const rowKey = (row: SummaryRow) => `${row.employeeId}:${row.constructionId}`;
const arrowStep: Readonly<Record<string, readonly [row: number, column: number]>> = {
  ArrowUp: [-1, 0], ArrowDown: [1, 0], ArrowLeft: [0, -1], ArrowRight: [0, 1],
};

function moveCell(event: KeyboardEvent<HTMLDivElement>) {
  if (event.defaultPrevented || event.altKey || event.ctrlKey || event.metaKey || event.shiftKey) return;
  if (!Object.hasOwn(arrowStep, event.key) || !(event.target instanceof HTMLElement)) return;
  const current = event.target.closest<HTMLElement>('[data-summary-cell]');
  const coordinates = current?.dataset.summaryCell;
  if (!coordinates || !event.currentTarget.contains(current)) return;
  const [row, column] = coordinates.split(':').map(Number);
  const [rows, columns] = arrowStep[event.key];
  const next = event.currentTarget.querySelector<HTMLElement>(`[data-summary-cell="${row + rows}:${column + columns}"]`);
  if (!next) return;
  event.preventDefault();
  next.focus();

  // Браузер видит границы прокрутки, но не перекрытие закреплёнными колонками.
  const table = next.closest('table');
  const pinned = table?.querySelectorAll('thead th[data-pin]');
  const lastPinned = pinned?.item(pinned.length - 1);
  if (!lastPinned || !table?.parentElement) return;
  const overlap = Math.ceil(lastPinned.getBoundingClientRect().right - next.getBoundingClientRect().left);
  if (overlap > 0) table.parentElement.scrollLeft -= overlap;
}

function month(today?: string) {
  const date = parseISO(today);
  return date ? { from: toISO(date.y, date.m, 1), to: toISO(date.y, date.m, daysInMonth(date.y, date.m)) } : { from: '', to: '' };
}

function SummaryGrid({ rows, days, today, reportRoute }: {
  rows: SummaryRow[]; days: string[]; today?: string; reportRoute: RecordRoute | null;
}) {
  const narrow = useMedia(NARROW);
  const keyboardHelp = useId();
  const [chosenDay, setChosenDay] = useState('');
  const [selected, setSelected] = useState<{ row: string; day: string }>();
  const select = useCallback((row: string, day: string) => setSelected(current =>
    current?.row === row && current.day === day ? current : { row, day }), []);
  const preferredDay = today && days.includes(today) ? today : days[0];
  const day = days.includes(chosenDay) ? chosenDay : preferredDay;
  const shown = narrow ? [day] : days;
  const activeRow = rows.find(row => rowKey(row) === selected?.row) ?? rows[0];
  const activeRowKey = rowKey(activeRow);
  const activeDay = narrow ? day : selected && days.includes(selected.day) ? selected.day : days[0];
  const entries = activeRow.cells[days.indexOf(activeDay)];
  const columns: DataGridColumn<SummaryRow>[] = [
    { key: 'employeeName', label: 'Сотрудник', width: 'w-28 md:w-48', rowHeader: true, wrap: true },
    { key: 'constructionName', label: 'Стройка', width: 'w-24 md:w-40', wrap: true },
    ...shown.map((date, column): DataGridColumn<SummaryRow> => {
      const index = days.indexOf(date);
      return {
        key: date, label: formatDayMonth(date), hint: formatDate(date), width: 'w-24 md:w-20',
        cell: (row, rowIndex) => {
          const key = rowKey(row);
          return <SummaryCell entries={row.cells[index]} timeZone={row.timeZoneId} reportRoute={reportRoute}
            context={`${row.employeeName} · ${row.constructionName} · ${formatDate(date)}`}
            selected={activeRowKey === key && activeDay === date} rowId={key} date={date} position={`${rowIndex}:${column}`}
            keyboardHelp={keyboardHelp} onSelect={select} />;
        },
      };
    }),
  ];
  const details = <section aria-label="Подробности клетки">
    <Card>
      <div>
        <h2 className="text-title-medium text-on-surface">{activeRow.employeeName} · {formatDate(activeDay)}</h2>
        <p className="text-body-small">{activeRow.constructionName}
          {activeRow.contact.state === 'not-tracked' && ' · Связь с устройством не отслеживается'}</p>
      </div>
      <ul className="grid gap-1 text-on-surface">
        {entries.map(entry => <li key={entry.shift}>
          {entry.shift === 'night' ? '☾ Ночная' : 'Дневная'} смена · {summaryEntryLabel(entry, activeRow.timeZoneId)}
        </li>)}
      </ul>
    </Card>
  </section>;
  return <div className="grid min-w-0 gap-3">
    {narrow && <>
      <SummaryDayPicker days={days} day={day} onChange={setChosenDay} />
      {details}
    </>}
    <p id={keyboardHelp} className="sr-only">Стрелки выбирают соседнюю клетку. Tab переводит фокус к действиям выбранной клетки.</p>
    <div className="min-w-0" onKeyDown={moveCell}>
      <DataGrid columns={columns} rows={rows} rowKey={rowKey} fixed pinned={2} density="compact" className="md:max-h-[52vh]" />
    </div>
    {!narrow && details}
    <SummaryLegend />
  </div>;
}

export function SummaryPage() {
  useDocumentTitle('Сводка');
  const { access } = useCan();
  const periods = usePeriods();
  const [search, setSearch] = useSearchParams();
  const defaults = month(periods.data?.today);
  const from = search.get('from') ?? defaults.from;
  const to = search.get('to') ?? defaults.to;
  const construction = search.get('construction') || undefined;
  const page = Number(search.get('offset'));
  const offset = Number.isInteger(page) && page >= 0 && page <= 2_147_483_647 ? page : 0;
  const query = useWorkSummary({ from, to, construction, offset });
  const data = query.data;
  const loading = (periods.isPending && !from && !to) || (query.isPending && !!from && !!to);
  const reportRoute = recordScreen('ОтчётМонтажника', access);

  function update(key: 'construction' | 'period' | 'offset', values: Record<string, string>) {
    const next = new URLSearchParams(search);
    if (key !== 'offset') next.delete('offset');
    for (const [name, value] of Object.entries(values)) {
      if (name === 'construction' && !value) next.delete(name);
      else next.set(name, value);
    }
    setSearch(next, { replace: true });
  }

  const scopeOptions = data?.constructions ?? [];
  const previous = data && data.offset > 0;
  const next = data && data.offset + data.rows.length < data.total;
  return <div className="h-full min-w-0 space-y-4 overflow-auto px-3 py-4 md:px-6">
    <PageHeader title="Сводка" subtitle={data?.boundary} />
    <div className="flex flex-wrap items-end gap-3">
      <div className="w-full sm:w-56">
        <Select label="Стройка" density="dense" value={construction ?? ''} emptyItem="Все стройки в охвате"
          onValueChange={value => update('construction', { construction: value })}>
          {scopeOptions.map(site => <SelectItem key={site.id} value={site.id}>{site.name}</SelectItem>)}
        </Select>
      </div>
      <DateRange label="Период" from={from} to={to} ordered required className="w-full sm:w-100"
        onChange={(start, end) => update('period', { from: start, to: end })} />
    </div>
    {periods.isError && (!from || !to) && <Banner tone="error">Не удалось загрузить текущий период. Укажите даты.</Banner>}
    {query.isError && <Banner tone="error">{apiError(query.error, 'Не удалось загрузить сводку.')}</Banner>}
    {loading && <p role="status" className="text-body-medium text-on-surface-variant">Загрузка сводки…</p>}
    {data && (data.rows.length > 0
      ? <SummaryGrid rows={data.rows} days={data.days} today={periods.data?.today} reportRoute={reportRoute} />
      : <EmptyState icon={<Icon icon={TableChart} size={24} />} title={data.reason === 'not-assigned'
        ? 'Вы не назначены ни на одну стройку' : 'За выбранный период нет строк сводки'} />)}
    {data && (previous || next) && <div className="flex items-center justify-end gap-2 text-body-small text-on-surface-variant">
      <IconButton icon={ChevronLeft} label="Предыдущая страница" aria-disabled={!previous}
        onClick={previous ? () => update('offset', { offset: String(Math.max(0, data.offset - data.limit)) }) : undefined} />
      <span>{data.rows.length ? `${formatCount(data.offset + 1)}–${formatCount(data.offset + data.rows.length)}` : '0'} из {formatCount(data.total)}</span>
      <IconButton icon={ChevronRight} label="Следующая страница" aria-disabled={!next}
        onClick={next ? () => update('offset', { offset: String(data.offset + data.limit) }) : undefined} />
    </div>}
  </div>;
}
