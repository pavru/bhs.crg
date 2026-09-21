import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiClient } from './client';

const QK = 'users';

/** Ссылка на роль: имя — чтобы назначить, название — чтобы прочитать (issue #951). */
export interface RoleRef {
  name: string;
  title: string;
}

export interface AppUser {
  id: string;
  email: string;
  displayName: string;
  /**
   * ВСЕ роли человека (ТЗ AUTH-3, issue #984), упорядоченные по названию. Пустой список —
   * законное состояние: доступ отозван, учётная запись цела.
   */
  roles: RoleRef[];
}

export function useListUsers(enabled = true) {
  return useQuery<AppUser[]>({
    queryKey: [QK],
    queryFn: () => apiClient.get('/users').then(r => r.data),
    enabled,
  });
}

export function useCreateUser() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (dto: { email: string; displayName: string; password: string; roles: string[] }) =>
      apiClient.post<AppUser>('/users', dto).then(r => r.data),
    onSuccess: () => qc.invalidateQueries({ queryKey: [QK] }),
  });
}

/**
 * Назначить роли. Адрес ОДИН и принимает список (issue #984): одиночный `/role` убран, а не
 * оставлен рядом — два пути назначения разошлись бы в том, снимают ли они прочие роли.
 *
 * Профиль сбрасываем тоже: администратор вправе поменять роли и себе, а подпись под его именем
 * в боковой панели берётся из `/api/account`.
 */
export function useChangeUserRoles() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ id, roles }: { id: string; roles: string[] }) =>
      apiClient.put<AppUser>(`/users/${id}/roles`, { roles }).then(r => r.data),
    onSuccess: (updated) => {
      // Ответ кладём в кэш СРАЗУ, а не ждём перезагрузки списка: строка — это ещё и источник
      // для следующего открытия меню ролей, и до обновления она предлагала бы прежний состав.
      qc.setQueryData<AppUser[]>([QK], prev =>
        prev?.map(u => (u.id === updated.id ? updated : u)));
      qc.invalidateQueries({ queryKey: [QK] });
      qc.invalidateQueries({ queryKey: ['account'] });
    },
  });
}

export function useResetUserPassword() {
  return useMutation({
    mutationFn: ({ id, newPassword }: { id: string; newPassword: string }) =>
      apiClient.post(`/users/${id}/reset-password`, { newPassword }),
  });
}

export function useDeleteUser() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (id: string) => apiClient.delete(`/users/${id}`),
    onSuccess: () => qc.invalidateQueries({ queryKey: [QK] }),
  });
}

export interface SendEmailResult { ok: boolean; sent?: number; skipped?: string[]; error?: string; }

/** Отправка сообщения выбранным пользователям (адреса в Bcc). */
export function useSendEmail() {
  return useMutation({
    mutationFn: (dto: { userIds: string[]; subject: string; body: string }) =>
      apiClient.post<SendEmailResult>('/email/send', dto).then(r => r.data),
  });
}

