import { useId, useState } from 'react';
import { useNonWorkingDays } from '@/shared/api/planningCalendars';
import type { ScheduleLink, ScheduleWarning, ScheduleWork } from '@/shared/api/planningScheduleTypes';
import { formatDate } from '@/shared/format/format';
import { apiError } from '@/shared/utils/apiError';
import { Banner } from '@/shared/ui/Banner';
import { Button, IconButton } from '@/shared/ui/Button';
import { DateField } from '@/shared/ui/DateField';
import { ChevronLeft, ChevronRight } from '@/shared/ui/icons';
import { ScheduleWarningMark } from './ScheduleWorksGrid';
import { dateAt, dayOf, FIRST_DAY, LAST_DAY, RANGE_DAYS, rangeStart, visibleWorkDays } from './scheduleDays';

const TRACK_DAY = 34;
const ROW_HEIGHT = 48;
const HEADER_HEIGHT = 40;
const TRACK_WIDTH = TRACK_DAY * RANGE_DAYS;

export function ScheduleGantt({ constructionId, scheduleId, rows, links, warnings, onOpen }: {
  constructionId: string; scheduleId: string; rows: ScheduleWork[]; links: ScheduleLink[]; warnings: Map<string, ScheduleWarning[]>;
  onOpen: (workId: string) => void;
}) {
  const firstDate = rows.flatMap(work => [work.start, work.end]).filter(date => date !== null).sort()[0];
  if (!rows.length) return <p className="text-body-medium">Работ по выбранным условиям нет.</p>;
  // Если сроки ещё не назначены, строки остаются доступны и открываются в списке.
  if (!firstDate) return <div className="grid gap-2">
    <p className="text-body-medium">У работ пока нет сроков.</p>
    <ul>{rows.map(work => <li key={work.id} className="flex items-center gap-1">
      <ScheduleWarningMark warnings={warnings.get(work.id) ?? []} />
      <Button variant="text" onClick={() => onOpen(work.id)}>{work.name}</Button>
    </li>)}</ul>
  </div>;
  return <CalendarPlot constructionId={constructionId} scheduleId={scheduleId} firstDate={firstDate}
    rows={rows} links={links} warnings={warnings} onOpen={onOpen} />;
}

function CalendarPlot({ constructionId, scheduleId, firstDate, rows, links, warnings, onOpen }: {
  constructionId: string; scheduleId: string; firstDate: string; rows: ScheduleWork[]; links: ScheduleLink[];
  warnings: Map<string, ScheduleWarning[]>; onOpen: (workId: string) => void;
}) {
  const [start, setStart] = useState(() => rangeStart(dayOf(firstDate)));
  const from = dateAt(start);
  const to = dateAt(start + RANGE_DAYS - 1);
  const query = useNonWorkingDays(constructionId, from, to);
  const off = new Set(query.data?.nonWorkingDays);
  const arrowId = useId();
  const height = HEADER_HEIGHT + ROW_HEIGHT * rows.length;
  const positions = new Map(rows.map((work, index) => [work.id, { work, range: visibleWorkDays(work, start),
    center: HEADER_HEIGHT + index * ROW_HEIGHT + ROW_HEIGHT / 2 }]));
  const arrows = links.flatMap<{ id: string; path: string; label: string | null }>(link => {
    const predecessor = positions.get(link.predecessorId);
    const successor = positions.get(link.successorId);
    if (!predecessor?.range && !successor?.range) return [];

    const remotePredecessor = link.predecessor?.scheduleId !== scheduleId ? link.predecessor : undefined;
    const remoteSuccessor = link.successor?.scheduleId !== scheduleId ? link.successor : undefined;
    const source = dependencyEndpoint(predecessor, start, 'end')
      ?? (remotePredecessor && successor ? boundary('left', successor.center) : null);
    const target = dependencyEndpoint(successor, start, 'start')
      ?? (remoteSuccessor && predecessor ? boundary('right', predecessor.center) : null);
    if (!source || !target) return [];

    const depart = Math.min(TRACK_WIDTH - 2, source.x + (source.side === 'right' ? -8 : 8));
    const approach = Math.max(2, target.x + (target.side === 'left' ? 8 : -8));
    const track = target.y - (target.side ? 5 : ROW_HEIGHT / 2);
    const path = `M ${source.x} ${source.y} H ${depart} V ${track} H ${approach} V ${target.y} H ${target.x}`;
    const context = remotePredecessor || remoteSuccessor ? 'связь между графиками' : [
      source.side && `окончание ${source.side === 'left' ? 'до' : 'после'} периода`,
      target.side && `начало ${target.side === 'left' ? 'до' : 'после'} периода`,
    ].filter(Boolean).join(', ');
    const predecessorName = remotePredecessor ? `${remotePredecessor.scheduleName} · ${remotePredecessor.name}` : predecessor?.work.name;
    const successorName = remoteSuccessor ? `${remoteSuccessor.scheduleName} · ${remoteSuccessor.name}` : successor?.work.name;
    return [{ id: link.id, path, label: context ? `${predecessorName} → ${successorName} (${context})` : null }];
  });
  const boundaryArrows = arrows.filter(arrow => arrow.label !== null);

  return <div className="grid min-w-0 grid-cols-1 gap-3">
    <div className="flex flex-wrap items-center gap-2">
      <IconButton icon={ChevronLeft} label="Предыдущий период" disabled={start === FIRST_DAY}
        onClick={() => setStart(rangeStart(start - RANGE_DAYS))} />
      <div className="w-48 max-w-full"><DateField label="Начало периода" value={from} density="dense"
        onChange={date => { if (date) setStart(rangeStart(dayOf(date))); }} /></div>
      <IconButton icon={ChevronRight} label="Следующий период" disabled={start + RANGE_DAYS - 1 === LAST_DAY}
        onClick={() => setStart(rangeStart(start + RANGE_DAYS))} />
      <span className="text-body-medium text-on-surface-variant">До {formatDate(to)}</span>
      <span className="inline-flex items-center gap-1.5 text-body-small text-on-surface-variant">
        <span className="size-3 border border-outline-variant bg-surface-container-highest" aria-hidden />Нерабочие дни
      </span>
    </div>
    {query.isPending && <p role="status" className="text-body-medium">Читаем нерабочие дни стройки…</p>}
    {query.isError && <Banner tone="error" actions={[{ label: 'Повторить', onClick: () => void query.refetch() }]}>
      {apiError(query.error, 'Календарь диаграммы не прочитался.')}
    </Banner>}
    {!!query.data?.gaps.length && <Banner tone="warning"><div>
      <ul>{query.data.gaps.map(gap => <li key={gap.year}>{gap.text}</li>)}</ul>
      Для этих лет используются правила рабочей недели.
    </div></Banner>}
    {query.data && <div className="min-w-0 overflow-auto border-y border-outline-variant">
      <div className="flex w-max min-w-full">
        <div className="sticky left-0 z-10 w-60 shrink-0 border-r border-outline-variant bg-surface">
          <div className="flex h-10 items-center border-b border-outline-variant px-3 text-title-small">Работа</div>
          {rows.map(work => <div key={work.id} className="flex h-12 items-center gap-1 border-b border-outline-variant px-2">
            <ScheduleWarningMark warnings={warnings.get(work.id) ?? []} />
            <Button variant="text" className="min-w-0" aria-label={`Открыть в списке: ${work.name}`}
              onClick={() => onOpen(work.id)}><span className="truncate">{work.name}</span></Button>
          </div>)}
        </div>
        <svg className="shrink-0" width={TRACK_WIDTH} height={height} aria-label="Сроки работ по рабочему календарю стройки">
          <defs>
            <marker id={arrowId} viewBox="0 0 8 8" refX="7" refY="4" markerWidth="6" markerHeight="6" orient="auto">
              <path d="M0 0 L8 4 L0 8 Z" fill="currentColor" className="text-on-surface-variant" />
            </marker>
          </defs>
          {Array.from({ length: RANGE_DAYS }, (_, index) => {
            const date = dateAt(start + index);
            const dayOff = off.has(date);
            const x = index * TRACK_DAY;
            return <g key={date} aria-hidden>
              {dayOff && <rect x={x} width={TRACK_DAY} height={height} data-non-working-date={date}
                className="fill-surface-container-highest" />}
              <path d={`M ${x} 0 V ${height}`} stroke="currentColor" className="text-outline-variant" />
              <text x={x + TRACK_DAY / 2} y={25} textAnchor="middle" className="fill-on-surface-variant text-body-small">
                {date.slice(8)}
              </text>
            </g>;
          })}
          {rows.map(work => {
            const position = positions.get(work.id)!;
            const range = position.range;
            return <g key={work.id}>
              <path d={`M 0 ${position.center - ROW_HEIGHT / 2} H ${TRACK_WIDTH}`} stroke="currentColor" className="text-outline-variant" />
              {range && <g role="button" tabIndex={0} aria-label={`${work.name}, ${formatDate(work.start!)} — ${formatDate(work.end!)}. Открыть в списке`}
                className="cursor-pointer focus-ring" onClick={() => onOpen(work.id)} onKeyDown={event => {
                  if (event.key === 'Enter' || event.key === ' ') { event.preventDefault(); onOpen(work.id); }
                }}>
                <rect x={range.start * TRACK_DAY + 2} y={position.center - 12} rx={8} height={24}
                  width={(range.end - range.start) * TRACK_DAY - 4} className="fill-secondary-container" />
              </g>}
            </g>;
          })}
          {arrows.map(arrow => <path key={arrow.id} aria-hidden d={arrow.path}
            className="pointer-events-none text-on-surface-variant" stroke="currentColor" fill="none" markerEnd={`url(#${arrowId})`} />)}
        </svg>
      </div>
    </div>}
    {query.data && boundaryArrows.length > 0 && <ul aria-label="Связи за пределами диаграммы" className="grid gap-1 text-body-medium">
      {boundaryArrows.map(arrow => <li key={arrow.id}>{arrow.label}</li>)}
    </ul>}
  </div>;
}

function boundary(side: 'left' | 'right', center: number) {
  return { x: side === 'left' ? 2 : TRACK_WIDTH - 2, y: center - 16, side };
}

function dependencyEndpoint(position: { work: ScheduleWork; center: number } | undefined, from: number, endpoint: 'start' | 'end') {
  if (!position) return null;
  const { start, end } = position.work;
  if (!start || !end) return null;
  const day = dayOf(endpoint === 'start' ? start : end) - from;
  if (day < 0) return boundary('left', position.center);
  if (day >= RANGE_DAYS) return boundary('right', position.center);
  const x = endpoint === 'start' ? day * TRACK_DAY + 2 : (day + 1) * TRACK_DAY - 2;
  return { x, y: position.center, side: null };
}
