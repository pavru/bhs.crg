import { isNotFound } from '@/shared/api/invoices';
import type { WaybillView } from '@/shared/api/waybills';
import { isConflict } from '@/shared/utils/apiError';
import { formatDate } from '@/shared/format/format';

/**
 * Удаление накладной — правила экрана (issue #1283; раздел Дизайнера от 11.10.2026). Устроено так же,
 * как удаление счёта (`invoiceDelete.ts`): подтверждение и отказы у двух документов звучат одинаково.
 *
 * <p>Удаляется только черновик — насовсем. Здесь лежит всё, что экран решает и говорит об этом сам:
 * когда действие есть, как назвать накладную и чем кончилась попытка.</p>
 */

/**
 * Можно ли удалить — то же условие, что проверит сервер. Действие, которое сервер заведомо откажется
 * выполнить, не показывается вовсе (правило экрана): у проведённой пункта нет — её возвращают в черновик.
 */
export const canDeleteWaybill = (view: WaybillView, canEdit: boolean): boolean =>
  canEdit && view.state === 'Draft';

/** «накладная № 45 от 02.10.2026» / «накладная без номера» — в заголовке диалога и в сообщении об исходе. */
export function waybillTitle(view: WaybillView): string {
  if (!view.number) return 'накладная без номера';
  return `накладная № ${view.number}${view.issuedOn ? ` от ${formatDate(view.issuedOn)}` : ''}`;
}

/** Кнопка подтверждения называет то, что удаляется, — не голое «Удалить». */
export const waybillConfirmLabel = (view: WaybillView): string =>
  view.number ? `Удалить накладную № ${view.number}` : 'Удалить накладную без номера';

/** Чем кончилось удаление: удалили этим запросом — или накладной уже не было. */
export type WaybillDeleteOutcome = 'deleted' | 'already';

/**
 * Удалить накладную — и перевести отказы на язык экрана.
 *
 * <p><b>404 — не отказ</b>: накладной уже нет (сосед удалил её секундой раньше, либо ответ на первый
 * запрос потерялся). Человек хотел именно этого, и «не удалена» про удалённую было бы неправдой.</p>
 *
 * <p><b>409</b> бывает про два положения, и различает их свежепрочитанная накладная, а не слова отказа.
 * Она по-прежнему черновик — значит, отказ был о версии: накладную изменили между открытием диалога и
 * нажатием. Тихого повтора нет — удалять изменённое вслепую нельзя. Уже проведена — причину назвал
 * сервер, и она уходит его словами.</p>
 *
 * <p><b>Ответа нет</b> — исход неизвестен, и сказать «не удалена» значило бы соврать в половине
 * случаев: запрос мог дойти.</p>
 *
 * @param send сам запрос удаления.
 * @param readFresh прочитать накладную заново, мимо кэша.
 */
export async function deleteWaybill(
  send: () => Promise<unknown>, readFresh: () => Promise<WaybillView>,
): Promise<WaybillDeleteOutcome> {
  try {
    await send();
    return 'deleted';
  } catch (refusal) {
    if (isNotFound(refusal)) return 'already';
    if (!(refusal as { response?: unknown })?.response)
      throw new Error('Удалена ли накладная — неизвестно: ответ не пришёл. Посмотрите список накладных.', { cause: refusal });
    if (!isConflict(refusal)) throw refusal;

    let fresh: WaybillView;
    try { fresh = await readFresh(); }
    catch (unread) {
      // Накладной уже нет — её удалил тот, с кем столкнулись. Не прочиталась иначе — остаётся исходный отказ.
      if (isNotFound(unread)) return 'already';
      throw refusal;
    }
    if (!canDeleteWaybill(fresh, true)) throw refusal;
    throw new Error(
      'Накладную тем временем изменили. Она не удалена: закройте это окно, посмотрите накладную заново '
      + 'и решите ещё раз.', { cause: refusal });
  }
}
