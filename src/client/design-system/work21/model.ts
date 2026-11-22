import { Edit, Group, ImageIcon, Person, Schedule } from '@/shared/ui/icons';
import type { StatusTone } from '@/shared/ui/StatusText';

export const variants = [
  { id: 'a', name: 'А · Очередь и карточка', summary: 'Вся ширина для отчётов. Карточка открывается отдельно, фильтры сохраняются. Сверка бригад — соседняя вкладка приёмки.' },
  { id: 'b', name: 'Б · Список и карточка рядом', summary: 'Компактный список остаётся слева. Справа меняются отчёт и журнал. Сверка открывается в контексте выбранного отчёта.' },
  { id: 'c', name: 'В · По стройкам', summary: 'Очередь сгруппирована по стройкам. Сверка бригад открывается из заголовка стройки. Карточка отчёта открывается отдельной страницей.' },
] as const;

export const scenarios = [
  { id: 'review', label: 'Менеджер · ждут решения' },
  { id: 'reader', label: 'Руководитель · чтение' },
  { id: 'history', label: 'Принятый · есть версии' },
  { id: 'draft', label: 'Черновик · ждёт автоподачи' },
  { id: 'stale', label: 'Конфликт · новая версия' },
  { id: 'decided', label: 'Конфликт · уже принят' },
  { id: 'not-assigned', label: 'Пусто · нет назначений' },
  { id: 'nothing-pending', label: 'Пусто · нет поданных' },
  { id: 'not-found', label: 'Отчёт недоступен · 404' },
] as const;

export type Variant = (typeof variants)[number]['id'];
export type Scenario = (typeof scenarios)[number]['id'];

type MarkStyle = { label: string; tone: StatusTone; icon: typeof Edit };
export const marks = {
  'edited-after-submit': { label: 'правка после подачи', tone: 'warning', icon: Edit },
  'same-person': { label: 'ввёл и принял один человек', tone: 'warning', icon: Person },
  'manager-edit': { label: 'правка менеджером', tone: 'warning', icon: Edit },
  'photos-pending': { label: 'фото не загружены', tone: 'neutral', icon: ImageIcon },
  'crew-mismatch': { label: 'расхождение в бригаде', tone: 'warning', icon: Group },
  'late-delivery': { label: 'поздняя доставка', tone: 'neutral', icon: Schedule },
} satisfies Record<string, MarkStyle>;

export type MarkCode = keyof typeof marks;
export const sites = [
  { id: 'terminal', name: 'Терминал Восточный' },
  { id: 'substation', name: 'ПС Береговая' },
] as const;
export type SiteId = (typeof sites)[number]['id'];

export interface Report {
  id: string;
  employee: string;
  site: SiteId;
  date: string;
  shift: 'day' | 'night';
  hours: number;
  lines: number;
  marks: Array<{ code: MarkCode; text: string }>;
  actions: Array<'accept' | 'return' | 'edit'>;
  acceptWouldMark: boolean;
  enteredBy?: string;
  photosPending: number;
  status: 'submitted' | 'accepted' | 'returned';
  comment?: string;
  workLines?: Line[];
}

export interface Filters {
  site: string;
  employee: string;
  from: string;
  to: string;
  mark: MarkCode | '';
}

export const emptyFilters: Filters = { site: '', employee: '', from: '', to: '', mark: '' };

export function matchesScope(report: Report, { site, employee, from, to }: Filters) {
  return (!site || report.site === site) && (!employee || report.id === employee)
    && (!from || report.date >= from) && (!to || report.date <= to);
}

const mark = (code: MarkCode, text = marks[code].label) => ({ code, text });
const actions: Report['actions'] = ['accept', 'return', 'edit'];
export const reports: Report[] = [
  { id: '1', employee: 'Максим Крылов', site: 'terminal', date: '2026-11-21', shift: 'night', hours: 10, lines: 3,
    marks: [mark('same-person'), mark('photos-pending', 'фото не загружены: 2'), mark('crew-mismatch')], actions,
    acceptWouldMark: true, enteredBy: 'Елена Соколова', photosPending: 2, status: 'submitted' },
  { id: '2', employee: 'Андрей Нестеров', site: 'terminal', date: '2026-11-21', shift: 'night', hours: 10, lines: 3,
    marks: [mark('crew-mismatch')], actions, acceptWouldMark: false, photosPending: 0, status: 'submitted' },
  { id: '3', employee: 'Ирина Сафонова', site: 'substation', date: '2026-11-22', shift: 'day', hours: 8, lines: 2,
    marks: [mark('edited-after-submit'), mark('photos-pending', 'фото не загружены: 1')], actions,
    acceptWouldMark: false, photosPending: 1, status: 'submitted' },
  { id: '4', employee: 'Олег Туманов', site: 'substation', date: '2026-11-22', shift: 'day', hours: 8, lines: 2,
    marks: [mark('late-delivery')], actions, acceptWouldMark: false, photosPending: 0, status: 'submitted' },
  { id: '5', employee: 'Лев Антонов', site: 'terminal', date: '2026-11-22', shift: 'day', hours: 8, lines: 1,
    marks: [], actions, acceptWouldMark: false, photosPending: 0, status: 'submitted' },
];

export const siteName = (site: SiteId) => sites.find(item => item.id === site)?.name ?? site;
export const briefDate = (date: string) => `${date.slice(8, 10)}.${date.slice(5, 7)}.${date.slice(0, 4)}`;
export const shiftName = { day: 'день', night: '☾ ночь' };

export interface Line {
  id: string;
  name: string;
  place: string;
  volume: string;
  unit: string;
  photos: number;
  pending: number;
}

export function sampleLines(report: Report): Line[] {
  return [
    { id: 'cable', name: 'Прокладка кабеля ВВГнг', place: 'Трасса ЭОМ · отметка +4,200', volume: '48', unit: 'м', photos: 1, pending: report.photosPending },
    { id: 'tray', name: 'Монтаж кабельного лотка', place: 'Проходная галерея', volume: '12', unit: 'м', photos: 0, pending: 0 },
    { id: 'boxes', name: 'Установка коробок', place: 'Шкафная · ось 4', volume: '6', unit: 'шт.', photos: 0, pending: 0 },
  ].slice(0, report.lines);
}
