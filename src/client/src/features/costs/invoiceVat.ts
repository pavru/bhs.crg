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
 *
 * <p>⚠️ Стёртый НДС откладывается, а не теряется (ревью PR #1508): режимов два, и обратное переключение
 * возвращает строке её НДС. Иначе флажок, поставленный и тут же снятый, молча заменял бы НДС бумаги
 * (219,99) расчётным (220,00).</p>
 */
export function withMode(drafts: readonly LineDraft[]): LineDraft[] {
  return drafts.map(draft => {
    if (draft.vatAside !== undefined) return { ...draft, vatAmount: draft.vatAside, vatAside: undefined };
    return toNumber(draft.vatRate) === null ? draft : { ...draft, vatAmount: '', vatAside: draft.vatAmount };
  });
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
 * Сверху с НДС шапки «сошлось» — арифметика бумаги, а не готовность: у строки без ставки суммы с НДС нет,
 * и «Разобран» её не примет (ревью PR #1508). Зелёное «сходится» рядом с отказом готовности спорило бы с
 * ним, поэтому полоса говорит, чего не хватает.
 */
const HEAD_ONLY = 'с НДС из реквизитов сходится — для «Разобран» впишите ставку НДС в строках';

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
  let fromHead = false;

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
      fromHead = true;
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
  const matched = gross !== null && sums.count > 0 && typeof total === 'number' && difference === null;
  if (matched && fromHead) items.push({ label: HEAD_ONLY, value: null, tone: 'warning' });
  return { items, difference, agrees: matched && !fromHead };
}

/**
 * Что смена режима сделает с разноской (issue #1499) — до сохранения, пока она обратима щелчком.
 *
 * <p>Сумма строки с НДС меняется. Разноска по количеству пересчитывается сама; разноска суммой —
 * записанными деньгами — разойдётся, и это надо назвать строками. Разобранный счёт с несошедшейся
 * разноской сервер вернёт в черновик.</p>
 *
 * <p>Разноска — у сохранённых строк, а номера — как на экране, по черновику (ревью PR #1508): удалённая
 * в черновике строка сдвигает номера, и сохранённый номер назвал бы чужую строку.</p>
 */
export function modeConsequences(view: InvoiceView, drafts: readonly LineDraft[], parsed: boolean): string {
  const saved = new Map(view.lines.map(line => [line.id, line]));
  const allocated = drafts.flatMap((draft, index) => {
    const line = draft.id ? saved.get(draft.id) : undefined;
    return line && line.allocation.parts.length > 0 ? [{ number: index + 1, mode: line.allocation.mode }] : [];
  });
  const bySum = allocated.filter(line => line.mode === 'amount').map(line => line.number);
  const parts = ['Суммы строк с НДС изменятся'];
  if (allocated.some(line => line.mode === 'quantity')) parts.push('разноска по количеству пересчитается сама');
  if (bySum.length > 0)
    parts.push(`разноска суммой у ${bySum.length === 1 ? 'строки' : 'строк'} ${bySum.join(', ')} разойдётся — проверьте её после сохранения`);
  const text = parts.join(', ');
  return parsed && allocated.length > 0 ? `${text}. Если разноска не сойдётся, счёт вернётся в черновик.` : `${text}.`;
}

/**
 * Доказательство режима, поставленного распознаванием (issue #1501): «строки 1 500,00 + НДС 330,00 = к
 * оплате 1 830,00». Числа — рядом с «Верно», чтобы его нажимали, сверив с файлом, а не наугад. НДС — у
 * строк, когда он у каждой, иначе из шапки; «=» — только когда сходится: равенство, которого нет, было бы
 * неправдой ровно там, где человек сверяет.
 */
export function modeProof(sums: LineTotals, paperVat: unknown, total: unknown, format: (value: number) => string): string {
  const net = sums.net ?? sums.amount;
  const vat = sums.count > 0 && sums.vatMissing === 0 ? sums.vat : typeof paperVat === 'number' ? paperVat : null;
  const lines = vat === null ? `строки ${format(net)}` : `строки ${format(net)} + НДС ${format(vat)}`;
  if (typeof total !== 'number') return lines;
  return vat !== null && money(net + vat) === total
    ? `${lines} = к оплате ${format(total)}`
    : `${lines} · к оплате ${format(total)}`;
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

