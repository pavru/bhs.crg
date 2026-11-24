import type { CSSProperties } from 'react';
import { Button } from '@/shared/ui/Button';
import { daysOff, shortDate, type Work, type WorkLink } from './sample';
import { WarningMark } from './WorkList';

const dayMs = 86_400_000;
const origin = Date.parse('2026-11-24');
const days = Array.from({ length: 14 }, (_, index) => new Date(origin + index * dayMs).toISOString().slice(0, 10));
const dateIndex = (date: string) => (Date.parse(date) - origin) / dayMs;
const rowHeight = 48;

function coordinates(work: Work) {
  if (!work.start || !work.end) return null;
  const start = Math.max(0, dateIndex(work.start));
  const end = Math.min(days.length, dateIndex(work.end) + 1);
  return end > start ? { start, end } : null;
}

export function Timeline({ rows, links, onOpen }: {
  rows: Work[]; links: WorkLink[]; onOpen: (work: Work) => void;
}) {
  if (!rows.length) return <p className="plan-schedule-panel text-body-medium">Работ по выбранным условиям нет.</p>;
  return <section className="plan-schedule-panel" aria-label="Диаграмма графика">
    <div className="flex flex-wrap items-center justify-between gap-2">
      <h3 className="text-title-medium">24 ноября — 7 декабря 2026</h3>
      <span className="text-body-small text-on-surface-variant plan-schedule-legend">Нерабочие дни</span>
    </div>
    <div className="plan-schedule-timeline">
      <div className="plan-schedule-timeline-labels">
        <span>Работа</span>
        {rows.map(work => <div key={work.id}><WarningMark work={work} />{work.name}</div>)}
      </div>
      <div className="plan-schedule-timeline-track">
        <div className="plan-schedule-day-columns" aria-hidden>
          {days.map(date => <div key={date} data-off={daysOff.includes(date)}>{shortDate(date)}</div>)}
        </div>
        <div className="plan-schedule-bars">
          {rows.map(work => {
            const range = coordinates(work);
            return <div key={work.id} className="plan-schedule-bar-row">
              {range ? <Button variant="tonal" className="plan-schedule-bar" style={{
                '--start': range.start, '--length': range.end - range.start,
              } as CSSProperties} onClick={() => onOpen(work)} aria-label={`Открыть в списке: ${work.name}`}>
                {shortDate(work.start)} — {shortDate(work.end)}
              </Button> : <Button variant="text" onClick={() => onOpen(work)}>Открыть в списке</Button>}
            </div>;
          })}
          <svg className="plan-schedule-arrows" viewBox={`0 0 1400 ${rows.length * rowHeight}`}
            preserveAspectRatio="none" aria-hidden>
            <defs><marker id="schedule-arrow" viewBox="0 0 8 8" refX="7" refY="4" markerWidth="6" markerHeight="6" orient="auto">
              <path d="M0 0 L8 4 L0 8 Z" fill="currentColor" />
            </marker></defs>
            {links.map(link => {
              const from = rows.findIndex(work => work.id === link.predecessor);
              const to = rows.findIndex(work => work.id === link.successor);
              const a = from >= 0 && coordinates(rows[from]);
              const b = to >= 0 && coordinates(rows[to]);
              if (!a || !b) return null;
              const x = a.end * 100 - 8;
              const target = b.start * 100 + 8;
              const y1 = from * rowHeight + 24;
              const y2 = to * rowHeight + 24;
              return <path key={`${link.predecessor}/${link.successor}`}
                d={`M ${x} ${y1} h 16 V ${y2 - 22} H ${target - 12} V ${y2} H ${target}`}
                fill="none" stroke="currentColor" vectorEffect="non-scaling-stroke" markerEnd="url(#schedule-arrow)" />;
            })}
          </svg>
        </div>
      </div>
    </div>
  </section>;
}
