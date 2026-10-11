import { createContext, useContext } from 'react';
import type { InvoiceView } from '@/shared/api/invoices';
import { apiError } from '@/shared/utils/apiError';
import { formatDate } from '@/shared/format/format';
import { K, asInput } from './invoiceFields';

/**
 * Удаление счёта — правила экрана (issue #1282; раздел Дизайнера от 11.10.2026).
 *
 * <p>Удаляется только черновик без оплаты — насовсем, вместе с файлом. Здесь лежит всё, что экран
 * решает и говорит об этом сам: когда действие есть, как назвать счёт и чем кончилась попытка.</p>
 */

const DRAFT = 'Черновик';

/**
 * Можно ли удалить — то же условие, что проверит сервер. Действие, которое сервер заведомо откажется
 * выполнить, не показывается вовсе (правило экрана): у разобранного, отклонённого и оплаченного пункта
 * нет, а путь к удалению называет руководство.
 */
export const canDelete = (view: InvoiceView, canEdit: boolean): boolean =>
  canEdit && view.requisites[K.state] === DRAFT && !view.payment.paid;

/** Имя файла счёта; `null` — файл не приложен. */
export function fileNameOf(view: InvoiceView): string | null {
  const node = view.requisites[K.scan];
  if (node == null || typeof node !== 'object') return null;
  const name = (node as Record<string, unknown>).fileName;
  return typeof name === 'string' && name ? name : null;
}

/** «Счёт № 123 от 01.10.2026» / «Счёт без номера» — в заголовке диалога и в сообщении об исходе. */
export function invoiceTitle(view: InvoiceView): string {
  const number = asInput(view.requisites[K.number]).trim();
  if (!number) return 'счёт без номера';
  const date = asInput(view.requisites[K.date]).slice(0, 10);
  return `счёт № ${number}${date ? ` от ${formatDate(date)}` : ''}`;
}

/**
 * Кнопка подтверждения называет то, что удаляется, — не голое «Удалить». У счёта без номера опознать
 * его можно только по файлу; нет и файла — это пустой черновик, и так и сказано.
 */
export function confirmLabel(view: InvoiceView): string {
  const number = asInput(view.requisites[K.number]).trim();
  if (number) return `Удалить счёт № ${number}`;
  const file = fileNameOf(view);
  return file ? `Удалить счёт из файла „${file}“` : 'Удалить пустой черновик';
}

/** Что уйдёт вместе со счётом — только существующее: пустой перечень пугал бы тем, чего нет. */
export function goingAway(view: InvoiceView): string[] {
  const gone: string[] = [];
  if (view.lines.length > 0) gone.push(`строки счёта — ${view.lines.length}`);
  if (view.lines.some(line => line.allocation.parts.length > 0)) gone.push('разноска по объектам и статьям');
  const file = fileNameOf(view);
  if (file) gone.push(`файл счёта «${file}» и прочитанное из него`);
  return gone;
}

/** Чем кончилось удаление: удалили этим запросом — или счёта уже не было. */
export type DeleteOutcome = 'deleted' | 'already';

/**
 * Удалить счёт — и перевести отказы на язык экрана.
 *
 * <p><b>404 — не отказ</b>: счёта уже нет (сосед удалил его секундой раньше, либо ответ на первый
 * запрос потерялся). Человек хотел именно этого, и «не удалён» про удалённый счёт было бы неправдой.</p>
 *
 * <p><b>409 «счёт изменили»</b> здесь бывает и без чужих рук: файл дочитался между открытием диалога
 * и нажатием, и версия счёта сменилась. Тихого повтора нет — прочитанное могло заполнить счёт, и
 * удалять его вслепую нельзя; текст так и говорит. Прочие отказы сервера уходят его словами.</p>
 *
 * <p><b>Ответа нет</b> — исход неизвестен, и сказать «не удалён» значило бы соврать в половине
 * случаев: запрос мог дойти.</p>
 */
export async function deleteInvoice(send: () => Promise<unknown>): Promise<DeleteOutcome> {
  try {
    await send();
    return 'deleted';
  } catch (refusal) {
    const response = (refusal as { response?: { status?: number } })?.response;
    if (response?.status === 404) return 'already';
    if (!response)
      throw new Error('Удалён ли счёт — неизвестно: ответ не пришёл. Посмотрите список счетов.', { cause: refusal });
    if (response.status === 409 && apiError(refusal, '').includes('тем временем изменили'))
      throw new Error(
        'Счёт тем временем изменили — или дочитали его файл. Он не удалён: закройте это окно, '
        + 'посмотрите счёт заново и решите ещё раз.', { cause: refusal });
    throw refusal;
  }
}

/** Откуда пришли к удалению: от этого зависит первая строка диалога. */
export type DeleteReason = 'menu' | 'duplicate' | 'wrong-file';

/**
 * Спросить об удалении открытого счёта. `null` — удалить нельзя: все три входа (меню «Ещё», оговорка
 * о дубликате, полоса исхода распознавания) в этом случае действие не показывают.
 */
export const InvoiceDeleteContext = createContext<((reason: DeleteReason) => void) | null>(null);

export const useInvoiceDelete = () => useContext(InvoiceDeleteContext);
