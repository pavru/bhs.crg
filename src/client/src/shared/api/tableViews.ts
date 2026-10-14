import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiClient } from './client';
import type { TableSort } from './tables';

/**
 * Сохранённые представления таблицы (ТЗ CORE-33; задача G3a, issue #1095): свои и общие. Настройка
 * хранится целиком, а не отличием от основы: `basedOn` — только «из чего сделано».
 */

/** Настройка представления. `filter` — строка с JSON дерева условий, как и отбор запроса к таблице. */
export interface SavedViewState {
  /** null — все колонки таблицы в её порядке. */
  columns?: string[] | null;
  filter?: string | null;
  sort?: TableSort[] | null;
  totals?: { column: string; aggregate: string }[] | null;
  pinned: number;
}

export interface SavedTableView {
  id: string;
  title: string;
  shared: boolean;
  /** Спрашивающий — автор. */
  mine: boolean;
  /** Имя автора; null — учётной записи автора больше нет. */
  author: string | null;
  /** Правка и удаление открыты: своё личное — автору, общее — держателю права на общие. */
  canEdit: boolean;
  basedOn: string | null;
  state: SavedViewState;
  /** Версия — её называет правка и удаление заголовком `If-Match`. */
  version: string;
  /**
   * Почему `state.filter` НЕ отдан: отбор автора стоит на колонке, закрытой читающему.
   * ⚠️ Пустой отбор под этим полем — не «отбора нет»: представление открыто БЕЗ отбора автора, и
   * сохранять его поверх нельзя — правка заменяет настройку целиком и стёрла бы условия автора.
   */
  withheld?: string | null;
}

export interface SaveTableViewBody {
  title: string;
  shared: boolean;
  state: SavedViewState;
  basedOn: string | null;
}

const url = (address: string) => `/tables/${encodeURIComponent(address)}/views`;
const key = (address: string) => ['tables', address, 'views'] as const;

/** Свои и общие представления таблицы. Отказ не повторяем: 403 и 404 — ответ, а не сбой сети. */
export function useSavedTableViews(address: string, enabled = true) {
  return useQuery({
    queryKey: key(address),
    queryFn: () => apiClient.get<SavedTableView[]>(url(address)).then(r => r.data),
    enabled,
    retry: false,
  });
}

/**
 * Завести представление (`saved` не назван) или заменить лежащее. Правка называет версию, с которой
 * экран представление прочёл: без неё сервер отказывает, с устаревшей — отвечает 409.
 *
 * Ответ кладётся в список СРАЗУ, а не перечитыванием: экран отсчитывает адрес от настройки
 * представления, и до прихода свежего списка только что сохранённое читалось бы как изменённое.
 */
export function useSaveTableView(address: string) {
  const client = useQueryClient();
  return useMutation({
    mutationFn: ({ saved, body }: { saved?: Pick<SavedTableView, 'id' | 'version'>; body: SaveTableViewBody }) =>
      (saved
        ? apiClient.put<SavedTableView>(`${url(address)}/${saved.id}`, body, { headers: { 'If-Match': saved.version } })
        : apiClient.post<SavedTableView>(url(address), body)).then(r => r.data),
    onSuccess: view => {
      client.setQueryData<SavedTableView[]>(key(address), list =>
        [...(list ?? []).filter(v => v.id !== view.id), view].sort((a, b) => a.title.localeCompare(b.title, 'ru')));
    },
    // Отказ — повод перечитать: представление могли изменить, удалить или вернуть автору.
    onError: () => void client.invalidateQueries({ queryKey: key(address) }),
  });
}

export function useDeleteTableView(address: string) {
  const client = useQueryClient();
  return useMutation({
    mutationFn: (saved: Pick<SavedTableView, 'id' | 'version'>) =>
      apiClient.delete(`${url(address)}/${saved.id}`, { headers: { 'If-Match': saved.version } }).then(() => saved.id),
    onSuccess: id => {
      client.setQueryData<SavedTableView[]>(key(address), list => (list ?? []).filter(v => v.id !== id));
    },
    onError: () => void client.invalidateQueries({ queryKey: key(address) }),
  });
}
