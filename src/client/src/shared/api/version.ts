import { useQuery } from '@tanstack/react-query';
import { apiClient } from './client';
import { useAuth } from '@/shared/hooks/useAuth';

export interface AppVersion {
  version: string;      // 0.1.0
  commit: string;       // короткий git-sha (может быть пустым, напр. в Docker без .git)
  buildDate: string | null;
}

/**
 * Версия приложения (для отображения в UI). Анонимный эндпоинт — работает и до входа, но
 * анонимному отдаётся только номер версии: git-хеш и дата сборки видны вошедшему.
 *
 * Отсюда состояние входа в ключе: ответ кешируется навсегда (staleTime: Infinity), и с общим
 * ключом ответ, полученный на странице входа, оставался бы в кеше на весь сеанс вкладки — хеш
 * сборки не появился бы до перезагрузки страницы.
 */
export function useAppVersion() {
  const { user } = useAuth();
  return useQuery({
    queryKey: ['app-version', user ? 'signed-in' : 'anonymous'],
    queryFn: () => apiClient.get<AppVersion>('/version').then(r => r.data),
    staleTime: Infinity,
    retry: false,
  });
}
