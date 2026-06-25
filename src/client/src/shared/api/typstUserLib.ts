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
