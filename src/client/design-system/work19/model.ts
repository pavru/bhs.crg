export const variants = [
  { id: 'a', label: 'А · Один список', summary: 'Проверка и сводка идут друг за другом в обычном диалоге.' },
  { id: 'b', label: 'Б · Две колонки', summary: 'Слева причины запрета, справа — что закроется и что останется незавершённым.' },
  { id: 'c', label: 'В · Два шага', summary: 'Сначала проверка. На втором шаге — сводка и подтверждение.' },
] as const;

export const scenarios = [
  { id: 'blocked', label: 'Закрытие запрещено' },
  { id: 'ready', label: 'Можно закрыть' },
  { id: 'changed', label: 'Перечень изменился при закрытии' },
] as const;

export type Variant = typeof variants[number]['id'];
export type Scenario = typeof scenarios[number]['id'];

export const start = '2026-10-01';
export const end = '2026-10-31';
export const period = '1–31 октября 2026';
export const companyRule = 'Граница закрытия и «сегодня» — по поясу компании.';
export const companyScope = 'Закрытие компании закрывает и все стройки.';
export const workRule = 'Отчёт относится к периоду по дате смены, сутки — по поясу стройки.';
export const costsRule = 'Счёт относится к периоду по учётному периоду оплаты. Расходная накладная — по учётной дате, которую получает при проведении; черновик накладной к периоду не относится.';

const blocking = [
  { text: 'В окне подачи', count: 3, note: 'Дождитесь подачи отчётов, включая автоподачу после окончания окна.' },
  { text: 'Ждут приёмки', count: 5, note: '' },
  { text: 'Возвращены на правку', count: 2, note: '' },
];
const changed = [{ text: 'Ждут приёмки', count: 2, note: 'Было 0 в предыдущем перечне.' }];

export function preview(scenario: Scenario, refreshed: boolean) {
  const lines = refreshed ? changed : scenario === 'blocked' ? blocking : [];
  return {
    canClose: lines.length === 0,
    blocking: lines,
    accepted: refreshed ? 22 : 24,
  };
}

export type Preview = ReturnType<typeof preview>;
