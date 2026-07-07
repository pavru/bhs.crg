import { useQuery } from '@tanstack/react-query';
import { apiClient } from './client';

export interface AppVersion {
  version: string;      // 0.1.0
  commit: string;       // короткий git-sha (может быть пустым, напр. в Docker без .git)
  buildDate: string | null;
}

/** Версия приложения (для отображения в UI). Анонимный эндпоинт — работает и до входа. */
export function useAppVersion() {
  return useQuery({
    queryKey: ['app-version'],
    queryFn: () => apiClient.get<AppVersion>('/version').then(r => r.data),
    staleTime: Infinity,
    retry: false,
  });
}
