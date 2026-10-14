import type { InvoiceListItem, InvoiceView } from '@/shared/api/invoices';
import { formatDate } from '@/shared/format/format';
import { K } from './invoiceFields';

/**
 * Срок «оплатить до» в форме счёта (ТЗ COST-9.1; задача C4, issue #1080): что сказать под полем и
 * предлагать ли «По правилу». Отдельно от разметки — это и проверяют тесты.
 *
 * Сам срок подставляет СЕРВЕР, правилом из настройки модуля: форма правила не знает и не считает. Что
 * дало бы правило сейчас, сервер присылает в `view.due.byRule`.
 */
export interface DueState {
  /** Подсказка под полем; `null` — сказать нечего. */
  note: string | null;
  /** Показать «По правилу»: нажатие что-то изменит. */
  offerRule: boolean;
}

export function dueState(view: InvoiceView, edits: Record<string, unknown>, locked: boolean): DueState {
  const due = view.due;
  // Старый сервер признаков не присылает — и форма молчит, а не угадывает.
  if (!due) return { note: null, offerRule: false };

  const stored = typeof view.requisites[K.dueDate] === 'string' ? (view.requisites[K.dueDate] as string) : null;
  // Пока шапка правится, кнопки нет: «По правилу» пишет счёт, и несохранённые правки шапки оказались бы
  // собраны по прежнему виду — сохранение получило бы отказ «счёт изменили». К тому же правило считает
  // по СОХРАНЁННЫМ отгрузке и отсрочке, а не по набранным.
  const clean = Object.keys(edits).length === 0;
  // Правилу не из чего считать — кнопки нет и у ручного срока (ревью PR #1328): «вернуть» некуда, а
  // сервер такому нажатию откажет. Иначе — когда нажатие что-то изменит.
  const differs = due.byRule !== null && (due.manual || due.byRule !== stored);
  const offerRule = !locked && clean && differs;

  if (due.manual)
    return {
      note: due.byRule && due.byRule !== stored
        ? `Изменён вручную; по правилу — ${formatDate(due.byRule)}`
        : 'Изменён вручную — правило его не меняет',
      offerRule,
    };
  if (stored === null && due.byRule === null)
    return { note: 'Не определён: нужны дата отгрузки и отсрочка', offerRule };
  // Записан по прежнему правилу: смена настройки сроки не пересчитывает (решение владельца 14.10.2026).
  if (due.byRule !== null && due.byRule !== stored)
    return { note: `По действующему правилу — ${formatDate(due.byRule)}`, offerRule };
  return { note: null, offerRule };
}

/**
 * Подсказки под полем — все, а не первая (ревью PR #1328): метка «распознано, не подтверждено» и
 * предупреждение о дате говорят о разном, и одна не вправе затирать другую.
 */
export function hints(...parts: (string | null | undefined)[]): string | undefined {
  const shown = parts.filter((p): p is string => !!p);
  return shown.length > 0 ? shown.join(' · ') : undefined;
}

/**
 * Что сказать у поля «Отсрочка» об условиях поставщика (ТЗ COST-9.2; решение владельца 14.10.2026):
 * взята ли она из условий или отличается от них. `null` — у поставщика условий нет.
 *
 * Подставляет отсрочку СЕРВЕР — при сохранении, когда сменился поставщик; форма только говорит.
 * Поставщика сменили и не сохранили — молчит: условия в виде счёта принадлежат прежнему поставщику.
 */
export function deferralNote(view: InvoiceView, edits: Record<string, unknown>): string | null {
  const terms = view.due?.supplierDeferral ?? null;
  if (terms === null) return null;
  if (K.supplier in edits && JSON.stringify(edits[K.supplier]) !== JSON.stringify(view.requisites[K.supplier] ?? null))
    return null;

  const value = K.deferral in edits ? edits[K.deferral] : view.requisites[K.deferral];
  const days = typeof value === 'number' ? value
    : typeof value === 'string' && value.trim() !== '' ? Number(value)
    : null;
  return days === terms ? 'Из условий поставщика' : `В условиях поставщика — ${terms} дн.`;
}

/**
 * Отгрузка раньше даты счёта (ТЗ COST-9.1): предупреждение у поля, а не отказ — бывает и так
 * (отгрузили до выставления счёта), но чаще это опечатка в годе или месяце, и срок от неё уедет.
 */
export function shippedBeforeIssued(shipped: unknown, issued: unknown): boolean {
  return typeof shipped === 'string' && typeof issued === 'string' && shipped !== '' && issued !== ''
    && shipped.slice(0, 10) < issued.slice(0, 10);
}

/** За сколько дней до срока строка списка начинает звать платить. */
export const DUE_SOON = 3;

/**
 * Отметка срока в строке списка счетов (ТЗ COST-9.1; C4, issue #1080): «просрочен» либо сколько дней
 * осталось. Числа считает СЕРВЕР — от сегодня в поясе компании, тем же правилом, что колонки таблицы
 * счетов; строка только выбирает слова. `null` — счёт оплаты не ждёт либо срок не определён: у
 * оплаченного «осталось дней» не показывается.
 */
export function rowDue(item: Pick<InvoiceListItem, 'daysLeft' | 'overdue' | 'dueDate'>):
  { text: string; title: string; tone: 'danger' | 'warning' | 'quiet' } | null {
  const days = item.daysLeft;
  if (days === null || days === undefined) return null;
  const on = item.dueDate ? ` (${formatDate(item.dueDate)})` : '';

  if (item.overdue)
    return { text: 'просрочен', tone: 'danger', title: `Срок оплаты прошёл ${-days} дн. назад${on}` };
  if (days === 0) return { text: 'сегодня', tone: 'warning', title: `Срок оплаты сегодня${on}` };
  return {
    text: `${days} дн.`,
    tone: days <= DUE_SOON ? 'warning' : 'quiet',
    title: `Осталось дней до срока оплаты: ${days}${on}`,
  };
}
