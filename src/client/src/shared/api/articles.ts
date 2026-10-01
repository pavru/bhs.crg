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

/** Убрать статью. Занятую сервер не уберёт — отказ назовёт, сколько на неё разнесено. */
export function useDeleteArticle() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (id: string) => apiClient.delete(`/costs/articles/${id}`),
    onSuccess: () => void qc.invalidateQueries({ queryKey: KEY }),
  });
}
