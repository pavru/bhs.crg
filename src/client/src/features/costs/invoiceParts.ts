import { createContext } from 'react';
import type { InvoiceView } from '@/shared/api/invoices';
import { formatDate, formatMoney } from '@/shared/format/format';
import { shippedBeforeIssued } from './invoiceDue';
import { BLOCKS, K, PARSED, REJECTED, asInput, isMarked, refEntryId } from './invoiceFields';
import { toNumber } from './invoiceLines';
import type { ReadinessTarget } from './invoiceReadiness';

/**
 * Части формы счёта и правило «заполнена» (costs-invoice-screen-10-layout, D-10.2, ТЗ COST-6.2).
 *
 * <p>Форма — одна прокрутка частей в порядке работы: сначала кто и сколько, потом строки, разноска, сроки.
 * Незаполненная часть открывается раскрытой, заполненная — свёрнутой в строку-итог. Решение принимается
 * ОДИН раз, при открытии счёта: запись не схлопывает часть у человека под руками.</p>
 *
 * <p>«Сроки и оплата» заполнена, когда в ней нечего решать: нет метки, ошибки значения, пункта готовности
 * о её поле и замечания «Отгрузка раньше даты счёта». Неопределённый срок её не раскрывает — строка-итог
 * говорит «срок не определён» (costs-invoice-terms-collapse, D1).</p>
 *
 * <p>⚠️ Свёрнутая часть не прячет того, что требует решения: метка «со скана, не проверено» и ошибка
 * значения держат часть раскрытой, как бы полно она ни была заполнена. Метка, пришедшая ПОСЛЕ открытия
 * (распознавание кончилось при открытой форме), часть раскрывает (`attentionOf`, ревью PR #1786):
 * сворачивает по-прежнему только человек.</p>
 */

export type PartId = 'who' | 'lines' | 'allocation' | 'terms' | 'more' | 'history';

/** Порядок частей — порядок работы (D1 зонтика). */
export const PART_ORDER: readonly PartId[] = ['who', 'lines', 'allocation', 'terms', 'more', 'history'];

/** Чем часть названа в шапке; у первых четырёх — номер шага. */
export const PART_TITLE: Record<PartId, { title: string; step?: number }> = {
  who: { title: 'Кто и сколько', step: 1 },
  lines: { title: 'Строки', step: 2 },
  allocation: { title: 'Разноска', step: 3 },
  terms: { title: 'Сроки и оплата', step: 4 },
  more: { title: 'Ещё' },
  history: { title: 'История' },
};

/** Несохранённое формы — то, что лежит поверх вида счёта. */
export interface PartDraft {
  /** Правки реквизитов. */
  edits: Readonly<Record<string, unknown>>;
  /** Строки правлены и не сохранены. */
  linesDirty: boolean;
}

/**
 * Что о форме знает только она: поля, дописанные в тип «Счёт» (их рисует «Ещё», issue #1482), и то, что
 * «Кто и сколько» показывает сверх своих полей, — замечания распознавания. Метки у полей, которых форма не
 * показывает, — пункт перечня состояния (11, 46), а не строка части.
 */
export interface PartExtra {
  /** Ключи полей, дописанных в тип «Счёт». */
  extra: readonly string[];
  /** В конце «Кто и сколько» стоит то, что требует решения, — замечания распознавания. */
  attention: boolean;
}

const NO_EXTRA: PartExtra = { extra: [], attention: false };

/** Обязательное «Кто и сколько»: без них первый этап не пройден. НДС — нет: его на бумаге бывает ноль. */
const WHO_REQUIRED = [K.supplier, K.payer, K.number, K.date, K.total] as const;

const fieldsOf = (id: string) => BLOCKS.find(b => b.id === id)?.fields ?? [];

const valueOf = (view: InvoiceView, draft: PartDraft, key: string): unknown =>
  (key in draft.edits ? draft.edits[key] : view.requisites[key]);

/** Значение задано. Сумма ноль — не задана: «Разобран» и оплата считают её так же (D9 зонтика). */
function given(key: string, value: unknown): boolean {
  if (value == null) return false;
  if (key === K.total) {
    const sum = typeof value === 'number' ? value : toNumber(String(value));
    return sum !== null && sum !== 0;
  }
  if (key === K.supplier || key === K.payer) return refEntryId(value) !== null;
  return asInput(value).trim() !== '';
}

/** Есть ли в части то, что требует решения: метка со скана или пункт готовности о поле части. */
function needsDecision(view: InvoiceView, draft: PartDraft, fields: readonly string[]): boolean {
  if (fields.some(key => isMarked(key, view.unconfirmed, draft.edits))) return true;
  if (view.readiness?.gaps?.some(gap => gap.field != null && fields.includes(gap.field) && !(gap.field in draft.edits)))
    return true;
  // Запись справочника, на которую ссылается сторона, удалена — ошибка значения, а не «заполнено».
  return (fields.includes(K.supplier) && view.references?.supplier === 'lost' && !(K.supplier in draft.edits))
    || (fields.includes(K.payer) && view.references?.payer === 'lost' && !(K.payer in draft.edits));
}

/**
 * Что требует решения в «Сроках и оплате»: метка или пункт готовности о её поле — и отгрузка раньше даты
 * счёта (пункт 32 перечня, `invoiceStatus`): чаще опечатка, и срок от неё уезжает. Пока срок что-то значит —
 * у оплаченного и отклонённого замечания нет и в перечне (ревью PR #1855).
 */
function termsNeedDecision(view: InvoiceView, draft: PartDraft): boolean {
  if (needsDecision(view, draft, fieldsOf('terms'))) return true;
  return !view.payment.paid && view.requisites[K.state] !== REJECTED
    && shippedBeforeIssued(valueOf(view, draft, K.shippedOn), valueOf(view, draft, K.date));
}

/**
 * Где сейчас стоит то, что требует решения: метка, пункт готовности о поле, удалённая запись. Части
 * без своих полей (строки, разноска, история) — `false`: их раскрытость решает заполненность.
 */
export function attentionOf(view: InvoiceView, draft: PartDraft, more: PartExtra = NO_EXTRA): Record<PartId, boolean> {
  return {
    who: needsDecision(view, draft, fieldsOf('who')) || more.attention,
    lines: false,
    allocation: false,
    terms: termsNeedDecision(view, draft),
    more: needsDecision(view, draft, [...fieldsOf('more'), ...more.extra]),
    history: false,
  };
}

/** Заполнена ли часть — таблица D-10.2. Чистая: её и проверяют тесты. */
export function partFilled(part: PartId, view: InvoiceView, draft: PartDraft, more: PartExtra = NO_EXTRA): boolean {
  switch (part) {
    case 'who':
      return WHO_REQUIRED.every(key => given(key, valueOf(view, draft, key)))
        && !attentionOf(view, draft, more).who;
    case 'lines':
      return view.lines.length > 0 && view.lines.every(line => line.nomenclatureId !== null) && !draft.linesDirty;
    case 'allocation':
      return view.allocation.allocated;
    // «Сроки и оплата» — только то, что требует решения (costs-invoice-terms-collapse, D1, COST-6.2): срок,
    // неопределённый без даты отгрузки, часть не раскрывает — дату отгрузки почти не вносят, и часть
    // открывалась бы почти у каждого счёта (#1818, п. 7). Пустой срок называет строка-итог.
    case 'terms':
      return !termsNeedDecision(view, draft);
    // «Ещё» и «История» открываются свёрнутыми — кроме «Ещё» с меткой со скана или пунктом готовности, и у
    // полей, дописанных в тип, тоже: метку прятать нельзя.
    case 'more':
      return !attentionOf(view, draft, more).more;
    case 'history':
      return true;
  }
}

/** Какие части раскрыты при открытии счёта: незаполненные. */
export function initiallyOpen(view: InvoiceView, draft: PartDraft, more: PartExtra = NO_EXTRA): Record<PartId, boolean> {
  return Object.fromEntries(PART_ORDER.map(part => [part, !partFilled(part, view, draft, more)])) as Record<PartId, boolean>;
}

/**
 * Часть, которой принадлежит поле шапки: к ней ведёт строка готовности. Поле, дописанное в тип «Счёт», —
 * в «Ещё» (`extra`): без этого пункт о нём не раскрыл бы часть, и щелчок по нему ничего бы не делал.
 */
export function partOfField(key: string, extra: readonly string[] = []): PartId | null {
  const block = BLOCKS.find(b => b.fields.includes(key));
  return block ? (block.id as PartId) : extra.includes(key) ? 'more' : null;
}

/** Часть, в которой стоит место строки готовности. */
export function partOfTarget(target: ReadinessTarget, extra: readonly string[] = []): PartId | null {
  return target.place === 'field' ? partOfField(target.key, extra)
    : target.place === 'part' ? target.part
      : target.place === 'lines' ? 'lines' : 'allocation';
}

/**
 * Привести к месту формы, раскрыв его часть: строка готовности ведёт и в свёрнутую часть. Без формы
 * (`null`) — просто к месту.
 */
export const PartRevealContext = createContext<((target: ReadinessTarget) => void) | null>(null);

const day = (value: unknown) => {
  const text = asInput(value).slice(0, 10);
  return text ? formatDate(text) : '';
};

const money = (value: unknown) => {
  const sum = typeof value === 'number' ? value : toNumber(asInput(value));
  return sum === null ? '' : formatMoney(sum);
};

/**
 * Строка-итог свёрнутой части: главное из неё одной строкой. Пустое — не называется, а не пишется
 * «—»: строка-итог читается фразой.
 *
 * @param nameOf название стороны счёта по ключу поля («Поставщик», «Плательщик»).
 */
export function partLine(part: PartId, view: InvoiceView, draft: PartDraft, nameOf: (key: string) => string | null): string {
  const value = (key: string) => valueOf(view, draft, key);
  switch (part) {
    case 'who': {
      const sides = [nameOf(K.supplier) ?? 'поставщик не выбран', nameOf(K.payer) ?? 'плательщик не выбран'].join(' → ');
      const number = asInput(value(K.number));
      const date = day(value(K.date));
      const paper = number ? `№ ${number}${date ? ` от ${date}` : ''}` : date ? `от ${date}` : '';
      const total = money(value(K.total));
      return [sides, paper, total && `к оплате ${total}`].filter(Boolean).join(' · ');
    }
    case 'lines': {
      if (view.lines.length === 0) return 'строк нет';
      const waiting = view.lines.filter(line => line.nomenclatureId === null).length;
      return [`строк: ${view.lines.length}`, waiting > 0 && `ждут позиции: ${waiting}`].filter(Boolean).join(' · ');
    }
    case 'allocation':
      if (view.requisites[K.state] === REJECTED) return 'отклонён — в затраты не входит';
      return view.allocation.allocated ? 'разноска сходится' : view.lines.length === 0 ? 'после строк'
        : view.requisites[K.state] === PARSED ? 'разобран до разноски — деньги ни к одной стройке не отнесены' : 'не разнесён';
    case 'terms': {
      const due = day(value(K.dueDate));
      const paid = view.payment.paid
        ? `оплачен${view.payment.paidOn ? ` ${formatDate(view.payment.paidOn)}` : ''}`
        : 'не оплачен';
      return [due ? `оплатить до ${due}` : 'срок не определён', paid].join(' · ');
    }
    case 'more': {
      const said = [asInput(value(K.basis)), asInput(value(K.purpose))].filter(Boolean).join(' · ');
      return said || 'основание, назначение, дополнительные поля';
    }
    case 'history':
      return 'исправления закрытого периода';
  }
}
