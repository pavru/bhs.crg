import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiClient } from './client';

const QK = 'users';

export interface AppUser {
  id: string;
  email: string;
  displayName: string;
  /** Техническое имя роли — им же роль и назначают. */
  role: string;
  /** Название для человека: техническое имя заведённой роли нечитаемо (issue #951). */
  roleTitle: string;
}

export function useListUsers() {
  return useQuery<AppUser[]>({
    queryKey: [QK],
    queryFn: () => apiClient.get('/users').then(r => r.data),
  });
}

export function useCreateUser() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (dto: { email: string; displayName: string; password: string; role: string }) =>
      apiClient.post<AppUser>('/users', dto).then(r => r.data),
    onSuccess: () => qc.invalidateQueries({ queryKey: [QK] }),
  });
}

export function useChangeUserRole() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ id, role }: { id: string; role: string }) =>
      apiClient.put<AppUser>(`/users/${id}/role`, { role }).then(r => r.data),
    onSuccess: () => qc.invalidateQueries({ queryKey: [QK] }),
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

