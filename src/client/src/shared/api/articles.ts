import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiClient } from './client';
import { INVOICES_KEY } from './invoices';

/**
 * Статьи вне строек — «Склад», «Общие расходы» (задача F3, issue #1087, ТЗ COST-10.1): цель части разноски,
 * которая не стройка. Справочник модуля счетов; ведёт его тот, у кого `costs.articles.edit`.
 */
export interface CostsArticle {
  id: string;
  name: string;
  /**
   * В архиве (issue #1185): на выбор не предлагается, а там, где на неё уже разнесено, остаётся.
   * Список один на справочник, названия и выбор — архивные в нём есть, и отбирает их тот, кто
   * показывает выбор (`PlaceSelect`); новую часть на архивную статью сервер не запишет.
   */
  archived: boolean;
}

const KEY = ['costs-articles'] as const;

export function useCostsArticles() {
  return useQuery({
    queryKey: KEY,
    queryFn: () => apiClient.get<CostsArticle[]>('/costs/articles').then(r => r.data),
  });
}

/** Завести (без `id`) или переименовать статью. */
export function useSaveArticle() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ id, name }: { id?: string; name: string }) => (id
      ? apiClient.put<CostsArticle>(`/costs/articles/${id}`, { name })
      : apiClient.post<CostsArticle>('/costs/articles', { name })).then(r => r.data),
    onSuccess: () => {
      void qc.invalidateQueries({ queryKey: KEY });
      // Название статьи приезжает в разноске счёта — открытые счета иначе показывали бы прежнее.
      void qc.invalidateQueries({ queryKey: INVOICES_KEY });
    },
  });
}

/** Отправить статью в архив или вернуть. Свой адрес модуля и своё право — `costs.articles.edit`. */
export function useSetArticleArchive() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ id, archived }: { id: string; archived: boolean }) =>
      apiClient.post<CostsArticle>(`/costs/articles/${id}/${archived ? 'archive' : 'unarchive'}`).then(r => r.data),
    onSuccess: () => void qc.invalidateQueries({ queryKey: KEY }),
  });
}

/** Убрать статью. Занятую сервер не уберёт — отказ назовёт, сколько на неё разнесено. */
export function useDeleteArticle() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (id: string) => apiClient.delete(`/costs/articles/${id}`),
    onSuccess: () => void qc.invalidateQueries({ queryKey: KEY }),
  });
}
