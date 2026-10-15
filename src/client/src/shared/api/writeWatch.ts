import type { QueryClient } from '@tanstack/react-query';

/**
 * Свои записи документа, видимые через кэш мутаций (issue #1221, #1222): разноска счёта или смена
 * получателя накладной уводит документ из списка под «Избранными стройками», и экран говорит об этом.
 *
 * <p>Один приём на все документы (ревью PR #1335): у счёта и накладной лежали две копии, и правка одной
 * — «только об открытом» после ревью PR #1334 — переносилась бы в другую руками. Пишущий хук документа
 * ставит признак (`writeMeta`) и кладёт прежний вид в контекст мутации (`WriteContext`); подписчик
 * получает прежний и новый вид. Чужая правка, пришедшая перечитыванием, мутацией не является и сюда не
 * попадает.</p>
 */

/** Контекст записи: вид документа ДО неё, из кэша; `undefined` — в кэше его не было. */
export interface WriteContext<V> { before: V | undefined }

/** `meta` пишущей мутации: по признаку её узнаёт `onWritten`. */
export const writeMeta = (flag: string) => ({ [flag]: true });

/** @returns отписка */
export function onWritten<V>(
  qc: QueryClient, flag: string, listener: (before: V | undefined, after: V) => void,
): () => void {
  return qc.getMutationCache().subscribe(event => {
    if (event.type !== 'updated' || event.action.type !== 'success' || !event.mutation.meta?.[flag]) return;
    const context = event.mutation.state.context as WriteContext<V> | undefined;
    listener(context?.before, event.action.data as V);
  });
}
