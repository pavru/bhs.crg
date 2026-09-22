import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiClient } from './client';
import type { RoleRef } from './users';
// Форма та же, что у редактора ролей, и НАРОЧНО одна: право с двумя описаниями рано или поздно
// покажет в профиле не то, что в редакторе. Отсюда берётся только тип — адрес свой, личный.
import type { PermissionGroup } from './roles';

const QK = 'account';

export interface Account {
  email: string;
  displayName: string;
  /**
   * ВСЕ роли, по названию (issue #984). Раньше сервер отдавал первую, а человеку без ролей
   * подставлял «Инженера ИД» — профиль называл роль, которой нет, ровно тому, у кого нет доступа.
   */
  roles: RoleRef[];
  emailConfirmed: boolean;
  /** Аватар профиля (issue #245) — data-URI уменьшённой картинки, null = нет. */
  avatar?: string | null;
}

/** Профиль текущего пользователя (issue #148). */
export function useAccount() {
  return useQuery<Account>({
    queryKey: [QK],
    queryFn: () => apiClient.get('/account').then(r => r.data),
  });
}

/**
 * Мои права словами (issue #954, ТЗ AUTH-16.6).
 *
 * Редакторский `/roles/permissions` закрыт правом «управлять пользователями» — о СВОИХ правах он
 * человеку не расскажет. Отвечает на единственный вопрос, который возникает после отказа: «почему
 * у меня нет этого раздела».
 *
 * Справочник меняется правкой кода и роли — не в этом сеансе, поэтому `staleTime` большой; но НЕ
 * бесконечный: администратор может снять право сейчас, и тогда профиль обязан перестать его
 * обещать.
 */
export function useMyPermissions() {
  return useQuery<PermissionGroup[]>({
    queryKey: [QK, 'permissions'],
    queryFn: () => apiClient.get('/account/permissions').then(r => r.data),
    staleTime: 5 * 60_000,
  });
}

export function useUpdateAccount() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (dto: { displayName: string }) =>
      apiClient.put<Account>('/account', dto).then(r => r.data),
    onSuccess: (data) => qc.setQueryData([QK], data),
  });
}

/** Задать/удалить аватар профиля (issue #245). `avatar` = data-URI или null для удаления. */
export function useUpdateAvatar() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (avatar: string | null) =>
      apiClient.put<Account>('/account/avatar', { avatar }).then(r => r.data),
    onSuccess: (data) => qc.setQueryData([QK], data),
  });
}

export function useChangeMyPassword() {
  return useMutation({
    mutationFn: (dto: { currentPassword: string; newPassword: string }) =>
      apiClient.post<{ accessToken?: string; refreshToken?: string }>('/account/change-password', dto).then(r => r.data),
  });
}

/** Повторно отправить письмо подтверждения адреса себе (issue #148). */
export function useResendConfirmation() {
  return useMutation({
    mutationFn: () => apiClient.post('/account/resend-confirmation'),
  });
}

/** Запустить смену email: письмо-подтверждение уходит на новый адрес. */
export function useChangeEmail() {
  return useMutation({
    mutationFn: (dto: { newEmail: string; currentPassword: string }) =>
      apiClient.post('/account/change-email', dto),
  });
}
