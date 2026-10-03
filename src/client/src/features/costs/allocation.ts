import type { AllocationPartView, LineAllocationView } from '@/shared/api/invoices';
import { toNumber } from './invoiceLines';

/**
 * Разноска строки в форме (задача F1, issue #1085, ТЗ COST-10, COST-13).
 *
 * <p><b>Считает сервер.</b> Суммы частей, копейки округления и расхождение с суммой к оплате приезжают
 * в ответе, и форма их только показывает. Здесь — ровно то, без чего нельзя редактировать: черновик
 * частей и ОЦЕНКА остатка, пока части не сохранены. Оценка подписана как оценка: после сохранения её
 * сменит число сервера, и расхождение на копейку без подписи читалось бы как ошибка.</p>
 */
export interface PartDraft {
  /** Ключ строки таблицы: `id` сохранённой части или временный у новой. */
  key: string;
  id: string | null;
  /** Стройка или статья вне строек (F3) — одно из двух; пустая строка — не выбрано. */
  constructionId: string;
  sectionId: string;
  articleId: string;
  /** Количество у строки с количеством, сумма — у строки без него. Текстом, как набрано. */
  value: string;
}

let sequence = 0;

export function emptyPart(constructionId = ''): PartDraft {
  sequence += 1;
  return { key: `новая-часть-${sequence}`, id: null, constructionId, sectionId: '', articleId: '', value: '' };
}

/**
 * Черновик частей из ответа сервера.
 *
 * ⚠️ У строки суммой в черновик идёт сумма части БЕЗ расхождения со счётом. Сервер отдаёт её уже с
 * расхождением, ушедшим в последнюю часть, а хранит — без: прими форма число ответа, повторное
 * сохранение записало бы расхождение в саму часть, и разнесено оказалось бы больше, чем стоит строка.
 */
export function toPartDrafts(allocation: LineAllocationView): PartDraft[] {
  return allocation.parts.map((part: AllocationPartView) => ({
    key: part.id,
    id: part.id,
    constructionId: part.constructionId ?? '',
    sectionId: part.sectionId ?? '',
    articleId: part.articleId ?? '',
    value: formatPlain(allocation.mode !== 'amount' ? part.quantity
      : part.amount === null ? null : round(part.amount - part.discrepancy, 2)),
  }));
}

/**
 * Набор частей для сервера.
 *
 * ⚠️ Уезжает РОВНО одно из двух — количество или сумма, по виду строки. Сумму части строки с
 * количеством считает сервер, и присланную он отвергнет: она была бы вторым ответом на тот же вопрос.
 * Пустое значение уезжает `null` — отказ сервера назовёт поле, а молча выброшенная часть исчезла бы.
 */
export function toPartsPayload(
  drafts: readonly PartDraft[], mode: LineAllocationView['mode'],
): Record<string, unknown>[] {
  return drafts.map(draft => ({
    id: draft.id,
    construction: draft.constructionId || null,
    section: draft.sectionId || null,
    article: draft.articleId || null,
    [mode === 'amount' ? 'amount' : 'quantity']: toNumber(draft.value),
  }));
}

/**
 * Остаток «не разнесено» по черновику — оценка до сохранения.
 *
 * <p>Сумма остатка у строки с количеством — доля суммы строки, тем же правилом, что у сервера. Копейки
 * округления сервер отдаёт последней части, и после сохранения число может сдвинуться на копейку, —
 * поэтому это «оценка», а не ответ.</p>
 */
export function estimateRemainder(
  drafts: readonly PartDraft[],
  line: { quantity: number | null; amount: number | null },
  mode: LineAllocationView['mode'],
): { quantity: number | null; amount: number | null } {
  const given = drafts.reduce((sum, draft) => sum + (toNumber(draft.value) ?? 0), 0);

  if (mode === 'quantity' && line.quantity) {
    const quantity = round(line.quantity - given, 3);
    return {
      quantity,
      amount: line.amount === null ? null : round(line.amount * quantity / line.quantity, 2),
    };
  }

  if (mode === 'amount' && line.amount !== null)
    return { quantity: null, amount: round(line.amount - given, 2) };

  return { quantity: null, amount: null };
}

/**
 * Состояние разноски строки одной фразой — для клетки таблицы строк. Остаток виден ВСЕГДА (ТЗ COST-13),
 * в том числе когда разнесено больше, чем есть (строку уменьшили после разноски).
 */
export function allocationStatus(
  allocation: LineAllocationView, unit: string | null,
): { text: string; tone: 'ok' | 'warning' | 'muted' } {
  if (allocation.mode === 'none')
    return allocation.parts.length === 0
      ? { text: 'нечего разносить', tone: 'muted' }
      : { text: 'части не того вида', tone: 'warning' };

  const lost = allocation.parts.find(p => p.targetLost);
  if (lost) return { text: lost.articleId ? 'статья удалена' : 'стройка удалена', tone: 'warning' };
  if (allocation.parts.some(p => p.mismatched)) return { text: 'части не того вида', tone: 'warning' };
  if (allocation.balanced) return { text: 'разнесено', tone: 'ok' };
  if (allocation.parts.length === 0) return { text: 'не разнесено', tone: 'warning' };

  const rest = allocation.mode === 'quantity'
    ? `${formatPlain(allocation.unallocatedQuantity)}${unit ? ` ${unit}` : ''}`
    : `${formatPlain(allocation.unallocatedAmount)} ₽`;

  return (allocation.mode === 'quantity' ? allocation.unallocatedQuantity ?? 0 : allocation.unallocatedAmount ?? 0) < 0
    ? { text: `разнесено лишнее: ${rest.replace('-', '')}`, tone: 'warning' }
    : { text: `не разнесено: ${rest}`, tone: 'warning' };
}

/** Число без группировки разрядов и лишних нулей: «100», «7,25». Для полей ввода и коротких фраз. */
export function formatPlain(value: number | null): string {
  if (value === null) return '';
  return String(round(value, 3)).replace('.', ',');
}

function round(value: number, digits: number): number {
  const factor = 10 ** digits;
  return Math.round(value * factor) / factor;
}
