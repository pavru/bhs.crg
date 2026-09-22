import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiClient } from './client';

/**
 * Предпочтения пользователя, которые хранит СЕРВЕР (issue #953, ТЗ CORE-25.3): тема, язык, а
 * дальше — рабочие пространства, закреплённые разделы, сохранённые представления.
 *
 * Ключа нет в ответе — человек его не выбирал. Это не то же самое, что «сервер не ответил»: в
 * первом случае действует умолчание, во втором показывать нечего, кроме последнего известного.
 */
export type UserSettings = Record<string, string>;

export const USER_SETTINGS_KEY = ['account', 'settings'] as const;

/**
 * ⚠️ Запрос идёт только вошедшему: настройки принадлежат человеку, а на странице входа человека
 * ещё нет. Поэтому `enabled` — не оптимизация, а условие осмысленности.
 */
export function useUserSettings(enabled: boolean) {
  return useQuery({
    queryKey: USER_SETTINGS_KEY,
    queryFn: () => apiClient.get<UserSettings>('/account/settings').then(r => r.data),
    enabled,
    staleTime: 5 * 60_000,
  });
}

/**
 * Записать настройки. Присланные ключи меняются, остальные остаются; null убирает настройку.
 *
 * Ответ сервера кладётся в кэш целиком — это и есть новое состояние, а не «наверное, применилось».
 */
export function useSaveUserSettings() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (patch: Record<string, string | null>) =>
      apiClient.put<UserSettings>('/account/settings', patch).then(r => r.data),
    onSuccess: data => queryClient.setQueryData(USER_SETTINGS_KEY, data),
  });
}

/**
 * Какое значение настройки показывать СЕЙЧАС.
 *
 * Три состояния, и путать их нельзя:
 * <ul>
 *   <li>сервер ещё не ответил — берём зеркало в браузере, последнее известное значение. Ради него
 *       зеркало и живёт: первый кадр не должен мигать чужой темой, пока идёт запрос;</li>
 *   <li>сервер ответил, ключ есть — это выбор человека, он и главный;</li>
 *   <li>сервер ответил, ключа НЕТ — человек ничего не выбирал, значит умолчание.</li>
 * </ul>
 *
 * ⚠️ Третий случай нарочно НЕ откатывается на зеркало, хотя так и просится. Зеркало осталось от
 * того, кто работал на этой машине до нас: вошёл другой человек — и получил бы чужую тему, а его
 * собственный «как в системе» выглядел бы как сбой синхронизации. Зеркало отвечает на вопрос «что
 * было в прошлый раз ЗДЕСЬ», а не «что выбрал этот человек».
 */
export function resolvePreference(
  server: UserSettings | undefined,
  key: string,
  mirror: string | null,
  fallback: string,
): string {
  if (!server) return mirror ?? fallback;
  return server[key] ?? fallback;
}
