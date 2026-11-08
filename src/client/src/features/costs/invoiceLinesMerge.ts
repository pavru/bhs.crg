import type { InvoiceView, VatMode } from '@/shared/api/invoices';
import { vatModeOf } from './invoiceVat';
import { toDrafts, toPayload, type LineDraft } from './invoiceLines';

/**
 * Ответ записи строк, слитый с тем, что набрано, пока запись шла (issue #1656, change
 * `costs-invoice-lines-save-race`, D1 (б), ТЗ COST-7).
 *
 * <p>Таблица на время записи не заперта, и до этого правила ответ ложился на черновик целиком: набранное между
 * отправкой и ответом пропадало без следа. Теперь черновик сверяется со СНИМКОМ отправленного:</p>
 *
 * <ul>
 *   <li>строка не менялась с отправки — берётся из ответа (досчитанные суммы, `id` новой строки);</li>
 *   <li>строку правили во время записи — остаётся набранной; из ответа ей достаётся `id` и серверные признаки,
 *     и только те, чьи ключевые поля с отправки не менялись: потеря позиции — при той же позиции, пометка
 *     «запомнено» — при той же позиции, пометке и ключе строки. Числа сервера в набранное не возвращаются —
 *     запятая под курсором не пропадает;</li>
 *   <li>строку добавили во время записи — остаётся новой; удалили — её нет, следующая запись удалит её и на
 *     сервере.</li>
 * </ul>
 *
 * <p>«Есть несохранённое» — черновик отличается от принятого ответа тем, что уехало бы на сервер.</p>
 *
 * <p>Здесь, а не в `shared`: правило «что изменено с отправки» знает строку счёта (D3). Тот же приём нужен
 * автосохранению строк, когда до него дойдёт.</p>
 */
export interface LinesMerged {
  drafts: LineDraft[];
  mode: VatMode;
  dirty: boolean;
}

/**
 * @param sent черновик и режим НА МИГ ОТПРАВКИ — каким он стоял в форме (а не тело запроса: снятые сменой
 *   поставщика пометки — правка сервера, а не человека, и она обязана прийти из ответа).
 * @param now черновик и режим на миг ответа.
 * @param saved ответ записи.
 */
export function mergeSaved(
  sent: { drafts: readonly LineDraft[]; mode: VatMode },
  now: { drafts: readonly LineDraft[]; mode: VatMode },
  saved: InvoiceView,
): LinesMerged {
  const savedMode = vatModeOf(saved);
  const answer = toDrafts(saved.lines, savedMode);
  const byId = new Map(answer.map(line => [line.key, line]));
  const before = new Map(sent.drafts.map(line => [line.key, line]));
  // Режим переключили во время записи — он и остаётся: строки под ним уже пересчитаны (`withMode`).
  const mode = now.mode === sent.mode ? savedMode : now.mode;

  const drafts = now.drafts.map(draft => {
    // У новой строки ключ — её `newId`, и строка из ответа зовётся им же.
    const server = byId.get(draft.id ?? draft.key);
    const was = before.get(draft.key);
    if (!server) {
      // Строку удалили ДО отправки, а «Вернуть» поставил её назад во время записи: `id` в счёте больше нет.
      // Новой строкой с тем же ключом (`newId`) она вернётся под прежним идентификатором.
      return draft.id !== null ? { ...draft, id: null } : draft;
    }
    if (was && sameLine(was, sent.mode, draft, now.mode)) return server;

    const nomenclatureKept = was !== undefined && was.nomenclatureId === draft.nomenclatureId;
    const markKept = nomenclatureKept && was.matchedBy === draft.matchedBy
      && was.supplierCode === draft.supplierCode && was.supplierText === draft.supplierText;
    return {
      ...draft,
      id: server.id,
      ...(nomenclatureKept ? {
        nomenclatureLost: server.nomenclatureLost,
        nomenclatureMoved: server.nomenclatureMoved,
        nomenclatureArchived: server.nomenclatureArchived,
      } : {}),
      ...(markKept ? { matchedBy: server.matchedBy, match: server.match } : {}),
    };
  });

  const dirty = mode !== savedMode || drafts.length !== answer.length
    || drafts.some((draft, index) => !sameLine(draft, mode, answer[index], savedMode));
  return { drafts, mode, dirty };
}

/** Одна и та же ли строка — тем, что уехало бы на сервер. */
function sameLine(a: LineDraft, aMode: VatMode, b: LineDraft, bMode: VatMode): boolean {
  return JSON.stringify(toPayload([a], aMode)) === JSON.stringify(toPayload([b], bMode));
}
