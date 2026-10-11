import type { WaybillView } from '@/shared/api/waybills';
import { formatDate } from '@/shared/format/format';
import { deleteRecord } from './recordDelete';

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

/**
 * Удалить накладную — и перевести отказы на язык экрана. Разбор общий со счётом: см. `deleteRecord`.
 *
 * @param send сам запрос удаления.
 * @param readFresh прочитать накладную заново, мимо кэша.
 */
export const deleteWaybill = (send: () => Promise<unknown>, readFresh: () => Promise<WaybillView>) =>
  deleteRecord(send, readFresh, fresh => canDeleteWaybill(fresh, true), {
    unknown: 'Удалена ли накладная — неизвестно: ответ не пришёл. Посмотрите список накладных.',
    changed: 'Накладную тем временем изменили. Она не удалена: закройте это окно, посмотрите накладную '
      + 'заново и решите ещё раз.',
    noLonger: () => 'Накладную тем временем провели. Она не удалена: удаляется только черновик — верните '
      + 'накладную в черновик.',
  });
