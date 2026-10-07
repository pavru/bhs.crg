import type { PurgeHolder, PurgeOffer } from '@/shared/api/recordPurge';

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
  const none = 'это данные модуля, которого нет в сборке, или колонки, о которой модуль не объявил. ' +
    'Единственный след — запись в журнале.';
  const lost = 'Ссылки останутся в данных модуля и будут вести в пустоту.';
  const shown = 'После включения модуль покажет их в своих отборах; в документах закрытого периода ' +
    'исправить их будет нельзя.';
  return [
    'Запись будет удалена совсем. Вернуть её можно только из резервной копии.',
    offer.untraceable >= offer.references ? `${lost} Потом их не покажет никто: ${none}` : `${lost} ${shown}`,
    ...(offer.untraceable > 0 && offer.untraceable < offer.references
      ? [`Из них ${offer.untraceable} не покажет никто: ${none}`] : []),
    'Действие записывается в журнал: кто, какую запись, сколько ссылок потеряно.',
  ];
}

/** Что изменилось между показанным и нынешним: человек подтверждал одно число, а стало другое. */
export function changedNote(before: number, now: number): string {
  return `Пока диалог был открыт, число ссылок изменилось: было ${before}, стало ${now}. ` +
    'Ничего не удалено — сверьте разбивку и введите новое число.';
}

/** Слова успеха: что удалено и сколько ссылок потеряно. */
export function purgedToast(name: string, references: number): string {
  return `Запись «${name}» удалена. Потеряно ссылок: ${references}.`;
}
