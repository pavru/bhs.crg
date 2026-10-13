import { purgeOffered, type PurgeHolder, type PurgeOffer } from '@/shared/api/recordPurge';
import { apiError, isConflict } from '@/shared/utils/apiError';

/**
 * Чистая логика диалога принудительного удаления (issue #1187): разбор введённого числа и слова.
 */

/**
 * Число, которое ввёл человек. Пробелы отбрасываются — их ставят как разделитель разрядов; всё, что
 * не цифры целиком, — не число: «43 шт» и «4З» подтверждением не считаются.
 */
export function typedCount(input: string): number | null {
  const digits = input.replace(/\s+/g, '');
  return /^\d+$/.test(digits) ? Number(digits) : null;
}

/** Совпало ли введённое с числом теряемых ссылок — только тогда кнопка удаления доступна. */
export function countMatches(input: string, offer: PurgeOffer): boolean {
  return typedCount(input) === offer.references;
}

/**
 * Показывать ли «не совпадает». Пока человек печатает, поле не ругается: при итоге «143» ввод «14» —
 * ещё не ошибка. Ошибкой он становится, когда знаков набрано столько же (или больше), либо когда
 * человек из поля ушёл или нажал Enter (`settled`).
 */
export function mismatchShown(input: string, offer: PurgeOffer, settled: boolean): boolean {
  const digits = input.replace(/\s+/g, '');
  if (digits === '' || countMatches(input, offer)) return false;
  return settled || digits.length >= String(offer.references).length;
}

/** Строка разбивки: «„Счета и накладные“ (модуль выключен) · строки счетов». */
export function holderLabel(holder: PurgeHolder): string {
  return `${holder.owner} · ${holder.what}`;
}

/**
 * Последствия — строками списка. О том, что модуль покажет потерю после включения, говорится только
 * если есть что показывать: когда не найдётся ни одна ссылка, такое обещание было бы неправдой.
 */
export function consequences(offer: PurgeOffer): string[] {
  return [
    'Запись будет удалена совсем. Вернуть её можно только из резервной копии.',
    ...lostLinks(offer),
    'Действие записывается в журнал: кто, какую запись, сколько ссылок потеряно.',
  ];
}

/**
 * Что станет со ссылками — одни слова на одну запись и на пакет (ревью PR #1308): пакетный диалог
 * раньше вырезал их из середины `consequences` по номерам строк и молча разошёлся бы с ним при
 * первой же строке, добавленной в начало или конец.
 */
export function lostLinks(offer: Pick<PurgeOffer, 'references' | 'untraceable'>): string[] {
  const none = 'это данные модуля, которого нет в сборке, или колонки, о которой модуль не объявил. ' +
    'Единственный след — запись в журнале.';
  const lost = 'Ссылки останутся в данных модуля и будут вести в пустоту.';
  const shown = 'После включения модуль покажет их в своих отборах; в документах закрытого периода ' +
    'исправить их будет нельзя.';
  return [
    offer.untraceable >= offer.references ? `${lost} Потом их не покажет никто: ${none}` : `${lost} ${shown}`,
    ...(offer.untraceable > 0 && offer.untraceable < offer.references
      ? [`Из них ${offer.untraceable} не покажет никто: ${none}`] : []),
  ];
}

/** Что изменилось между показанным и нынешним: человек подтверждал одно число, а стало другое. */
export function changedNote(before: number, now: number): string {
  return `Пока диалог был открыт, число ссылок изменилось: было ${before}, стало ${now}. ` +
    'Ничего не удалено — сверьте разбивку и введите новое число.';
}

/**
 * Чем кончилась неудачная попытка — от этого зависит, что человеку остаётся (ревью PR #1246):
 *  - `recount` — сервер прислал свежее предложение: разбивку показать заново, число ввести снова;
 *  - `final`   — выхода больше нет (модуль включили, запись стало держать ядро, записи уже нет);
 *  - `retry`   — ответа по существу нет (сеть, сбой сервера): диалог остаётся, можно повторить.
 *    ⚠️ Удалена ли запись, в этом случае неизвестно — и говорить «удаление невозможно» нельзя.
 */
export type PurgeFailure<O extends PurgeOffer = PurgeOffer> =
  | { kind: 'recount'; offer: O; note: string }
  | { kind: 'final'; text: string }
  | { kind: 'retry'; text: string };

/** Слова исходов, в которых названо удаляемое: у записи одни, у стройки и раздела — свои (#1243). */
export interface PurgeWords {
  /** Отказ без слов сервера. */
  refused: string;
  /** 404: удаляемого уже нет. */
  gone: string;
  /** Конец фразы «…» после слов сервера, когда ответа по существу нет: что неизвестно и где проверить. */
  unknown: string;
}

const RECORD_WORDS: PurgeWords = {
  refused: 'Удалить запись нельзя.',
  gone: 'Записи уже нет: её удалили, пока диалог был открыт.',
  unknown: 'Удалена ли запись — неизвестно: закройте диалог и проверьте список. Если запись на месте, удаление можно повторить.',
};

/** Запись: слова и разбор предложения — свои, называть их не нужно. */
export function purgeFailure(e: unknown, shown: PurgeOffer): PurgeFailure;
/**
 * Всё остальное обязано назвать и слова, и разбор (ревью PR #1309).
 * @param read Как достать свежее предложение из отказа. У уровня оно шире (едет состав уходящего),
 *   и прочитанное общим разбором потеряло бы его при пересчёте — молча, поэтому умолчания нет.
 */
export function purgeFailure<O extends PurgeOffer>(
  e: unknown, shown: O, words: PurgeWords, read: (e: unknown) => O | null): PurgeFailure<O>;
export function purgeFailure(
  e: unknown, shown: PurgeOffer, words: PurgeWords = RECORD_WORDS,
  read: (e: unknown) => PurgeOffer | null = purgeOffered,
): PurgeFailure {
  const fresh = read(e);
  if (fresh?.allowed)
    return {
      kind: 'recount', offer: fresh,
      // Итог тот же — значит, отказ о другом: показываем слова сервера, а не молча стираем ввод.
      note: fresh.references !== shown.references
        ? changedNote(shown.references, fresh.references)
        : `${apiError(e, 'Сервер не принял подтверждение.')} Сверьте разбивку и введите число заново.`,
    };
  if (isConflict(e)) return { kind: 'final', text: apiError(e, words.refused) };
  const status = (e as { response?: { status?: number } })?.response?.status;
  if (status === 404) return { kind: 'final', text: words.gone };
  // Прочие 4xx — ответ по существу: ничего не удалено, и повтор даст то же (право отозвали, пока
  // окно было открыто, — 403). «Исход неизвестен» здесь был бы отказом, переодетым в неизвестность.
  if (status !== undefined && status >= 400 && status < 500)
    return { kind: 'final', text: `${apiError(e, words.refused)} Ничего не удалено.` };
  return { kind: 'retry', text: `${apiError(e, 'Ответ от сервера не получен.')} ${words.unknown}` };
}

/** Слова успеха: что удалено и сколько ссылок потеряно. */
export function purgedToast(name: string, references: number): string {
  return `Запись «${name}» удалена. Потеряно ссылок: ${references}.`;
}
