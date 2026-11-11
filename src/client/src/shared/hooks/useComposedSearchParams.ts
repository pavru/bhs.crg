import { useCallback, useRef } from 'react';
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
 * <p>⚠️ Складываются только записи через `update`: правка мимо него (своим `setSearchParams`) снова
 * читает снимок.</p>
 */
export function useComposedSearchParams(): [URLSearchParams, SearchUpdate] {
  const [params, setParams] = useSearchParams();
  const rendered = params.toString();
  // Что записано поверх какого отрисованного адреса. Отрисовка того же адреса записанного не
  // сбрасывает: она может быть срочной отрисовкой ДО перехода.
  const pending = useRef<{ base: string; latest: string } | null>(null);

  const update = useCallback<SearchUpdate>((change, options) => {
    const start = pending.current?.base === rendered ? pending.current.latest : rendered;
    const next = new URLSearchParams(start);
    change(next);
    const text = next.toString();
    if (text === start) return;
    pending.current = { base: rendered, latest: text };
    setParams(next, options);
  }, [rendered, setParams]);

  return [params, update];
}
