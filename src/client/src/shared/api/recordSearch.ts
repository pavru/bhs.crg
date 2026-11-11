import { useQuery } from '@tanstack/react-query';
import { useDebouncedValue } from '@/shared/hooks/useDebouncedValue';
import { apiClient } from './client';

/**
 * Поиск записей для командной палитры (shell-05, [UI-30]): `GET /api/search?q=&group=`. Сервер ищет в правах
 * спрашивающего — группы, закрытой правами, в ответе нет вовсе; отказ одной группы приходит `refused` у неё.
 */

/** Найденная запись — карточка, как её рисует палитра (М-4). */
export interface RecordSearchHit {
  id: string;
  title: string;
  /** Пояснение: назначение счёта, внешний код стройки. */
  detail?: string | null;
  /** Место записи — стройка набора данных. */
  place?: string | null;
  /** Сумма счёта; форматирует клиент. */
  amount?: number | null;
  /** Уровень записи — у той, чей экран свой на каждом уровне (набор данных); адрес строит `hitRoute`. */
  level?: RecordLevel | null;
}

/** Где лежит запись: уровень, его запись и её стройка — с неё начинается адрес раздела и комплекта. */
export interface RecordLevel {
  scope: 'System' | 'Construction' | 'Section' | 'Set';
  scopeId?: string | null;
  constructionId?: string | null;
}

export interface RecordSearchGroup {
  code: string;
  title: string;
  module: string;
  /** Код типа записи — по нему клиент находит экран записи (`RECORD_ROUTES`). */
  recordType: string;
  items: RecordSearchHit[];
  /** Записей больше, чем показано. */
  more: boolean;
  /** Группа не ответила — причина словами. Отказ ≠ «ничего не найдено». */
  refused?: string | null;
}

/**
 * Слово короче — сервер не спрашивают: из одного знака подходит почти всё, и «а б» не уже, чем «а» (тот же
 * порог, что у адреса: `RecordSearchEndpoints.MinLength`).
 */
export const SEARCH_MIN_LENGTH = 2;
/** Длиннее — сервер отказывает (`RecordSearchEndpoints.MaxLength`): вставленный абзац не ищут. */
export const SEARCH_MAX_LENGTH = 200;
/** Пауза после последней клавиши: запрос на каждую букву догонял бы следующую. */
export const SEARCH_DELAY_MS = 250;

/** Знаки по краям слова — не часть того, что ищут: так режет слова и сервер (`RecordSearchText.Words`). */
const EDGE_MARKS = /^[^\p{L}\p{N}]+|[^\p{L}\p{N}]+$/gu;

/** Запрос как его видит сервер: без краёв и с одиночными пробелами. Одно место — у порога и у предела. */
const normalized = (query: string) => query.trim().replace(/\s+/g, ' ');

/**
 * Что спрашивать у сервера: нормализованный запрос, если в нём есть слово от порога и он не длиннее
 * предела, иначе `null`. Слово меряется без знаков по краям — «№ ·» не запрос.
 */
export function searchQuery(query: string): string | null {
  const q = normalized(query);
  if (q.length > SEARCH_MAX_LENGTH) return null;
  return q.split(' ').some(w => w.replace(EDGE_MARKS, '').length >= SEARCH_MIN_LENGTH) ? q : null;
}

export const recordSearchKey = (q: string) => ['record-search', q] as const;

export interface RecordSearchState {
  /** Ответ на ТЕКУЩИЙ запрос; `undefined` — его ещё нет (печатают, едет, спрашивать не о чем). */
  groups: RecordSearchGroup[] | undefined;
  /** Поиск по данным целиком не ответил — отказ, а не пусто. */
  failed: boolean;
  /** Ответ едет. */
  pending: boolean;
  /** Поиск включён, а запрос длиннее предела: записи не ищутся — сказать словами, а не «Ничего не найдено». */
  tooLong: boolean;
}

/**
 * Записи по строке палитры; `enabled: false` — по данным не искать (выбран чип действий или разделов, у
 * человека нет ни одной группы данных).
 *
 * <p>Ключ — запрос: группы отбирает палитра из ответа «везде» — сервер отдаёт на группу те же записи, и
 * чип или Tab не повод спрашивать снова (ревью #1915). Ответ на устаревший запрос отброшен: пока запрос не
 * отстоялся, ответ на прежний не показывается вовсе — иначе на «418» висели бы счета, найденные по «41»
 * (сценарий «Найденное раньше ответа»). Смена ключа отменяет прошлый запрос через `signal`.</p>
 *
 * <p>Ответ не считается свежим и секунды: счёт, заведённый между двумя открытиями палитры, обязан
 * найтись — прежний ответ показывается, пока едет новый.</p>
 */
export function useRecordSearch(query: string, enabled = true): RecordSearchState {
  const wanted = enabled ? searchQuery(query) : null;
  const settled = useDebouncedValue(wanted, SEARCH_DELAY_MS);
  const asked = settled !== null && settled === wanted;
  const result = useQuery<RecordSearchGroup[]>({
    queryKey: asked ? recordSearchKey(settled) : ['record-search', 'none'],
    queryFn: ({ signal }) => apiClient
      .get<{ groups: RecordSearchGroup[] }>('/search', { params: { q: settled }, signal })
      .then(r => r.data.groups),
    enabled: asked,
    subscribed: asked,
    staleTime: 0,
    // Отказ сказан словами сразу: повтор держал бы палитру немой ещё на секунды.
    retry: false,
  });
  return {
    groups: asked ? result.data : undefined,
    failed: asked && result.isError,
    pending: wanted !== null && (!asked || result.isPending),
    tooLong: enabled && normalized(query).length > SEARCH_MAX_LENGTH,
  };
}
