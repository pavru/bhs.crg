import { isNotFound } from '@/shared/api/invoices';
import { isConflict } from '@/shared/utils/apiError';

/** Чем кончилось удаление: удалили этим запросом — или записи уже не было. */
export type DeleteOutcome = 'deleted' | 'already';

/** Слова документа: разбор отказов у счёта и накладной один, называют они себя по-разному. */
export interface DeleteWords<T> {
  /** Ответ не пришёл: исход неизвестен. */
  unknown: string;
  /** Документ изменили, но удалить его по-прежнему можно: решить заново. */
  changed: string;
  /** Документ изменили так, что удалить его уже нельзя: чем он стал и какой у этого выход. */
  noLonger: (fresh: T) => string;
}

/**
 * Удалить документ модуля счетов — и перевести отказы на язык экрана (issue #1282, #1283). Одно место
 * на счёт и накладную: скопированный разбор расходился бы первой же правкой (ревью PR #1299).
 *
 * <p><b>404 — не отказ</b>: документа уже нет (сосед удалил его секундой раньше, либо ответ на первый
 * запрос потерялся). Человек хотел именно этого, и «не удалён» про удалённое было бы неправдой.</p>
 *
 * <p><b>409 здесь всегда о версии</b>: сервер сверяет её раньше, чем условие удаления, а экран
 * предлагает удалить только то, что удалить можно, — значит, документ успели изменить. Слова сервера
 * («перечитайте и повторите») при этом не годятся ни в одном из двух положений, и различает их
 * свежепрочитанный документ. Удаляемый по-прежнему — тихого повтора нет: удалять изменённое вслепую
 * нельзя. Уже не удаляемый — «повторить» нечего, и названо, чем он стал и какой у этого выход.</p>
 *
 * <p><b>Ответа нет</b> — исход неизвестен, и сказать «не удалён» значило бы соврать в половине
 * случаев: запрос мог дойти.</p>
 *
 * @param send сам запрос удаления.
 * @param readFresh прочитать документ заново, мимо кэша.
 * @param deletable то же условие, по которому экран показывает действие.
 */
export async function deleteRecord<T>(
  send: () => Promise<unknown>, readFresh: () => Promise<T>, deletable: (fresh: T) => boolean,
  words: DeleteWords<T>,
): Promise<DeleteOutcome> {
  try {
    await send();
    return 'deleted';
  } catch (refusal) {
    if (isNotFound(refusal)) return 'already';
    if (!(refusal as { response?: unknown })?.response) throw new Error(words.unknown, { cause: refusal });
    if (!isConflict(refusal)) throw refusal;

    let fresh: T;
    try { fresh = await readFresh(); }
    catch (unread) {
      // Документа уже нет — его удалил тот, с кем столкнулись. Не прочитался иначе — остаётся исходный отказ.
      if (isNotFound(unread)) return 'already';
      throw refusal;
    }
    throw new Error(deletable(fresh) ? words.changed : words.noLonger(fresh), { cause: refusal });
  }
}
