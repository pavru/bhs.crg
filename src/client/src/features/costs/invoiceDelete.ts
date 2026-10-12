import { createContext, useContext } from 'react';
import type { InvoiceView } from '@/shared/api/invoices';
import { formatDate } from '@/shared/format/format';
import { K, asInput } from './invoiceFields';
import { deleteRecord } from './recordDelete';

/**
 * Удаление счёта — правила экрана (issue #1282; раздел Дизайнера от 11.10.2026).
 *
 * <p>Удаляется только черновик без оплаты — насовсем, вместе с файлом. Здесь лежит всё, что экран
 * решает и говорит об этом сам: когда действие есть, как назвать счёт и чем кончилась попытка.</p>
 */

export const DRAFT = 'Черновик';

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

export type { DeleteOutcome } from './recordDelete';

/**
 * Удалить счёт — и перевести отказы на язык экрана. Разбор общий с накладной: см. `deleteRecord`.
 * У счёта «изменили» бывает и без человека: его файл дочитался между открытием диалога и нажатием.
 *
 * @param send сам запрос удаления.
 * @param readFresh прочитать счёт заново, мимо кэша.
 */
export const deleteInvoice = (send: () => Promise<unknown>, readFresh: () => Promise<InvoiceView>) =>
  deleteRecord(send, readFresh, fresh => canDelete(fresh, true), {
    unknown: 'Удалён ли счёт — неизвестно: ответ не пришёл. Посмотрите список счетов.',
    changed: 'Счёт тем временем изменили — или дочитали его файл. Он не удалён: закройте это окно, '
      + 'посмотрите счёт заново и решите ещё раз.',
    noLonger: fresh => fresh.payment.paid
      ? 'Счёт тем временем оплатили. Он не удалён: удаляется только черновик без оплаты — сначала '
        + 'отмените оплату.'
      : `Счёт тем временем изменили: теперь он — ${String(fresh.requisites[K.state]).toLowerCase()}. Он не `
        + 'удалён: удаляется только черновик — верните счёт в черновик.',
  });

/** Откуда пришли к удалению: от этого зависит первая строка диалога. */
export type DeleteReason = 'menu' | 'duplicate' | 'same-file' | 'wrong-file';

/**
 * Спросить об удалении открытого счёта. `null` — удалить нельзя: все три входа (меню «Ещё», оговорка
 * о дубликате, полоса исхода распознавания) в этом случае действие не показывают.
 */
export const InvoiceDeleteContext = createContext<((reason: DeleteReason) => void) | null>(null);

export const useInvoiceDelete = () => useContext(InvoiceDeleteContext);
