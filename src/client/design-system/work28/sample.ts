import type { ProgressColumn, ProgressCosts, ProgressRow, ProgressValue } from '@/shared/api/progress';

export const variants = [
  { value: 'a', label: 'А · Сводка в строке' },
  { value: 'b', label: 'Б · Панель подробностей' },
  { value: 'c', label: 'В · Раскрытие на месте' },
] as const;
export type Variant = (typeof variants)[number]['value'];

export const scenarios = [
  { value: 'normal', label: 'Обычный расчёт' },
  { value: 'partial', label: 'Есть неразнесённые часы' },
  { value: 'small-sample', label: 'Нет принятых · данных мало' },
  { value: 'hidden', label: 'Скрыто правами' },
  { value: 'no-norms', label: 'Нет сметных норм' },
  { value: 'work-off', label: 'Учёт работ выключен' },
  { value: 'plan-off', label: 'Планирование выключено' },
] as const;
export type Scenario = (typeof scenarios)[number]['value'];

export const sections = [
  { value: 'all', label: 'Вся стройка' },
  { value: 'office', label: 'Административный корпус' },
  { value: 'production', label: 'Производственный корпус' },
] as const;
export type Section = (typeof sections)[number]['value'];

export const periods = [
  { value: 'november', label: 'Ноябрь 2026' },
  { value: 'october', label: 'Октябрь 2026' },
] as const;
export type Period = (typeof periods)[number]['value'];

export function option<T extends string>(items: readonly { value: T }[], value: string | null, fallback: T): T {
  return items.find(item => item.value === value)?.value ?? fallback;
}

type Rate = { state: 'ok'; perHour: number; reports: number; sites: number }
  | { state: 'too-little-data'; reports: number; sites: number }
  | { state: 'hidden' | 'not-connected' };
export type MonthlyRate = Rate & { month: string };

export interface Work {
  id: string;
  name: string;
  section: Exclude<Section, 'all'>;
  unit: string;
  plan: number;
  fact: number;
  start: string;
  end: string;
  rates: MonthlyRate[];
}

export const works: Work[] = [
  { id: 'cable', name: 'Прокладка силового кабеля', section: 'office', unit: 'м', plan: 480, fact: 392,
    start: '2026-11-09', end: '2026-11-28', rates: [
      { month: 'Ноябрь 2026', state: 'ok', perHour: 4.8, reports: 14, sites: 3 },
      { month: 'Октябрь 2026', state: 'too-little-data', reports: 4, sites: 1 },
    ] },
  { id: 'trays', name: 'Монтаж кабельных лотков', section: 'production', unit: 'м', plan: 410, fact: 234,
    start: '2026-11-12', end: '2026-11-27', rates: [
      { month: 'Ноябрь 2026', state: 'ok', perHour: 3.2, reports: 12, sites: 2 },
      { month: 'Октябрь 2026', state: 'ok', perHour: 2.9, reports: 10, sites: 2 },
    ] },
  { id: 'boards', name: 'Монтаж распределительных щитов', section: 'office', unit: 'шт.', plan: 6, fact: 4,
    start: '2026-11-16', end: '2026-11-24', rates: [
      { month: 'Ноябрь 2026', state: 'too-little-data', reports: 6, sites: 2 },
      { month: 'Октябрь 2026', state: 'too-little-data', reports: 3, sites: 1 },
    ] },
  { id: 'lights', name: 'Монтаж аварийных светильников', section: 'production', unit: 'шт.', plan: 24, fact: 12,
    start: '2026-11-19', end: '2026-11-30', rates: [
      { month: 'Ноябрь 2026', state: 'ok', perHour: 1.2, reports: 10, sites: 2 },
      { month: 'Октябрь 2026', state: 'too-little-data', reports: 2, sites: 1 },
    ] },
];

export function rates(work: Work, scenario: Scenario): MonthlyRate[] {
  if (scenario === 'hidden' || scenario === 'work-off') {
    return [{ month: work.rates[0].month, state: scenario === 'hidden' ? 'hidden' : 'not-connected' }];
  }
  if (scenario === 'small-sample') {
    return [{ month: work.rates[0].month, state: 'too-little-data', reports: 4, sites: 1 }];
  }
  return work.rates;
}

export function latest(work: Work, scenario: Scenario): MonthlyRate {
  const months = rates(work, scenario);
  return months.find(month => month.state === 'ok') ?? months[0];
}

const snapshots: Record<Period, Record<Section, { estimate: number; actual: number; extra: string }>> = {
  november: {
    all: { estimate: 416, actual: 462, extra: 'Несметные работы · 16 ч, маркировка трасс 124 м' },
    office: { estimate: 156, actual: 172.5, extra: 'Несметные работы · маркировка трасс 48 м' },
    production: { estimate: 260, actual: 289.5, extra: 'Несметные работы · маркировка трасс 76 м' },
  },
  october: {
    all: { estimate: 204, actual: 219, extra: 'Несметные работы · 4 ч, маркировка трасс 36 м' },
    office: { estimate: 84, actual: 92, extra: 'Несметные работы · маркировка трасс 12 м' },
    production: { estimate: 120, actual: 127, extra: 'Несметные работы · маркировка трасс 24 м' },
  },
};

export type Labor = { estimate: number; actual: number; distributed: boolean; extra: string; residual?: string }
  | { message: string; extra?: string } | { permission: string };

export function labor(scenario: Scenario, section: Section, period: Period): Labor {
  if (scenario === 'plan-off') return { message: 'Планирование не подключено.' };
  if (scenario === 'hidden') return { permission: 'plan.labor.read' };
  if (scenario === 'small-sample') return { message: 'За выбранный период нет принятых отчётов.' };
  if (scenario === 'no-norms') return { message: 'Для принятых объёмов нет числовых сметных норм.', extra: section === 'all'
    ? 'Несметные работы · 16 ч, маркировка трасс 124 м' : 'Несметные работы · маркировка трасс 48 м' };
  return { ...snapshots[period][section], distributed: section !== 'all', residual: scenario === 'partial'
    ? (section === 'all' ? 'Неразнесённые часы · 18 ч в остатке стройки' : 'Часть часов раздела осталась в остатке стройки')
    : undefined };
}

const volume = (v: number): ProgressValue => ({ volume: v, start: null, end: null });

export function progress(scenario: Scenario, section: Section): { columns: ProgressColumn[]; rows: ProgressRow[] } {
  const connected = scenario !== 'work-off';
  const planned = scenario !== 'plan-off';
  const columns: ProgressColumn[] = [];
  if (planned) columns.push({ code: 'plan', title: 'План по смете', state: 'ok', kind: 'Volume' });
  if (connected) columns.push({ code: 'fact', title: 'Факт по учёту', state: scenario === 'partial' ? 'partial' : 'ok', kind: 'Volume' });
  if (planned) columns.push({ code: 'dates', title: 'Срок по графику', state: 'ok', kind: 'Dates' });
  return { columns, rows: works.filter(work => section === 'all' || work.section === section).map(work => ({
    workTypeId: work.id, unitId: work.unit, workTypeName: work.name, unitName: work.unit,
    cells: {
      plan: planned ? volume(work.plan) : undefined,
      fact: connected && scenario !== 'small-sample' ? volume(work.fact) : undefined,
      dates: planned ? { volume: null, start: work.start, end: work.end } : undefined,
    },
  })) };
}

export const costs: ProgressCosts = { state: 'hidden', permission: 'costs.totals.read' };
