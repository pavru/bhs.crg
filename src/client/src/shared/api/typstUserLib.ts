import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { apiClient } from './client';

const QK = ['typst-userlib'] as const;

export function useTypstUserLib() {
  return useQuery({
    queryKey: QK,
    queryFn: async () => {
      const r = await apiClient.get<{ content: string }>('/typst-userlib');
      return r.data.content;
    },
  });
}

/** Системная Typst-библиотека (issue #344) — хардкод, только чтение. */
export function useSystemTypstLib() {
  return useQuery({
    queryKey: ['typst-systemlib'],
    queryFn: async () => {
      const r = await apiClient.get<{ content: string }>('/templates/systemlib');
      return r.data.content;
    },
    staleTime: Infinity, // константа — не протухает
  });
}

export function useSaveTypstUserLib() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: async (content: string) => {
      const r = await apiClient.put<{ content: string }>('/typst-userlib', { content });
      return r.data.content;
    },
    onSuccess: () => qc.invalidateQueries({ queryKey: QK }),
  });
}
