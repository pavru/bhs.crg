import { keepPreviousData, useQuery } from '@tanstack/react-query';
import { apiClient } from './client';
import type { FilterNode } from './types';
import { cleanFilterNode } from './datasetHelpers';
import type { FilterColumn } from '@/shared/filter/rowFilterModel';

/**
 * Таблицы модулей — экрану (ТЗ CORE-33; задача G1d, issue #1091). Первый потребитель адресов
 * `/api/tables` в клиенте: до него их читали только тесты сервера и набор данных.
 */

/** Колонка таблицы так, как её отдаёт сервер. */
export interface TableColumn {
  key: string;
  label: string;
  /** Вид значения: `text`, `number`, `date`, `boolean`, `list`, `choice`. */
  kind: string;
  /** Операторы отбора, которые сервер у колонки примет. Пусто — по колонке не отбирают. */
  operators: string[];
  system: boolean;
  /** Код причины, по которой колонка пришла без значений (`no-right`, `removed`, `module-off`). */
  unavailable: string | null;
  /** Та же причина словами: «нет права на суммы». */
  reason: string | null;
  dependsOnFilter: boolean;
  /** Что колонка значит под этим отбором: «доля: Комарова 36». */
  note: string | null;
  /** Закрытый перечень значений — у колонки вида `choice`. */
  options: string[] | null;
}

export interface TableData {
  address: string;
  title: string;
  grain: string;
  boundary: string;
  columns: TableColumn[];
  rows: Record<string, unknown>[];
  /** Состояние таблицы целиком: `module-off` либо null. */
  state: string | null;
  /** Сколько строк в отборе ВСЕГО, а не на странице. */
  count: number;
  offset: number;
  limit: number | null;
}

export interface TableQuery {
  filter: FilterNode | null;
  limit?: number;
}

/** Сколько строк экран просит за раз. Остальное он называет числом, а не обрезает молча. */
export const TABLE_PAGE = 200;

/**
 * Отбор длиннее этого уходит телом запроса: дерево условий в адресе упирается в потолок длины
 * строки запроса, а ставит его сервер перед приложением — отказ пришёл бы без причины.
 */
const FILTER_IN_URL_LIMIT = 1500;

export type TableRequest =
  | { method: 'get'; url: string; params: Record<string, string | number> }
  | { method: 'post'; url: string; body: { filter: string; limit: number } };

/**
 * Запрос к таблице из состояния экрана. Чистая и отдельная — чтобы «снятие чипа меняет запрос, а не
 * только вид» проверялось тестом, а не глазами: отбора нет — параметра `filter` в запросе нет вовсе.
 */
export function tableRequest(address: string, query: TableQuery): TableRequest {
  const url = `/tables/${encodeURIComponent(address)}`;
  const limit = query.limit ?? TABLE_PAGE;
  const cleaned = query.filter ? cleanFilterNode(query.filter) : null;
  if (!cleaned) return { method: 'get', url, params: { limit } };

  const filter = JSON.stringify(cleaned);
  return filter.length > FILTER_IN_URL_LIMIT
    ? { method: 'post', url: `${url}/query`, body: { filter, limit } }
    : { method: 'get', url, params: { filter, limit } };
}

/**
 * Таблица модуля под отбором. Отбор входит в ключ запроса: без этого отобранной таблице достался бы
 * кэш полной. Прежние строки остаются на экране, пока идёт новый запрос, — иначе таблица мигала бы
 * пустотой на каждый чип.
 */
export function useTable(address: string, query: TableQuery) {
  const request = tableRequest(address, query);
  return useQuery({
    queryKey: ['tables', address, request],
    queryFn: () => (request.method === 'get'
      ? apiClient.get<TableData>(request.url, { params: request.params })
      : apiClient.post<TableData>(request.url, request.body)).then(r => r.data),
    placeholderData: keepPreviousData,
    // Отказ отбора (409) — ответ, а не сбой сети: повторять его незачем.
    retry: false,
  });
}

/**
 * Колонки таблицы — колонками отбора. Условие стоит на КЛЮЧЕ колонки, человеку показывается
 * заголовок. Колонка, чьё значение зависит от самого отбора, в отбор не идёт — по ней не отбирают
 * (операторов у неё нет); закрытая остаётся с причиной: условие по ней названо, а не спрятано.
 */
export function tableFilterColumns(columns: TableColumn[]): FilterColumn[] {
  return columns.filter(c => !c.dependsOnFilter).map(c => ({
    name: c.key,
    label: c.label,
    kind: c.kind,
    operators: c.operators,
    options: c.options ?? undefined,
    unavailable: c.unavailable ? c.reason ?? c.unavailable : undefined,
  }));
}
