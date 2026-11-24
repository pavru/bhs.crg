import { memo, useState } from 'react';
import { Link } from 'react-router';
import type { SummaryEntry } from '@/shared/api/workSummary';
import { formatNumber } from '@/shared/format/format';
import { Button } from '@/shared/ui/Button';
import { recordLink, type RecordRoute } from '@/shared/ui/recordRoutes';
import { Tooltip } from '@/shared/ui/Tooltip';
import { summaryEntryLabel, summaryStateView } from './summaryPresentation';

export const SummaryCell = memo(function SummaryCell({ entries, timeZone, reportRoute, context, selected, rowId, date,
  position, keyboardHelp, onSelect }: {
  entries: SummaryEntry[]; timeZone: string; reportRoute: RecordRoute | null; context: string;
  selected: boolean; rowId: string; date: string; position: string; keyboardHelp: string;
  onSelect: (rowId: string, date: string) => void;
}) {
  const [draftOpen, setDraftOpen] = useState(false);
  const [cellFocused, setCellFocused] = useState(false);
  const select = () => onSelect(rowId, date);
  const tabIndex = selected ? 0 : -1;
  const labels = entries.map(entry => `${entry.shift === 'night' ? 'Ночная' : 'Дневная'} смена. ${summaryEntryLabel(entry, timeZone)}`);
  return <div className="grid gap-1 rounded-sm focus-ring" data-summary-cell={position} role="group"
    aria-label={[context, ...labels].join(' · ')} aria-describedby={keyboardHelp} tabIndex={tabIndex} onPointerEnter={select}
    onFocus={event => { select(); setCellFocused(event.target === event.currentTarget); }}
    onBlur={() => setCellFocused(false)}>
    {entries.map((entry, index) => {
      const night = entry.shift === 'night';
      const state = summaryStateView[entry.state];
      const label = labels[index];
      const report = entry.state === 'report' || entry.state === 'zero-hours';
      const value = report && entry.hours != null ? `${formatNumber(entry.hours)} ч` : state.symbol;
      const text = <>{night && <span aria-hidden>☾</span>}{value}</>;
      const classes = `inline-flex min-h-10 w-full items-center justify-center gap-1 rounded-full px-1 text-label-medium tabular-nums ${state.tone}`;
      const link = report ? recordLink(reportRoute, entry.reportId, `${context} · ${label}`) : null;
      if (link) return <Tooltip content={label} key={entry.shift}>
        <Link className={`${classes} state-layer focus-ring`} to={link.to} aria-label={link.label} tabIndex={tabIndex}>{text}</Link>
      </Tooltip>;
      if (entry.state === 'draft') return <Tooltip content={label} key={entry.shift} open={selected && draftOpen}>
        <Button aria-label={label} fullWidth className={`!px-1 ${state.tone}`} tabIndex={tabIndex} onClick={() => {
          select();
          setDraftOpen(open => !open);
        }}>{text}</Button>
      </Tooltip>;
      return <Tooltip content={state.label} open={cellFocused} key={entry.shift}>
        <span className={classes}><span aria-hidden>{text}</span><span className="sr-only">{state.label}</span></span>
      </Tooltip>;
    })}
  </div>;
});
