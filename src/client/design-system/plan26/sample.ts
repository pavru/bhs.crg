export const variants = [
  { value: 'a', label: 'А · Реестр и карточка' },
  { value: 'b', label: 'Б · По стройкам' },
  { value: 'c', label: 'В · Очередь по статусам' },
] as const;
export type Variant = (typeof variants)[number]['value'];

export const scenarios = [
  { value: 'normal', label: 'Снабженец' },
  { value: 'manager', label: 'Менеджер · счета скрыты' },
  { value: 'hidden', label: 'Материалы сметы скрыты' },
  { value: 'unknown-units', label: 'В смете нет единиц' },
  { value: 'plan-off', label: 'Планирование выключено' },
  { value: 'stale', label: 'Заявка изменилась (409)' },
  { value: 'split', label: 'Счёт на две стройки (422)' },
  { value: 'no-lines', label: 'Связанный счёт без строк' },
] as const;
export type Scenario = (typeof scenarios)[number]['value'];

export const statuses = [
  { value: 'draft', label: 'Черновик' },
  { value: 'submitted', label: 'Подана' },
  { value: 'closed', label: 'Закрыта' },
] as const;
export type Status = (typeof statuses)[number]['value'];

export const sites = [
  { value: 'park', label: 'Северный логистический парк' },
  { value: 'school', label: 'Школа на проспекте Строителей' },
] as const;
export type Site = (typeof sites)[number]['value'];

export const sections = [
  { value: 'power', label: 'Ввод и ВРУ' },
  { value: 'lighting', label: 'Освещение склада' },
] as const;

export const works = [
  { value: 'feed', label: 'Прокладка питающих линий', section: 'Ввод и ВРУ' },
  { value: 'light', label: 'Монтаж линий освещения', section: 'Освещение склада' },
] as const;

export interface Material {
  id: string;
  name: string;
  unit: string;
  estimateUnit?: string;
  estimate: number;
  issued: number | null;
  quantity: number;
  invoiced: number | null;
  issue?: string;
  exceeds?: boolean;
  mismatch?: boolean;
}

const cable: Material = {
  id: 'cable', name: 'Кабель ВВГнг(А)-LS 3×2,5', unit: 'м', estimate: 100, issued: 30, quantity: 70, invoiced: 40,
};
const tray: Material = {
  id: 'tray', name: 'Лоток перфорированный 100×50', unit: 'м', estimate: 40, issued: 55,
  quantity: 0, invoiced: 0, exceeds: true,
};
const anchors: Material = {
  id: 'anchors', name: 'Анкер клиновой М8', unit: 'шт.', estimate: 24, issued: null,
  quantity: 24, invoiced: null, issue: 'В накладной количество указано в упаковках.', mismatch: true,
};

export type Scope = 'items' | 'section';
export function suggest(scope: Scope, section: string, picked: readonly string[]): Material[] {
  const estimate = scope === 'section' ? (section === 'power' ? 40 : 60)
    : (picked.includes('feed') ? 60 : 0) + (picked.includes('light') ? 40 : 0);
  const result: Material[] = [{ ...cable, estimate, quantity: Math.max(0, estimate - 30) }];
  if (scope === 'section' ? section === 'power' : picked.includes('feed')) result.push(tray);
  if (scope === 'section' ? section === 'lighting' : picked.includes('light')) result.push(anchors);
  return result.map(row => ({ ...row, estimateUnit: row.unit, invoiced: 0 }));
}

export const missingUnits: Material[] = [12, 4].map((quantity, index) => ({
  id: `pipe-${index}`, name: 'Труба ПНД Ø 25', unit: '', estimateUnit: '', estimate: quantity, issued: null,
  quantity, invoiced: 0, mismatch: true, issue: 'В строке сметы не указана единица.',
}));

export interface Request {
  number: number;
  site: Site;
  status: Status;
  date: string;
  author: string;
  availableToAuthor: boolean;
  invoices: number;
  rows: Material[];
}

export const starter: Request[] = [
  { number: 147, site: 'park', status: 'submitted',
    date: '2026-11-27', author: 'Анна Волкова', availableToAuthor: true, invoices: 2, rows: [cable, tray, anchors] },
  { number: 146, site: 'park', status: 'draft',
    date: '2026-11-26', author: 'Анна Волкова', availableToAuthor: true, invoices: 0,
    rows: suggest('section', 'power', []) },
  { number: 145, site: 'school',
    status: 'submitted', date: '2026-11-25', author: 'Илья Орлов', availableToAuthor: false, invoices: 0,
    rows: [{ ...cable, quantity: 120, issued: 0, invoiced: 0 }] },
  { number: 144, site: 'park', status: 'submitted',
    date: '2026-11-24', author: 'Анна Волкова', availableToAuthor: true, invoices: 0,
    rows: [{ ...cable, quantity: 30, invoiced: 0 }] },
  { number: 143, site: 'park', status: 'closed',
    date: '2026-11-22', author: 'Илья Орлов', availableToAuthor: false, invoices: 1,
    rows: [{ ...anchors, issued: 60, quantity: 60, invoiced: 60, mismatch: false, issue: undefined }] },
  { number: 142, site: 'school', status: 'draft',
    date: '2026-11-20', author: 'Илья Орлов', availableToAuthor: false, invoices: 0,
    rows: [{ ...cable, quantity: 110, invoiced: 0, issued: 0 }] },
];

export const siteName = (id: Site) => sites.find(site => site.value === id)!.label;
export const statusName = (id: Status) => statuses.find(status => status.value === id)!.label;

export function option<T extends string>(items: readonly { value: T }[], raw: string | null, fallback: T): T {
  return items.find(item => item.value === raw)?.value ?? fallback;
}
