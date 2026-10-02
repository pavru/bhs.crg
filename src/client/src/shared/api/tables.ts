import { keepPreviousData, useQuery } from '@tanstack/react-query';
import { apiClient } from './client';
import type { FilterNode } from './types';
import { cleanFilterNode } from './datasetHelpers';
import type { FilterColumn } from '@/shared/filter/rowFilterModel';

/**
 * Таблицы модулей — экрану (ТЗ CORE-33; задачи G1d и G1e, issue #1091, #1092). Первый потребитель
 * адресов `/api/tables` в клиенте: до него их читали только тесты сервера и набор данных.
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
  /** Код права, которого не хватило, — у колонки с причиной `no-right`. */
  requires: string | null;
}

/**
 * Готовое представление таблицы — её настройка, которую поставляет модуль (ТЗ CORE-33, COST-20.1;
 * задача G4, issue #1097): «Реестр счетов». Условий отбора в нём нет — их ставит человек; `filters`
 * называет колонки, по которым экран отбор ПРЕДЛАГАЕТ.
 */
export interface TablePreset {
  /** Код представления — им оно названо в адресе экрана: `/tables/costs.invoices/registry`. */
  code: string;
  title: string;
  /** Колонки в порядке показа. */
  columns: string[];
  sort: TableSort[];
  totals: { column: string; aggregate: string }[];
  /** Сколько первых колонок закреплено слева. */
  pinned: number;
  /** Колонки, по которым отбор предлагается готовыми местами под условие. */
  filters: string[];
}

/** Таблица без строк: что она такое и ВСЕ её колонки — в том числе не показанные. */
export interface TableDeclaration {
  address: string;
  title: string;
  grain: string;
  boundary: string;
  columns: TableColumn[];
  /** Состояние таблицы целиком: `module-off` либо null. */
  state: string | null;
  /** Готовые представления таблицы; у выключенного модуля — пусто. */
  views?: TablePreset[];
}

/** Итог по колонке — по всему отбору, а не по странице. */
export interface TableTotal {
  /** Сколько значений учтено. */
  count: number;
  /** Сколько значений НЕ учтено: в клетке лежит не то, что обещает вид колонки. */
  skipped: number;
  /** Почему не учтены: «не число», «не дата». */
  skippedReason: string | null;
  sum: number | null;
  average: number | null;
  min: number | string | null;
  max: number | string | null;
}

export interface TableData extends TableDeclaration {
  /** Запрошенные колонки в запрошенном порядке; исчезнувшая из типа — с причиной `removed`. */
  rows: Record<string, unknown>[];
  /** Сколько строк в отборе ВСЕГО, а не на странице. */
  count: number;
  offset: number;
  limit: number | null;
  totals: Record<string, TableTotal> | null;
  /** Ключи строк — по одному на строку, в том же порядке. null — таблица ключей не называет. */
  keys: string[] | null;
}

export interface TableSort {
  column: string;
  descending: boolean;
}

export interface TableQuery {
  /**
   * Отбор — дерево условий. Строкой — то, что стояло в адресе страницы и деревом не разобралось:
   * оно уходит серверу КАК ЕСТЬ, и отказывает он. Выбросить такой отбор на клиенте значило бы
   * показать все строки под видом отобранных.
   */
  filter: FilterNode | string | null;
  /** Колонки и их порядок; null — все колонки таблицы. */
  columns?: string[] | null;
  sort?: TableSort[];
  /** Колонки, по которым нужен итог. */
  totals?: string[];
  offset?: number;
  limit?: number;
  /** Ключ одной строки: только она, и только если она в отборе. */
  row?: string | null;
}

/** Сколько строк экран просит за раз. Остальное он называет числом, а не обрезает молча. */
export const TABLE_PAGE = 200;

/**
 * Запрос длиннее этого уходит телом. Строка запроса упирается в потолок длины, а ставит его сервер
 * перед приложением (8 КБ) — отказ пришёл бы без причины.
 *
 * ⚠️ Мерим в БАЙТАХ адреса, а не в знаках: кириллица в адресе стоит шести байт за знак, и «полторы
 * тысячи знаков» — это до девяти тысяч байт. Счёт знаками пропускал запрос, который до приложения
 * не доходил.
 */
const QUERY_IN_URL_LIMIT = 4000;

/** Тело запроса к `/query` — теми же именами, что читает сервер. */
export interface TableQueryBody {
  filter?: string;
  columns?: string[];
  sort?: TableSort[];
  totals?: string[];
  offset?: number;
  limit: number;
  row?: string;
}

export type TableRequest =
  | { method: 'get'; url: string; params: Record<string, string | number> }
  | { method: 'post'; url: string; body: TableQueryBody };

/**
 * Запрос к таблице из состояния экрана. Чистая и отдельная — чтобы «снятие чипа меняет запрос, а не
 * только вид» проверялось тестом, а не глазами: отбора нет — параметра `filter` в запросе нет вовсе.
 */
export function tableRequest(address: string, query: TableQuery): TableRequest {
  const url = `/tables/${encodeURIComponent(address)}`;
  const body: TableQueryBody = { limit: query.limit ?? TABLE_PAGE };

  const filter = filterText(query.filter);
  if (filter) body.filter = filter;
  if (query.columns?.length) body.columns = query.columns;
  if (query.sort?.length) body.sort = query.sort;
  if (query.totals?.length) body.totals = query.totals;
  if (query.offset) body.offset = query.offset;
  if (query.row) body.row = query.row;

  const params: Record<string, string | number> = { limit: body.limit };
  if (body.filter) params.filter = body.filter;
  if (body.columns) params.columns = body.columns.join(',');
  if (body.sort) params.sort = body.sort.map(s => `${s.column}:${s.descending ? 'desc' : 'asc'}`).join(',');
  if (body.totals) params.totals = body.totals.join(',');
  if (body.offset) params.offset = body.offset;
  if (body.row) params.row = body.row;

  const bytes = Object.values(params).reduce<number>((sum, v) => sum + encodeURIComponent(String(v)).length, 0);
  return bytes > QUERY_IN_URL_LIMIT
    ? { method: 'post', url: `${url}/query`, body }
    : { method: 'get', url, params };
}

function filterText(filter: FilterNode | string | null): string | null {
  if (filter === null) return null;
  if (typeof filter === 'string') return filter.trim() ? filter : null;
  const cleaned = cleanFilterNode(filter);
  return cleaned ? JSON.stringify(cleaned) : null;
}

/**
 * Таблица модуля под отбором. Запрос входит в ключ целиком: без этого отобранной таблице достался бы
 * кэш полной. Прежние строки остаются на экране, пока идёт новый запрос, — иначе таблица мигала бы
 * пустотой на каждый чип.
 */
export function useTable(address: string, query: TableQuery, enabled = true) {
  const request = tableRequest(address, query);
  return useQuery({
    queryKey: ['tables', address, 'rows', request],
    queryFn: () => (request.method === 'get'
      ? apiClient.get<TableData>(request.url, { params: request.params })
      : apiClient.post<TableData>(request.url, request.body)).then(r => r.data),
    placeholderData: keepPreviousData,
    enabled,
    // Отказ отбора (409) — ответ, а не сбой сети: повторять его незачем.
    retry: false,
  });
}

/**
 * Описание таблицы — отдельным запросом от строк. Выбор колонок и чипы отбора обязаны знать ВСЕ
 * колонки, а не показанные, и обязаны остаться на экране, когда отбор отказал: отказ строк приходит
 * без единой колонки, и исправить негодное условие было бы нечем.
 */
export function useTableDeclaration(address: string) {
  return useQuery({
    queryKey: ['tables', address, 'columns'],
    queryFn: () => apiClient.get<TableDeclaration>(`/tables/${encodeURIComponent(address)}/columns`).then(r => r.data),
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
