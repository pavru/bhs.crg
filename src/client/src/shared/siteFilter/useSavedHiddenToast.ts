import { useCallback, useEffect, useLayoutEffect, useRef } from 'react';
import { useQueryClient, type QueryClient } from '@tanstack/react-query';
import { useToast } from '@/shared/ui/Toast';

/**
 * Тост после СВОЕЙ записи, которая увела открытый документ из списка под «Избранными стройками»
 * (рейл счетов #1221, накладные #1222). Одна логика на оба экрана (ревью PR #1335).
 *
 * <p>Решение — по прежнему и новому виду документа, а не по списку: список перечитывается позже, и
 * чужая правка, пришедшая перечитыванием, «Сохранено» не скажет. Только об ОТКРЫТОМ документе (ревью PR
 * #1334): ответ приходит и после ухода на другой, и «скрыт» над его формой читалось бы как о нём. Только
 * когда увела именно эта запись: до неё документ был виден, после — скрыт.</p>
 *
 * @param subscribe подписка на свои записи документа (`onInvoiceWritten`, `onWaybillWritten`) — функция
 *     модуля, не замыкание: иначе подписка пересоздавалась бы на каждый кадр
 * @param hides скрыт ли документ с таким видом — читается в момент записи, а не при подписке
 * @returns решение тоста — для проверок
 */
export function useSavedHiddenToast<V extends { id: string }>(
  subscribe: (qc: QueryClient, listener: (before: V | undefined, after: V) => void) => () => void,
  { selected, hides, text, showAll }: {
    selected: string | null; hides: (view: V) => boolean; text: string; showAll: () => void;
  },
) {
  const toast = useToast();
  const qc = useQueryClient();
  const now = useRef({ selected, hides, showAll });
  useLayoutEffect(() => { now.current = { selected, hides, showAll }; });
  const onSaved = useCallback((before: V | undefined, after: V) => {
    const at = now.current;
    if (!before || after.id !== at.selected || !at.hides(after) || at.hides(before)) return;
    toast.info(text, { action: { label: 'Показать все', onClick: () => at.showAll() } });
  }, [toast, text]);
  useEffect(() => subscribe(qc, onSaved), [subscribe, qc, onSaved]);
  return onSaved;
}
