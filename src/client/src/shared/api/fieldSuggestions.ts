import { keepPreviousData, useQuery } from '@tanstack/react-query';
import { apiClient } from './client';
import type { CatalogScope } from './types';

/**
 * Подсказки поля по ранее введённому (эпик #1527, адрес — #1530, контракт D10 зонтика `suggest-00-epic`).
 *
 * <p>Адрес отвечает различимо, и поле обязано это различие сохранить: не подсказывается — 200 и пусто,
 * таймаут — 503, нет прав — 403. Здесь любой отказ — ошибка запроса, а не пустой список: поле решает
 * само, что показывать (по правилу UI-3 — ничего), но «никто ничего не вводил» и «не успели» в данных
 * не смешиваются.</p>
 */

/** Чьё поле: тип-владелец и ключ, плюс уровень, по которому сервер находит «свою стройку». */
export interface SuggestSource {
  type: string;
  field: string;
  scope?: CatalogScope;
  scopeId?: string;
}

/** Пункт подсказки. `here` — значение встречалось на своей стройке; порядок показа задаёт сервер. */
export interface FieldSuggestion {
  value: string;
  here: boolean;
}

/** Повтор набора в пределах минуты берётся из кэша, без запроса (UI-5). */
export const SUGGESTIONS_STALE_MS = 60_000;

export const fieldSuggestionsKey = (source: SuggestSource, q: string) =>
  ['field-suggestions', source.type, source.field, source.scope ?? null, source.scopeId ?? null, q] as const;

/**
 * Подсказки по набранному `q`; `q === null` — спрашивать не о чем (поле без фокуса, ничего не набрано).
 * Задержку после последней клавиши держит поле: в ключ попадает уже отстоявшееся `q`.
 *
 * <p>Прошлый ответ придерживается, пока едет новый (`keepPreviousData`): иначе список мигал бы на каждую
 * клавишу. Смена `q` отменяет прошлый запрос через `signal`.</p>
 */
export function useFieldSuggestions(source: SuggestSource | null, q: string | null) {
  const asked = source !== null && q !== null;
  return useQuery<FieldSuggestion[]>({
    queryKey: asked ? fieldSuggestionsKey(source, q) : ['field-suggestions', 'none'],
    queryFn: ({ signal }) => apiClient
      .get<{ items: FieldSuggestion[] }>('/field-suggestions', {
        params: {
          type: source!.type, field: source!.field, q: q!,
          scope: source!.scope, scopeId: source!.scopeId,
        },
        signal,
      })
      .then(r => r.data.items),
    enabled: asked,
    staleTime: SUGGESTIONS_STALE_MS,
    placeholderData: keepPreviousData,
    // 503 — сервер не успел; повтор той же буквы только догонял бы следующую.
    retry: false,
  });
}
