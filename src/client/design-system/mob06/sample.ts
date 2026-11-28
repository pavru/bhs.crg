import { newLocalId } from '@/shared/utils/localId';

export const variants = {
  a: { title: 'Карточка отчёта', description: 'Работы и часы видны сразу. Добавление открывает отдельную форму.' },
  b: { title: 'Поиск в отчёте', description: 'Поиск и недавние работы рядом со строками текущей смены.' },
  c: { title: 'Ввод по шагам', description: 'Сначала работа, затем обязательные параметры и, при необходимости, бригада.' },
} as const;

export type Variant = keyof typeof variants;
export type Shift = 'day' | 'night';
export type Life = 'draft' | 'returned' | 'submitted' | 'accepted';
export type Delivery = 'local' | 'delivered';

export interface Work {
  id: string;
  name: string;
  unit: string;
  variants?: string[];
  place?: boolean;
  material?: boolean;
  web?: boolean;
}

export const work: Work[] = [
  { id: 'cable', name: 'Прокладка кабеля', unit: 'м', variants: ['В лотке', 'В трубе'], place: true, material: true, web: true },
  { id: 'tray', name: 'Монтаж кабельного лотка', unit: 'м', variants: ['Лестничный', 'Перфорированный'], place: true, material: true },
  { id: 'panel', name: 'Монтаж электрощита', unit: 'шт.', place: true, web: true },
  { id: 'marking', name: 'Маркировка линий', unit: 'шт.' },
  { id: 'measure', name: 'Проверка изоляции', unit: 'линия' },
];

export const sites = [
  { id: 'east', name: 'ЖК «Восточный»', sections: ['К1', 'К2'], workIds: ['cable', 'tray', 'panel', 'measure'] },
  { id: 'port', name: 'Терминал «Морской»', sections: ['Блок А', 'Блок Б'], workIds: ['tray', 'marking', 'measure'] },
];

export const crew = ['Алексей Сорокин', 'Марина Белова', 'Илья Орлов'];
export const today = '2026-11-28';
export const previous = '2026-11-27';

export interface Line {
  id: string;
  workId: string;
  name: string;
  variant: string;
  section: string;
  unit: string;
  quantity: string;
  place: string;
  material: string;
  crew: string[];
  note: string;
  photo: boolean;
}

export interface Report {
  id: string;
  siteId: string | null;
  siteName: string;
  date: string;
  shift: Shift;
  status: Life;
  delivery: Delivery;
  hours: string;
  lines: Line[];
  returnNote?: string;
}

export const lifeLabels: Record<Life, string> = {
  draft: 'Черновик', returned: 'Возвращён', submitted: 'Подан', accepted: 'Принят',
};

export const deliveryLabels: Record<Delivery, string> = {
  local: 'На телефоне', delivered: 'Доставлен',
};

export function blankLine(workItem?: Work): Line {
  return {
    id: newLocalId(), workId: workItem?.id ?? '', name: workItem?.name ?? '',
    variant: '', section: '', unit: workItem?.unit ?? '', quantity: '', place: '', material: '',
    crew: [], note: '', photo: false,
  };
}

export function sampleReports(): Report[] {
  const cable = { ...blankLine(work[0]), variant: 'В лотке', section: 'К1', quantity: '42', material: 'ВВГнг 3×2,5' };
  const tray = { ...blankLine(work[1]), variant: 'Перфорированный', section: 'К2', quantity: '18', place: '2 этаж', material: 'Лоток 100×50' };
  const marking = { ...blankLine(work[3]), section: 'Блок А', quantity: '12' };
  return [
    {
      id: 'returned', siteId: 'east', siteName: sites[0].name, date: previous, shift: 'night',
      status: 'returned', delivery: 'delivered', hours: '8', lines: [cable, tray],
      returnNote: 'Уточните этаж и оси в строке «Прокладка кабеля». Для К2 место указано, эту строку можно оставить как есть.',
    },
    {
      id: 'draft', siteId: 'port', siteName: sites[1].name, date: today, shift: 'day',
      status: 'draft', delivery: 'local', hours: '4', lines: [marking],
    },
    {
      id: 'submitted', siteId: 'east', siteName: sites[0].name, date: previous, shift: 'day',
      status: 'submitted', delivery: 'local', hours: '7,5', lines: [{ ...tray, id: newLocalId() }],
    },
    {
      id: 'accepted', siteId: 'port', siteName: sites[1].name, date: '2026-11-26', shift: 'day',
      status: 'accepted', delivery: 'delivered', hours: '8', lines: [{ ...marking, id: newLocalId() }],
    },
  ];
}

export function shortDate(date: string) {
  const [, month, day] = date.split('-');
  return `${day}.${month}`;
}

export function needsLineFields(line: Line, linked: boolean): string[] {
  const item = work.find(entry => entry.id === line.workId);
  const required = [
    [!line.name.trim(), 'Вид работы'], [!line.unit, 'Единица'],
    [!/^\d+(?:[,.]\d+)?$/.test(line.quantity) || Number(line.quantity.replace(',', '.')) <= 0, 'Объём'],
    [linked && !line.section, 'Раздел стройки'], [!!item?.variants && !line.variant, 'Вариант'],
    [!!item?.place && !line.place.trim(), 'Место'], [!!item?.material && !line.material.trim(), 'Материалы'],
  ] as const;
  return required.filter(([missing]) => missing).map(([, label]) => label);
}
