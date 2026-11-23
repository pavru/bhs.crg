import type { WorkSummaryState } from '@/shared/api/work';

export const variants = [
  { id: 'a', name: 'А · Компактный календарь' },
  { id: 'b', name: 'Б · Статусы словами' },
  { id: 'c', name: 'В · Календарь и подробности' },
] as const;

export const states = {
  report: { label: 'Отчёт с часами', short: 'Отчёт', symbol: '✓', tone: 'bg-primary-container text-on-primary-container' },
  'zero-hours': { label: 'Ноль часов', short: 'Ноль часов', symbol: '0', tone: 'bg-secondary-container text-on-secondary-container' },
  draft: { label: 'Черновик', short: 'Черновик', symbol: '◐', tone: 'bg-tertiary-container text-on-tertiary-container' },
  'not-received': { label: 'Не поступал', short: 'Не поступал', symbol: '!', tone: 'bg-error-container text-on-error-container' },
  'not-assigned': { label: 'Не назначен', short: 'Не назначен', symbol: '—', tone: 'bg-surface-container text-on-surface-variant' },
  'not-employed': { label: 'Вне периода работы', short: 'Вне найма', symbol: '×', tone: 'bg-surface-container-high text-on-surface-variant' },
  'window-open': { label: 'Окно подачи открыто', short: 'Окно открыто', symbol: '◷', tone: 'bg-secondary-container text-on-secondary-container' },
  future: { label: 'Будущий день', short: 'Будущий день', symbol: '·', tone: 'text-on-surface-variant' },
} satisfies Record<WorkSummaryState, { label: string; short: string; symbol: string; tone: string }>;

export interface Entry { state: WorkSummaryState; hours?: number; night?: boolean; status?: string }
export interface Person { id: string; employee: string; site: string; firstDays: Entry[][] }

export const sites = ['ЖК Маяк', 'Технопарк Восток'];
export const days = Array.from({ length: 31 }, (_, i) => `2026-10-${String(i + 1).padStart(2, '0')}`);
export const people: Person[] = [
  { id: '1', employee: 'Ирина Волкова', site: sites[0], firstDays: [
    [{ state: 'report', hours: 8, status: 'Принят' }, { state: 'report', hours: 4, night: true, status: 'Подан' }],
    [{ state: 'report', hours: 7.5, night: true, status: 'Возвращён' }],
    [{ state: 'zero-hours', hours: 0, status: 'Подан' }], [{ state: 'not-assigned' }],
    [{ state: 'draft' }], [{ state: 'window-open' }],
  ] },
  { id: '2', employee: 'Михаил Корнеев', site: sites[0], firstDays: [
    [{ state: 'not-received' }], [{ state: 'report', hours: 8, status: 'Принят' }],
    [{ state: 'not-received' }], [{ state: 'not-assigned' }], [{ state: 'not-assigned' }], [{ state: 'not-assigned' }],
  ] },
  { id: '3', employee: 'Софья Петрова', site: sites[1], firstDays: [
    [{ state: 'zero-hours', hours: 0, status: 'Принят' }], [{ state: 'draft', night: true }],
    [{ state: 'not-employed' }], [{ state: 'not-employed' }], [{ state: 'not-employed' }], [{ state: 'not-employed' }],
  ] },
  { id: '4', employee: 'Дмитрий Седов', site: sites[1], firstDays: [
    [{ state: 'not-assigned' }], [{ state: 'not-assigned' }], [{ state: 'report', hours: 6, status: 'Подан' }],
    [{ state: 'not-received' }], [{ state: 'report', hours: 8.5, status: 'Возвращён' }], [{ state: 'window-open' }],
  ] },
];
