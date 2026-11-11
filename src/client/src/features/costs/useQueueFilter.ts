import { useCallback, useEffect, useRef, useState } from 'react';
import type { InvoiceQueue } from '@/shared/api/invoices';
import type { SearchUpdate } from '@/shared/hooks/useComposedSearchParams';
import { QUEUE_PARAM, parseQueue } from './invoiceQueues';

/** Отбор списка счетов: очередь, «только эта загрузка» или ничего. */
export type InvoiceFilter = InvoiceQueue | 'upload' | null;

/**
 * Отбор списка счетов — ОДНО состояние (ревью PR #1304), и очередь в нём живёт ещё и в адресе
 * (shell-08, ТЗ COST-6.2): карточка Главной ведёт на `/invoices?queue=…`, а обновление страницы отбор не
 * снимает.
 *
 * <p>Адрес → отбор — при входе и при переходе по ссылке очереди на открытом экране. Отбор → адрес — тем же
 * вызовом, заменой записи истории: «Назад» уводит с экрана, а не перебирает чипы. Запись — через
 * `update` страницы (`useComposedSearchParams`): снять отбор и открыть счёт в одном обработчике — две
 * записи, и вторая не стирает первую. «Эта загрузка» в адрес не идёт: она живёт на странице, и
 * пережившая перезагрузку без полосы рядом выглядела бы пропажей счетов.</p>
 *
 * <p>Незнакомое значение — без отбора; адрес при этом не трогаем — его уберёт первый же выбор чипа.</p>
 */
export function useQueueFilter(
  params: URLSearchParams, update: SearchUpdate,
): [InvoiceFilter, (next: InvoiceFilter) => void] {
  const urlQueue = params.get(QUEUE_PARAM);
  const [filter, setFilterState] = useState<InvoiceFilter>(() => parseQueue(urlQueue));

  // Значение адреса, которое отбор уже учёл: своя запись не должна вернуться отбору эхом — иначе
  // «эта загрузка», убравшая очередь из адреса, тут же сменилась бы на «все».
  const seen = useRef(urlQueue);
  useEffect(() => {
    if (urlQueue === seen.current) return;
    seen.current = urlQueue;
    setFilterState(parseQueue(urlQueue));
  }, [urlQueue]);

  const setFilter = useCallback((next: InvoiceFilter) => {
    setFilterState(next);
    const queue = next === 'upload' ? null : next;
    seen.current = queue;
    // Записи нет, если адрес уже такой (`update` молчит): зов из отрисовки — снятие «этой загрузки» —
    // тогда не уходит переходом.
    update(p => { if (queue) p.set(QUEUE_PARAM, queue); else p.delete(QUEUE_PARAM); }, { replace: true });
  }, [update]);

  return [filter, setFilter];
}
