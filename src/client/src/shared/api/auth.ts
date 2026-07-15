import { useMutation } from '@tanstack/react-query';
import { apiClient } from './client';

/** Запрос письма для сброса пароля (issue #148). Ответ всегда 200 — существование
 *  адреса не раскрывается (enumeration-safe). */
export function useForgotPassword() {
  return useMutation({
    mutationFn: (dto: { email: string }) =>
      apiClient.post('/auth/forgot-password', dto),
  });
}

/** Установка нового пароля по токену из письма. */
export function useResetPassword() {
  return useMutation({
    mutationFn: (dto: { email: string; token: string; newPassword: string }) =>
      apiClient.post('/auth/reset-password', dto),
  });
}
