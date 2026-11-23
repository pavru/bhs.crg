import type { AssignmentCandidate } from '@/shared/api/work';
import { newLocalId } from '@/shared/utils/localId';
import cableTray from '../work21/cable-tray.webp';

export const variants = [
  { id: 'a', title: 'А · Реестр и сетка' },
  { id: 'b', title: 'Б · Карточки на одной странице' },
  { id: 'c', title: 'В · Ввод по шагам' },
] as const;
export type Variant = (typeof variants)[number]['id'];

export const scenarios = [
  { id: 'installer', label: 'Монтажник · обычная смена' },
  { id: 'night', label: 'Ночная смена после полуночи' },
  { id: 'manager', label: 'Ввод за сотрудника' },
  { id: 'unassigned', label: 'Нет назначений' },
  { id: 'duplicate', label: 'Отчёт на эту смену уже есть' },
  { id: 'stale', label: 'Отчёт изменён в другой вкладке' },
  { id: 'required', label: 'Подача · не хватает материалов' },
  { id: 'photos', label: 'Фото · загрузка и отказ' },
  { id: 'closed', label: 'Закрытый период · досылка фото' },
] as const;
export type Scenario = (typeof scenarios)[number]['id'];

export const sites = [
  { id: 'terminal', name: 'Терминал Восточный', today: '2026-11-23',
    suggested: { date: '2026-11-23', shift: 'day', label: 'Новый отчёт' } },
  { id: 'substation', name: 'Подстанция Северная', today: '2026-11-22',
    suggested: { date: '2026-11-22', shift: 'day', label: 'Новый отчёт' } },
] as const;

export const nightSuggestion = { date: '2026-11-22', shift: 'night', label: 'Ночная смена 22.11' } as const;
export const assignmentBoundary = { text: 'ваши стройки (2)', reason: null };
export const noAssignments = { text: 'ваши стройки (0)', reason: 'not-assigned' };

export const candidates: AssignmentCandidate[] = [
  { employeeId: 'petrov', name: 'Артём Петров', personnelNumber: '031', eligible: true, reason: null, assigned: null,
    account: { state: 'none' } },
  { employeeId: 'volkov', name: 'Денис Волков', personnelNumber: '052', eligible: true, reason: null, assigned: null,
    account: { state: 'linked' } },
];
export const crew = ['Денис Волков', 'Илья Сафонов', 'Михаил Ершов'];
export const workTypes = [
  { id: 'tray', name: 'Монтаж кабельного лотка', variants: ['Лоток 100×50', 'Лоток 200×50'], unit: 'м',
    requiresLocation: true, requiresMaterials: true },
  { id: 'cable', name: 'Прокладка кабеля', variants: ['ВВГнг 3×2,5', 'ВВГнг 5×6'], unit: 'м',
    requiresLocation: true, requiresMaterials: true },
  { id: 'mark', name: 'Маркировка линий', variants: [], unit: 'шт.',
    requiresLocation: false, requiresMaterials: false },
];
export const materials = ['Лоток перфорированный 100×50', 'Кабель ВВГнг 3×2,5', 'Кронштейн для лотка'];

export interface Photo {
  id: string;
  name: string;
  state: 'stored' | 'pending' | 'error';
  src?: string;
  error?: string;
}
export const examplePhoto = { id: 'photo-tray', name: 'Лоток на отметке +4,200', state: 'stored', src: cableTray } as const;
export const photoLimits = { count: 10, size: 15 };
export const photoRefusal = 'Фото больше 15 МБ.';
export const submissionRefusal = { line: 2, field: 'materials', message: 'Укажите материалы для исполнительной документации.' } as const;

export interface WorkLine {
  id: string;
  type: string;
  option: string;
  volume: string;
  unit: string;
  section: string;
  place: string;
  material: string;
  note: string;
  isCrew: boolean;
  participants: string[];
  photos: Photo[];
}

export const blankLine = (): WorkLine => ({
  id: newLocalId(), type: '', option: '', volume: '', unit: '', section: '', place: '',
  material: '', note: '', isCrew: false, participants: [], photos: [],
});

const baseLine: WorkLine = {
  id: 'tray-line', type: 'tray', option: 'Лоток 100×50', volume: '24', unit: 'м',
  section: 'Корпус 1', place: 'Этаж 2, оси Б–Г', material: materials[0], note: 'Отметка +4,200',
  isCrew: true, participants: [crew[0]], photos: [examplePhoto, { ...examplePhoto, id: 'photo-fixing', name: 'Крепление лотка' }],
};
const exampleLines: WorkLine[] = [
  baseLine,
  { ...baseLine, id: 'cable-line', type: 'cable', option: 'ВВГнг 3×2,5', volume: '42', material: '',
    note: '', isCrew: false, participants: [], photos: [] },
];

export type Shift = 'day' | 'night';
export type Status = 'draft' | 'returned' | 'submitted' | 'accepted';
export const statuses: Record<Status, string> = {
  draft: 'Черновик', returned: 'Возвращён', submitted: 'Подан', accepted: 'Принят',
};
export const shiftText = { day: 'день', night: '☾ ночь' };

export interface Report {
  id: string;
  site: string;
  date: string;
  shift: Shift;
  status: Status;
  hours: string;
  note?: string;
  employee?: string;
  lines: WorkLine[];
}
export const draft: Report = {
  id: 'report-23', site: 'terminal', date: '2026-11-23', shift: 'day', status: 'draft', hours: '8',
  lines: exampleLines, note: 'Подастся 23.11 в 23:59 по времени стройки',
};
export const sampleReports: Report[] = [
  draft,
  { ...draft, id: 'report-21', site: 'substation', date: '2026-11-21', shift: 'night', status: 'returned',
    note: 'Уточните расположение кабельной трассы на втором этаже.' },
  { ...draft, id: 'report-20', date: '2026-11-20', status: 'submitted', note: undefined, lines: [baseLine] },
  { ...draft, id: 'report-19', date: '2026-11-19', status: 'accepted', note: undefined, lines: [baseLine] },
];

export function reportFor(scenario: Scenario) {
  if (scenario === 'closed') return { ...sampleReports[3], lines: [{ ...baseLine, photos: [examplePhoto,
    { id: 'declared', name: 'Крепление лотка', state: 'pending' as const, src: cableTray }] }] };
  if (scenario === 'photos') return { ...draft, lines: [{ ...baseLine, photos: [examplePhoto,
    { id: 'upload', name: 'Трасса у щита', state: 'pending' as const, src: cableTray },
    { id: 'error', name: 'Общий план', state: 'error' as const, error: photoRefusal }] }] };
  return draft;
}

export function siteName(id: string) {
  return sites.find(site => site.id === id)?.name ?? '';
}

export function dateText(date: string) {
  return date.split('-').reverse().join('.');
}
