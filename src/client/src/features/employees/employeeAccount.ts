import type { SchemaField } from '@/shared/api/schema';
import { FUNCTIONAL_TAG, hasTag as tagged } from '@/shared/api/tags';
import type { AccountCandidate, AccountMatch, Employee, EmployeeAccount, LinkBatchResult, LinkRowResult } from '@/shared/api/employees';

/**
 * Правила экрана «Сотрудники» без React (change `work-01-employee-account`, макет Р1-В): что писать в
 * блоке «Учётная запись», какой строке какой отбор, итог пачки. Всё — по полям ответа сервера: из
 * реквизитов карточки (поле «УчётнаяЗапись», «Уволен с») экран ничего не выводит.
 */

/** «2026-11-03» → «03.11.2026». Иное — как есть: показать лучше, чем потерять. */
export function ruDate(iso: string): string {
  const m = /^(\d{4})-(\d{2})-(\d{2})$/.exec(iso);
  return m ? `${m[3]}.${m[2]}.${m[1]}` : iso;
}

export interface AccountView {
  headline: string;
  supporting: string | null;
  /** Учётную запись удалили — блок говорит плашкой «внимание», а не тихой строкой. */
  deleted: boolean;
  /** Пометка «уволен, но связан» — дата пришла с сервера, когда она наступила. */
  dismissed: string | null;
}

/** Блок «Учётная запись» карточки — по `account.state` (B17). */
export function accountView(account: EmployeeAccount): AccountView {
  const dismissed = account.state === 'linked' && account.dismissedOn
    ? `Уволен с ${ruDate(account.dismissedOn)}, но связан: войти и подать отчёт он по-прежнему может. Отвяжите учётную запись, если он больше не работает.`
    : null;
  switch (account.state) {
    case 'linked':
      return { headline: `Связан: ${account.name ?? ''}`, supporting: account.login || null, deleted: false, dismissed };
    case 'deleted':
      return {
        headline: `Учётная запись удалена: ${account.name ?? ''}`,
        supporting: 'Отчёты остаются за сотрудником. Отвяжите или свяжите с новой учётной записью.',
        deleted: true, dismissed: null,
      };
    default:
      return { headline: 'Не связан', supporting: 'Сотрудник не может входить и подавать отчёты', deleted: false, dismissed: null };
  }
}

/**
 * Скрытый текст строки списка: состояние словами для чтеца — знак в строке тихий и один его не
 * передаёт (B16). У «не связан» слов нет: это обычное положение, и каждая вторая строка не обязана
 * о нём говорить.
 */
export function rowStateText(account: EmployeeAccount): string | null {
  if (account.state === 'linked') return `связан с ${account.name ?? ''}`;
  if (account.state === 'deleted') return 'учётная запись удалена';
  return null;
}

/** Отборы над списком. «Все» — без архива: архивных по умолчанию нет, у них свой отбор (B19). */
export type EmployeeFilter = 'all' | 'unlinked' | 'deleted' | 'archived';

export const FILTERS: { key: EmployeeFilter; label: string }[] = [
  { key: 'all', label: 'Все' },
  { key: 'unlinked', label: 'Без учётной записи' },
  { key: 'deleted', label: 'Учётная запись удалена' },
  { key: 'archived', label: 'Архив' },
];

export function inFilter(e: Employee, filter: EmployeeFilter): boolean {
  if (filter === 'archived') return e.archived;
  if (e.archived) return false;
  if (filter === 'unlinked') return e.account.state === 'none';
  if (filter === 'deleted') return e.account.state === 'deleted';
  return true;
}

export function filterCounts(list: readonly Employee[]): Record<EmployeeFilter, number> {
  const counts: Record<EmployeeFilter, number> = { all: 0, unlinked: 0, deleted: 0, archived: 0 };
  for (const e of list) for (const f of FILTERS) if (inFilter(e, f.key)) counts[f.key]++;
  return counts;
}

/** Совпадает ли сотрудник с поиском: имя и псевдонимы. */
export function matchesQuery(e: Employee, q: string): boolean {
  const needle = q.trim().toLocaleLowerCase('ru');
  if (!needle) return true;
  return [e.displayName, ...e.aliases].some(s => s.toLocaleLowerCase('ru').includes(needle));
}

/**
 * Можно ли выбрать кандидата: свободный — да; занятый этим же сотрудником — да (повтор безобиден);
 * занятый другим — нет, и подпись называет кем (B18).
 */
export function candidateBlocked(c: AccountCandidate, employeeId: string): string | null {
  if (!c.linkedTo || c.linkedTo.id === employeeId) return null;
  return `уже связана с ${c.linkedTo.name}`;
}

/** Фамилия — первое слово имени: ею заполнен поиск окна связывания. */
export function surnameOf(name: string): string {
  return name.trim().split(/\s+/)[0] ?? '';
}

/** Причина без предложения — словами строки сопоставления. */
export function matchReasonText(reason: AccountMatch['reason']): string | null {
  if (reason === 'ambiguous') return 'несколько совпадений — выберите';
  if (reason === 'no-match') return 'нет совпадения';
  return null;
}

/** Итог пачки одной строкой: «Связано 9 из 11». */
export function batchSummary(result: LinkBatchResult): string {
  return `Связано ${result.linked} из ${result.total}`;
}

/** Итог строки — словами сервера: у отказа причину называет он, экран её не придумывает. */
export function rowOutcomeText(r: LinkRowResult): string {
  return r.outcome === 'linked' ? 'Связан' : `Не связан: ${r.error ?? r.outcome}`;
}

/** Строки, которые стоит повторить: версия устарела — перечитать и отправить заново. */
export function retryable(r: LinkRowResult): boolean {
  return r.outcome === 'stale';
}

/** Простые поля — их карточка показывает строкой «название — значение»; составные правятся формой. */
const PLAIN: ReadonlySet<SchemaField['type']> = new Set(['string', 'text', 'number', 'date', 'boolean', 'enum', 'primitive']);

const hasTag = (f: SchemaField, tag: string) => tagged(f.tags, tag);

/**
 * Реквизиты карточки для просмотра — простые поля по порядку схемы. Поле связи («УчётнаяЗапись», по тэгу
 * `employee.account`, а не по ключу — ключ заказчик вправе переименовать) не показывается: оно заперто и
 * связью не заполняется, а состояние связи говорит блок «Учётная запись» (B16).
 */
export function requisiteRows(fields: readonly SchemaField[], data: Record<string, unknown>): { key: string; title: string; text: string }[] {
  return fields
    .filter(f => PLAIN.has(f.type) && !hasTag(f, FUNCTIONAL_TAG.employeeAccount))
    .map(f => ({ key: f.key, title: f.title || f.key, text: valueText(f, data[f.key]) }));
}

function valueText(f: SchemaField, v: unknown): string {
  if (v === null || v === undefined || v === '') return '—';
  if (f.type === 'boolean') return v === true ? 'Да' : 'Нет';
  if (f.type === 'date' && typeof v === 'string') return ruDate(v.slice(0, 10));
  if (typeof v === 'object') return '—';
  return String(v);
}

/** Табельный номер — по тэгу: вторая строка списка («Таб. № 0117»), по нему сотрудника и узнают. */
export function personnelNumber(fields: readonly SchemaField[], data: Record<string, unknown>): string | null {
  const f = fields.find(x => hasTag(x, FUNCTIONAL_TAG.employeePersonnelNumber));
  const v = f ? data[f.key] : undefined;
  return typeof v === 'string' && v.trim() ? v.trim() : typeof v === 'number' ? String(v) : null;
}
