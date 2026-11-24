export interface Work {
  id: string;
  name: string;
  section: string;
  volume: string;
  unit: string;
  start: string;
  end: string;
  duration: number | null;
  employeeIds: string[];
  warnings: Warning[];
}

export type Warning = 'dependency' | 'day-off' | 'no-dates' | 'calendar';
export type Variant = 'a' | 'b' | 'c';
export const variants = [
  { value: 'a', label: 'А · Рабочая таблица' },
  { value: 'b', label: 'Б · Список и карточка' },
  { value: 'c', label: 'В · По разделам' },
] as const;
export const durationText = (work: Work) => work.duration === null ? '—' : `${work.duration} раб. дн.`;
export const warningLabels: Record<Warning, string> = {
  dependency: 'Нарушение зависимости',
  'day-off': 'Окончание на выходном',
  'no-dates': 'Не назначены сроки',
  calendar: 'Календарь компании на 2027 год не заполнен',
};
export const wholeSite = 'По стройке в целом';
export const sections = ['Корпус 1', 'Корпус 2', wholeSite];
export const employees = [
  { employeeId: 'orlov', name: 'Иван Орлов', personnelNumber: '0124', eligible: true, reason: null },
  { employeeId: 'kim', name: 'Алексей Ким', personnelNumber: '0171', eligible: true, reason: null },
  { employeeId: 'volkova', name: 'Елена Волкова', personnelNumber: '0212', eligible: true, reason: null },
  { employeeId: 'belov', name: 'Сергей Белов', personnelNumber: '0040', eligible: false, reason: 'Запись в архиве' },
];
export const employeeNames = (work: Work) => work.employeeIds
  .map(id => employees.find(employee => employee.employeeId === id)?.name).filter(Boolean).join(', ');

export const works: Work[] = [
  { id: 'trays', name: 'Монтаж кабельных лотков', section: 'Корпус 1', volume: '700', unit: 'м',
    start: '2026-11-24', end: '2026-11-30', duration: 5, employeeIds: ['orlov'], warnings: ['dependency'] },
  { id: 'cable', name: 'Прокладка силового кабеля', section: 'Корпус 1', volume: '420', unit: 'м',
    start: '2026-11-27', end: '2026-12-05', duration: 6, employeeIds: ['kim', 'volkova'], warnings: ['dependency', 'day-off'] },
  { id: 'panels', name: 'Установка щитов', section: 'Корпус 2', volume: '8', unit: 'шт.',
    start: '2026-12-02', end: '2026-12-07', duration: 4, employeeIds: ['volkova'], warnings: [] },
  { id: 'ground', name: 'Монтаж заземления', section: wholeSite, volume: '200', unit: 'м',
    start: '2026-11-26', end: '2026-12-02', duration: 5, employeeIds: [], warnings: [] },
  { id: 'checks', name: 'Пусконаладочные работы', section: wholeSite, volume: '1', unit: 'компл.',
    start: '', end: '', duration: null, employeeIds: [], warnings: ['no-dates'] },
];
export const schedules = [
  { id: 'electric', name: 'Электромонтаж', source: 'Электроснабжение · редакция 3', works },
  { id: 'structure', name: 'Общестрой', source: 'Ручной график', works: [
    { id: 'base', name: 'Подготовка оснований', section: 'Корпус 2', volume: '8', unit: 'шт.',
      start: '2026-11-24', end: '2026-11-26', duration: 3, employeeIds: [], warnings: [] },
  ] as Work[] },
];
export interface WorkLink { predecessor: string; successor: string; lag: string }
export const initialLinks: WorkLink[] = [
  { predecessor: 'trays', successor: 'cable', lag: '1' },
  { predecessor: 'base', successor: 'panels', lag: '0' },
];
export const daysOff = ['2026-11-28', '2026-11-29', '2026-12-05', '2026-12-06'];
export const shortDate = (date: string) => date ? date.slice(8, 10) + '.' + date.slice(5, 7) : '—';
export const workTypes = [
  { name: 'Монтаж светильников', unit: 'шт.' },
  { name: 'Монтаж кабельных лотков', unit: 'м' },
  { name: 'Пусконаладочные работы', unit: 'компл.' },
];
