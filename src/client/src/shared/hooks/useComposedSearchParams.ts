import { useCallback, useLayoutEffect, useRef } from 'react';
import { useSearchParams, type NavigateOptions } from 'react-router';

/** Правка параметров адреса — от последнего записанного, а не от отрисованного. */
export type SearchUpdate = (change: (next: URLSearchParams) => void, options?: NavigateOptions) => void;

/**
 * `useSearchParams`, у которого записи подряд складываются (shell-08).
 *
 * <p>Функциональная форма `setSearchParams` строит следующий адрес из параметров ОТРИСОВАННОГО снимка.
 * Две записи в одном обработчике (снять отбор и открыть счёт) читают один снимок, и вторая стирает
 * первую; а переход react-router идёт переходом React, так что срочная отрисовка между ними видит
 * старый адрес. Здесь правка берёт последний ЗАПИСАННЫЙ адрес, пока отрисованный не догнал его.</p>
 *
 * <p>`update` один на всё время жизни и читает ТЕКУЩИЙ отрисованный адрес, а не адрес своей отрисовки:
 * его держат долгоживущие обработчики — «Вернуть» снэкбара, «Вернуться» под отбором очереди. Взяв
 * адрес из замыкания, «открыть счёт X», сделанное после ухода с X, совпало бы с давним «?invoice=X»
 * и перехода не было бы вовсе (живые прогоны #1913).</p>
 *
 * <p>⚠️ Складываются только записи через `update`: правка мимо него (своим `setSearchParams`) снова
 * читает снимок.</p>
 */
export function useComposedSearchParams(): [URLSearchParams, SearchUpdate] {
  const [params, setParams] = useSearchParams();
  const rendered = params.toString();
  const current = useRef({ rendered, setParams });
  // Что записано поверх какого отрисованного адреса. Отрисовка того же адреса записанного не
  // сбрасывает: она может быть срочной отрисовкой ДО перехода.
  const pending = useRef<{ base: string; latest: string } | null>(null);
  // Отрисовка ушла от адреса, поверх которого писали, — записанное догнано или перебито чужим переходом.
  // Без сброса переход, вернувший тот же адрес (ссылка «Счета» на открытом экране, «назад»), заставил бы
  // следующую правку начать с давно закрытого счёта.
  useLayoutEffect(() => {
    current.current = { rendered, setParams };
    if (pending.current && pending.current.base !== rendered) pending.current = null;
  }, [rendered, setParams]);

  const update = useCallback<SearchUpdate>((change, options) => {
    const { rendered: now, setParams: write } = current.current;
    const start = pending.current?.base === now ? pending.current.latest : now;
    const next = new URLSearchParams(start);
    change(next);
    const text = next.toString();
    if (text === start) return;
    pending.current = { base: now, latest: text };
    write(next, options);
  }, []);

  return [params, update];
}
