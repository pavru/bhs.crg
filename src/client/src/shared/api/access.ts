import { useQuery } from '@tanstack/react-query';
import { apiClient } from './client';

/**
 * Что доступно текущему пользователю (ТЗ AUTH-14).
 *
 * Единственный источник для навигации. Название роли здесь не участвует и не должно: роль — это
 * имя набора прав, а не сам набор, и любое решение, принятое по имени, расходится с сервером в тот
 * день, когда состав роли меняют. Именно так система и жила до issue #952 — клиент декодировал
 * роль из токена и по ней показывал раздел настроек.
 */
export interface ModuleAccess {
  code: string;
  title: string;
  /** Есть ли у пользователя хотя бы одно право этого модуля. Считает сервер (AUTH-8.1). */
  available: boolean;
}

export interface AccessInfo {
  permissions: string[];
  modules: ModuleAccess[];
}

export const ACCESS_KEY = ['account', 'access'] as const;

/**
 * ⚠️ Ответ живёт недолго НАМЕРЕННО. Права действуют немедленно (AUTH-7): администратор снял
 * право — следующий запрос пользователя уже отвечает отказом. Меню, нарисованное по устаревшему
 * ответу, показывает пункты, которые отвечают «нельзя», — то есть ровно то, от чего уходили,
 * убирая роль из токена.
 */
export function useAccess() {
  return useQuery({
    queryKey: ACCESS_KEY,
    queryFn: () => apiClient.get<AccessInfo>('/account/access').then(r => r.data),
    staleTime: 30_000,
  });
}

/** Пустой доступ: пока ответ не получен, не показываем НИЧЕГО лишнего. */
export const NO_ACCESS: AccessInfo = { permissions: [], modules: [] };

export function hasPermission(access: AccessInfo, code: string): boolean {
  return access.permissions.includes(code);
}

export function hasModule(access: AccessInfo, code: string): boolean {
  return access.modules.some(m => m.code === code && m.available);
}
