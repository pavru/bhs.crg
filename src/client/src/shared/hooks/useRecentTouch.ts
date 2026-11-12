import { useEffect, useRef } from 'react';
import { apiClient } from '@/shared/api/client';

/**
 * Отметка «открыто» для «Недавних» Главной (shell-08, ТЗ UI-35, CORE-25.3): экран записи зовёт хук с типом
 * и идентификатором ПОКАЗАННОЙ записи — `null`, пока её нет на экране (едет, не найдена, выбора нет).
 *
 * <p>Не чаще раза на открытие: та же запись после перерисовки, перечитывания или правки второй отметки не
 * шлёт. Ответ не ждётся и отказ не показывается — отметка личная и не стоит прерывать ей работу; потеря
 * одной лишь сдвинет порядок «Недавних».</p>
 *
 * <p>Код типа — тот же, что ключ `RECORD_ROUTES` и тип группы поиска на сервере: тип без владельца сервер
 * не принимает. Каждый экран из `RECORD_ROUTES` хук зовёт — сверяет `recordRoutes.test.ts`.</p>
 */
export function useRecentTouch(recordType: string, id: string | null | undefined) {
  const sent = useRef<string | null>(null);
  useEffect(() => {
    if (!id) { sent.current = null; return; }
    const key = `${recordType}:${id}`;
    if (sent.current === key) return;
    sent.current = key;
    apiClient.post('/account/recent', { recordType, id }).catch(() => {});
  }, [recordType, id]);
}
