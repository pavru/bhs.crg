import type { InvoiceView } from '@/shared/api/invoices';
import { formatDate, formatMoney } from '@/shared/format/format';
import { K, PARSED, REJECTED, asInput } from './invoiceFields';
import { toNumber } from './invoiceLines';
import type { ReadinessItem } from './invoiceReadiness';

/**
 * Правила сводки счёта (costs-invoice-screen-10-layout, D-10.6, ТЗ COST-6.2): какое основное действие
 * стоит в ней и почему оно неактивно. Отдельно от разметки — это и проверяют тесты.
 */

/**
 * Можно ли отметить оплату: есть право, счёт не оплачен и не отклонён. Одно условие на оба входа — кнопку
 * части «Сроки и оплата» и основное действие сводки (ревью PR #1786).
 */
export function mayMarkPaid(view: InvoiceView, canPay: boolean): boolean {
  return canPay && !view.payment.paid && view.requisites[K.state] !== REJECTED;
}

/**
 * Основное действие по состоянию и праву — одно на экран, filled:
 * - черновик — «Разобран» (право вводить счета);
 * - разобранный неоплаченный — «Отметить оплату…» (право оплаты);
 * - исправление закрытого периода — «Проверить исправление…»;
 * - без права на действие, у отклонённого, оплаченного разобранного и удалённого — ничего.
 */
export type PrimaryAction = 'parsed' | 'pay' | 'check' | null;

export function primaryOf(
  view: InvoiceView,
  can: { edit: boolean; pay: boolean },
  mode: { correcting: boolean; gone: boolean },
): PrimaryAction {
  if (mode.gone) return null;
  if (mode.correcting) return 'check';
  const state = view.requisites[K.state];
  if (state === REJECTED) return null;
  if (state === PARSED) return mayMarkPaid(view, can.pay) ? 'pay' : null;
  // Запертый черновик «Разобран» не пройдёт: замок сильнее любого перечня.
  if (view.payment.lockedBy !== null) return null;
  return can.edit ? 'parsed' : null;
}

/** Сколько пунктов готовности назвать в подсказке — остальные числом. */
const SHOWN_GAPS = 3;

/**
 * Почему «Разобран» неактивна; `null` — активна. Перечень — СЕРВЕРА и о сохранённом (#1291): пока есть
 * несохранённое или запись в пути, причина — они, а не перечень, который сейчас неправда (ТЗ COST-6.4).
 */
export function parsedHold(readiness: readonly ReadinessItem[] | null, unsaved: boolean, saving = false): string | null {
  if (saving) return 'Счёт сохраняется — «Разобран» станет доступна после записи';
  if (unsaved) return 'Правки не сохранены: «Разобран» утверждает сохранённый счёт';
  if (!readiness || readiness.length === 0) return null;
  const named = readiness.slice(0, SHOWN_GAPS).map(item => item.label).join(' · ');
  const rest = readiness.length - SHOWN_GAPS;
  return `До «Разобран» осталось: ${named}${rest > 0 ? ` и ещё ${rest}` : ''}`;
}

/**
 * Почему «Отметить оплату…» неактивна; `null` — активна (ТЗ COST-6.4, D-11.2, 47–48): несохранённое либо
 * отказ сервера его словами. Отказ стоит и пунктом перечня — с пометкой «мешает: оплата».
 */
export function payHold(unsaved: boolean, refusal: string | null = null, saving = false): string | null {
  if (saving) return 'Счёт сохраняется — оплату можно отметить после записи';
  if (unsaved) return 'Правки не сохранены: оплата считается по сохранённому счёту';
  return refusal ? `Оплатить нельзя: ${refusal}` : null;
}

/** Вторая строка сводки — поставщик · дата · сумма к оплате, по СОХРАНЁННОМУ счёту. */
export function summaryMeta(view: InvoiceView, supplierName: string | null): string {
  const date = asInput(view.requisites[K.date]).slice(0, 10);
  const raw = view.requisites[K.total];
  const total = typeof raw === 'number' ? raw : toNumber(asInput(raw));
  return [
    supplierName ?? 'поставщик не выбран',
    date ? `от ${formatDate(date)}` : null,
    total !== null ? formatMoney(total) : null,
  ].filter(Boolean).join(' · ');
}
