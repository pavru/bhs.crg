import type { InvoiceView, VatMode } from '@/shared/api/invoices';
import { lines, mismatch, money, toNumber, vatCaption, type LineDraft, type LineTotals } from './invoiceLines';

/**
 * Режим НДС счёта в форме (эпик #1496, issue #1499, #1500) — всё, что не рисование: смена режима в
 * черновике, полоса сверки порядком бумаги и что смена сделает с разноской.
 *
 * <p>⚠️ <b>Режим меняет только человек.</b> Подсказка «похоже, цены без НДС» ставит флажок в черновике —
 * и только по нажатию: совпадение расхождения с НДС бывает случайным, а смена режима меняет деньги на
 * стройках. Догадку даёт сервер (`VatModeGuess`), форма её не повторяет.</p>
 */

/** Режим сохранённого счёта. Старый сервер поля не присылает — так жили все счета до #1497. */
export const vatModeOf = (view: Pick<InvoiceView, 'vatMode'>): VatMode => view.vatMode ?? 'Included';

/**
 * Черновик строк в другом режиме. Бумажная сумма остаётся: на бумаге то же число, меняется его смысл.
 * НДС строк со ставкой пересчитывается — его посчитал прежний режим (от суммы с НДС), и в новом он был
 * бы неверен; у строки без ставки сумма НДС — из бумаги, и она остаётся.
 */
export function withMode(drafts: readonly LineDraft[]): LineDraft[] {
  return drafts.map(draft => (toNumber(draft.vatRate) === null ? draft : { ...draft, vatAmount: '' }));
}

/** Кусок полосы сверки: подпись, число (жирным) и тон. */
export interface ReconciliationItem {
  label: string;
  value: number | null;
  tone: 'main' | 'quiet' | 'warning';
}

export interface Reconciliation {
  items: ReconciliationItem[];
  /** Расхождение с суммой к оплате; `null` — его нет или сравнивать не с чем. */
  difference: number | null;
  /** Сходится с суммой к оплате — сказать об этом, а не молчать (issue #1499). */
  agrees: boolean;
}

/**
 * Полоса сверки порядком бумаги (issue #1499).
 *
 * <p>«В том числе» — как прежде: сумма строк и НДС в ней. Сверху — «сумма строк без НДС · НДС · всего с
 * НДС · к оплате». НДС берётся у строк, когда он у каждой, иначе из реквизитов («В том числе НДС» шапки) —
 * то же правило, что у сервера (`InvoiceLineTotals.VatFrom`).</p>
 *
 * <p>⚠️ <b>Сверху без НДС расхождения нет.</b> Сравнить сумму без НДС с суммой к оплате значило бы
 * выдать налог за расхождение — отказ, переодетый в результат. Взамен — «всего с НДС не посчитать».</p>
 */
export function reconcile(
  sums: LineTotals, paperVat: unknown, total: unknown, format: (value: number) => string,
): Reconciliation {
  const head = typeof paperVat === 'number' ? paperVat : null;
  const items: ReconciliationItem[] = [];
  let gross: number | null = sums.amount;

  if (sums.mode === 'Included') {
    items.push({ label: 'Сумма строк: ', value: sums.amount, tone: 'main' });
    items.push({ label: vatCaption(sums, format), value: null, tone: 'quiet' });
  } else {
    items.push({ label: 'Сумма строк без НДС: ', value: sums.net ?? 0, tone: 'main' });
    if (sums.count === 0) gross = 0;
    else if (sums.vatMissing === 0) items.push({ label: 'НДС по строкам: ', value: sums.vat, tone: 'quiet' });
    else if (head !== null) {
      items.push({ label: 'НДС из реквизитов: ', value: head, tone: 'quiet' });
      gross = money((sums.net ?? 0) + head);
    } else {
      gross = null;
      items.push({
        label: sums.vatMissing < sums.count
          ? `НДС по строкам: ${format(sums.vat)}, у ${lines(sums.vatMissing)} не указан — всего с НДС не посчитать. `
            + 'Впишите ставку в строках или НДС в реквизитах'
          : 'НДС не указан — всего с НДС не посчитать. Впишите НДС в реквизитах или ставку в строках',
        value: null, tone: 'warning',
      });
    }
    if (gross !== null && sums.count > 0) items.push({ label: 'всего с НДС: ', value: gross, tone: 'quiet' });
  }

  if (typeof total === 'number') items.push({ label: 'к оплате по счёту: ', value: total, tone: 'quiet' });

  const difference = gross === null ? null : mismatch(total, gross, sums.count);
  return {
    items,
    difference,
    agrees: gross !== null && sums.count > 0 && typeof total === 'number' && difference === null,
  };
}

/**
 * Что смена режима сделает с разноской (issue #1499) — до сохранения, пока она обратима щелчком.
 *
 * <p>Сумма строки с НДС меняется. Разноска по количеству пересчитывается сама; разноска суммой —
 * записанными деньгами — разойдётся, и это надо назвать строками. Разобранный счёт с несошедшейся
 * разноской сервер вернёт в черновик.</p>
 */
export function modeConsequences(view: InvoiceView, parsed: boolean): string {
  const allocated = view.lines.filter(line => line.allocation.parts.length > 0);
  const bySum = allocated.filter(line => line.allocation.mode === 'amount').map(line => line.ordinal);
  const parts = ['Суммы строк с НДС изменятся'];
  if (allocated.some(line => line.allocation.mode === 'quantity')) parts.push('разноска по количеству пересчитается сама');
  if (bySum.length > 0)
    parts.push(`разноска суммой у ${bySum.length === 1 ? 'строки' : 'строк'} ${bySum.join(', ')} разойдётся — проверьте её после сохранения`);
  const text = parts.join(', ');
  return parsed && allocated.length > 0 ? `${text}. Если разноска не сойдётся, счёт вернётся в черновик.` : `${text}.`;
}

/** Подсказка полосы сверки (issue #1500): что сказать и какой режим предложить. */
export function modeHint(explains: 'onTop' | 'included'): { text: string; action: string; to: VatMode } {
  return explains === 'onTop'
    ? { text: 'Расхождение равно НДС — похоже, цены в строках без НДС.', action: 'Считать НДС сверху', to: 'OnTop' }
    : {
      text: 'Суммы строк без НДС уже сходятся с суммой к оплате — похоже, цены в строках с НДС.',
      action: 'Считать НДС в сумме', to: 'Included',
    };
}

/**
 * Догадка сервера о режиме — только о СОХРАНЁННЫХ строках: с правкой рядом она говорила бы о том, чего
 * на экране уже нет.
 */
export function savedExplains(view: InvoiceView): 'onTop' | 'included' | null {
  return view.readiness?.gaps?.find(gap => gap.vatExplains)?.vatExplains ?? null;
}

