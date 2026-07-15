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

/** Подтверждение адреса по ссылке из письма (issue #148). */
export function useConfirmEmail() {
  return useMutation({
    mutationFn: (dto: { email: string; token: string }) =>
      apiClient.post('/auth/confirm-email', dto),
  });
}

/** Подтверждение смены адреса (переход по ссылке на новый адрес). */
export function useConfirmEmailChange() {
  return useMutation({
    mutationFn: (dto: { userId: string; newEmail: string; token: string }) =>
      apiClient.post('/auth/confirm-email-change', dto),
  });
}
