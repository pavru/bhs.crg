import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiClient } from './client';
import type { UserRole } from '@/shared/hooks/useAuth';

const QK = 'account';

export interface Account {
  email: string;
  displayName: string;
  role: UserRole;
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
