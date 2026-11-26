export const variants = [
  { value: 'a', label: 'А · Общая таблица' },
  { value: 'b', label: 'Б · Строка и подробности' },
  { value: 'c', label: 'В · Работы по наличию данных' },
] as const;
export type Variant = (typeof variants)[number]['value'];

export const scenarios = [
  { value: 'partial', label: 'Факт неполный' },
  { value: 'complete', label: 'Факт без исключений' },
  { value: 'work-off', label: 'Учёт работ выключен' },
  { value: 'fact-only', label: 'Только право читать факт' },
  { value: 'plan-only', label: 'Только чтение плана' },
  { value: 'schedule-only', label: 'Только чтение графика' },
  { value: 'costs-only', label: 'Только право читать итоги затрат' },
  { value: 'costs-off', label: 'Счета и накладные выключены' },
  { value: 'denied', label: 'Нет доступа к данным' },
] as const;
export type Scenario = (typeof scenarios)[number]['value'];

export const sections = [
  { value: 'all', label: 'Вся стройка' },
  { value: 'office', label: 'Административный корпус' },
  { value: 'workshop', label: 'Производственный корпус' },
] as const;
export type Section = (typeof sections)[number]['value'];

export const permissions = {
  plan: 'plan.estimate.read',
  schedule: 'plan.schedule.read',
  fact: 'work.facts.read',
  costs: 'costs.totals.read',
};

export type WorkRow = {
  id: string;
  name: string;
  unit: string;
  plan?: number | null;
  fact?: number;
  dates?: string;
};

// Каждый срез — готовый ответ для макета. Суммы, объёмы и границы дат в браузере не пересчитываются.
const snapshots: Record<Section, { rows: WorkRow[]; costs: { allocated: number; paid: number } }> = {
  all: {
    rows: [
      { id: 'trays', name: 'Монтаж кабельных лотков', unit: 'м', plan: 620, fact: 418, dates: '05.11–27.11.2026' },
      { id: 'cable', name: 'Прокладка силового кабеля', unit: 'м', plan: 1480, fact: 962, dates: '09.11–04.12.2026' },
      { id: 'boards', name: 'Монтаж распределительных щитов', unit: 'шт.', plan: 18, fact: 12, dates: '16.11–30.11.2026' },
      { id: 'lights', name: 'Монтаж аварийных светильников', unit: 'шт.', plan: 36, dates: '30.11–08.12.2026' },
      { id: 'measure', name: 'Измерение сопротивления изоляции', unit: 'шт.', plan: 24, dates: '07.12–11.12.2026' },
      { id: 'marking', name: 'Маркировка кабельных трасс', unit: 'м', fact: 124 },
      { id: 'sleeves', name: 'Монтаж защитных гильз', unit: 'шт.', fact: 8 },
      { id: 'label', name: 'Нанесение предупредительных знаков', unit: 'шт.', plan: 0, dates: '07.12–11.12.2026' },
      { id: 'control', name: 'Прокладка кабеля управления', unit: 'м', plan: 100 },
      { id: 'signals', name: 'Прокладка контрольного кабеля', unit: 'м', plan: 60 },
    ],
    costs: { allocated: 2340000, paid: 2100000 },
  },
  office: {
    rows: [
      { id: 'trays', name: 'Монтаж кабельных лотков', unit: 'м', plan: 210, fact: 184, dates: '05.11–21.11.2026' },
      { id: 'cable', name: 'Прокладка силового кабеля', unit: 'м', plan: 480, fact: 392, dates: '09.11–28.11.2026' },
      { id: 'boards', name: 'Монтаж распределительных щитов', unit: 'шт.', plan: 6, fact: 4, dates: '16.11–24.11.2026' },
      { id: 'lights', name: 'Монтаж аварийных светильников', unit: 'шт.', plan: 12, dates: '30.11–04.12.2026' },
      { id: 'marking', name: 'Маркировка кабельных трасс', unit: 'м', fact: 124 },
    ],
    costs: { allocated: 840000, paid: 770000 },
  },
  workshop: {
    rows: [
      { id: 'trays', name: 'Монтаж кабельных лотков', unit: 'м', plan: 410, fact: 234, dates: '12.11–27.11.2026' },
      { id: 'cable', name: 'Прокладка силового кабеля', unit: 'м', plan: 600, fact: 570, dates: '16.11–04.12.2026' },
      { id: 'boards', name: 'Монтаж распределительных щитов', unit: 'шт.', plan: 12, fact: 8, dates: '19.11–30.11.2026' },
      { id: 'lights', name: 'Монтаж аварийных светильников', unit: 'шт.', plan: 24, dates: '01.12–08.12.2026' },
      { id: 'measure', name: 'Измерение сопротивления изоляции', unit: 'шт.', plan: 24, dates: '07.12–11.12.2026' },
      { id: 'sleeves', name: 'Монтаж защитных гильз', unit: 'шт.', fact: 8 },
      { id: 'label', name: 'Нанесение предупредительных знаков', unit: 'шт.', plan: 0, dates: '07.12–11.12.2026' },
    ],
    costs: { allocated: 1500000, paid: 1330000 },
  },
};

const estimates = [
  {
    name: 'Электроснабжение', revision: 3, unmatched: 12,
    examples: ['Установка маркировочных табличек', 'Монтаж держателей кабеля'],
  },
  {
    name: 'Освещение', revision: 2, unmatched: 5,
    examples: ['Установка указателей эвакуации', 'Монтаж креплений светильников'],
  },
];

export type Estimate = (typeof estimates)[number];

const grants: Record<Scenario, readonly (keyof typeof permissions)[]> = {
  partial: ['plan', 'schedule', 'fact', 'costs'],
  complete: ['plan', 'schedule', 'fact', 'costs'],
  'work-off': ['plan', 'schedule', 'costs'],
  'fact-only': ['fact'],
  'plan-only': ['plan'],
  'schedule-only': ['schedule'],
  'costs-only': ['costs'],
  'costs-off': ['plan', 'schedule', 'fact'],
  denied: [],
};

export function progressSample(scenario: Scenario, section: Section) {
  const visible = grants[scenario];
  const plan = visible.includes('plan');
  const schedule = visible.includes('schedule');
  const fact = visible.includes('fact');
  const costs = visible.includes('costs');
  const snapshot = snapshots[section];
  const rows = snapshot.rows.flatMap(({ plan: volume, fact: actual, dates, ...base }) => {
    if (!(plan && volume !== undefined) && !(schedule && dates) && !(fact && actual !== undefined)) return [];
    return [{
      ...base,
      ...(plan && volume !== undefined && { plan: volume }),
      ...(schedule && dates && { dates }),
      ...(fact && actual !== undefined && { fact: actual }),
    }];
  });
  return {
    permitted: visible.length > 0, plan, schedule, fact, rows,
    workOff: scenario === 'work-off',
    costsOff: scenario === 'costs-off',
    partial: fact && scenario !== 'complete',
    estimates: plan ? estimates : [],
    costs: costs ? snapshot.costs : undefined,
  };
}

export type ProgressSample = ReturnType<typeof progressSample>;
