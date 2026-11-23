import type { WorkSummaryState } from '@/shared/api/work';
import type { SummaryEntry } from '@/shared/api/workSummary';
import { formatDayTime, formatNumber } from '@/shared/format/format';

export const summaryStateView = {
  report: { label: 'Отчёт с часами', symbol: '✓', tone: 'bg-primary-container text-on-primary-container' },
  'zero-hours': { label: 'Ноль часов', symbol: '0', tone: 'bg-secondary-container text-on-secondary-container' },
  draft: { label: 'Черновик', symbol: '◐', tone: 'bg-tertiary-container text-on-tertiary-container' },
  'not-received': { label: 'Не поступал', symbol: '!', tone: 'bg-error-container text-on-error-container' },
  'not-assigned': { label: 'Не назначен', symbol: '—', tone: 'bg-surface-container text-on-surface-variant' },
  'not-employed': { label: 'Вне периода работы', symbol: '×', tone: 'bg-surface-container-high text-on-surface-variant' },
  'window-open': { label: 'Окно подачи открыто', symbol: '◷', tone: 'bg-secondary-container text-on-secondary-container' },
  future: { label: 'Будущий день', symbol: '·', tone: 'text-on-surface-variant' },
} satisfies Record<WorkSummaryState, { label: string; symbol: string; tone: string }>;

const statusLabels = { submitted: 'Подан', accepted: 'Принят', returned: 'Возвращён' } as const;

export function summaryEntryLabel(entry: SummaryEntry, timeZone: string) {
  if (entry.state === 'draft') return `Черновик. Автоподача ${formatDayTime(entry.autoSubmitAt, timeZone)} по времени стройки`;
  if (entry.state === 'report' || entry.state === 'zero-hours')
    return `${entry.hours == null ? 'Часы не указаны' : `${formatNumber(entry.hours)} ч`} · ${statusLabels[entry.status]}`;
  return summaryStateView[entry.state].label;
}
