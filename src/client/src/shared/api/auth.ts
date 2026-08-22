import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiClient } from './client';

const REGISTRATION_OPEN_KEY = ['auth', 'registration-open'];

/**
 * Открыта ли регистрация первого администратора (issue #826).
 *
 * Сервер держит её открытой, пока в системе нет ни одного пользователя, и первый получает роль
 * Admin. Этот флаг решает, ЧТО показывать на `/login`, поэтому:
 *
 * - без повторов (`retry: false`) — пока ответа нет, форма не рисуется вовсе, и повторы растянули
 *   бы пустой экран на десятки секунд;
 * - отказ трактуется как «регистрация закрыта» и показывает обычный вход: он о недоступном
 *   сервере скажет внятно, а форма регистрации на работающей системе была бы прямой ложью.
 */
export function useRegistrationOpen() {
  return useQuery({
    queryKey: REGISTRATION_OPEN_KEY,
    // Срок ответа СВОЙ и короткий: у apiClient общего таймаута нет, а пока ответа нет, экран
    // входа пуст. Сервер, который принял соединение и замолчал (пул базы, медленный старт за
    // уже здоровым прокси), держал бы пустую карточку до таймаута nginx — минуту без единого
    // слова и без единой кнопки. Отвалившись за 6 с, мы показываем обычный вход.
    queryFn: async () =>
      (await apiClient.get<{ open: boolean }>('/auth/registration-open', { timeout: 6000 })).data.open,
    retry: false,
    refetchOnWindowFocus: false,
  });
}

/** Регистрация первого администратора. 403 — кто-то успел раньше (см. registerErrorText). */
export function useRegisterFirstAdmin() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (dto: { email: string; password: string; displayName: string }) =>
      apiClient.post('/auth/register', dto),
    // Ответ на флаг теперь известен точно — записываем его, а не помечаем устаревшим.
    // Иначе: QueryClient живёт модулем, `staleTime` 30 с, переходы клиентские, — и первый же
    // «Выйти» после первичной настройки вернул бы на настроенной системе экран «Первый вход»
    // с плашкой «эта форма открыта любому», подменяемый входом через мгновение. Кто успел
    // отправить форму, получил бы 403. Инвалидация от этого не спасает: старое значение
    // отдаётся, пока идёт перезапрос.
    onSuccess: () => queryClient.setQueryData(REGISTRATION_OPEN_KEY, false),
  });
}

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
