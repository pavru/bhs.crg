import axios, { AxiosError, type InternalAxiosRequestConfig } from 'axios';
import { getToken, getRefreshToken, replaceTokens, clearToken } from './token';
import { recordApiError } from './apiErrorLog';

/**
 * Ошибка API с идентификатором запроса (issue #834).
 *
 * Поле СТРУКТУРНОЕ, а не выуженное регулярным выражением из фразы «Идентификатор запроса: …»:
 * сервер присылает `traceId` отдельным полем тела, и первая же правка формулировки не отключит
 * молча ни кнопку «Сообщить об ошибке», ни поиск в логе (класс ошибок #773).
 *
 * Есть только у 500: у доменного отказа («укажите название») искать в логе нечего.
 */
export interface ApiErrorWithTrace extends AxiosError {
  traceId?: string;
}

export function traceIdOf(e: unknown): string | undefined {
  return (e as ApiErrorWithTrace | undefined)?.traceId;
}

const baseURL = import.meta.env.VITE_API_URL ?? '/api';

export const apiClient = axios.create({ baseURL });

apiClient.interceptors.request.use((config) => {
  const token = getToken();
  if (token) config.headers.Authorization = `Bearer ${token}`;
  return config;
});

// Тихое обновление access по refresh-токену (issue #148 follow-up). Один общий запрос
// на все параллельные 401 (single-flight); голый axios — чтобы не зациклить интерсептор.
let refreshPromise: Promise<string | null> | null = null;

async function refreshAccess(): Promise<string | null> {
  const refresh = getRefreshToken();
  if (!refresh) return null;
  try {
    const { data } = await axios.post<{ accessToken: string; refreshToken: string }>(
      `${baseURL}/auth/refresh`, { refreshToken: refresh });
    replaceTokens(data.accessToken, data.refreshToken);
    return data.accessToken;
  } catch {
    return null;
  }
}

function toLogin() {
  clearToken();
  if (window.location.pathname !== '/login') window.location.href = '/login';
}

apiClient.interceptors.response.use(
  (r) => r,
  async (err: AxiosError) => {
    const original = err.config as (InternalAxiosRequestConfig & { _retried?: boolean }) | undefined;
    const url = original?.url ?? '';
    const isAuthCall = url.includes('/auth/refresh') || url.includes('/auth/login');

    if (err.response?.status === 401 && original && !isAuthCall) {
      if (!original._retried && getRefreshToken()) {
        original._retried = true;
        refreshPromise ??= refreshAccess().finally(() => { refreshPromise = null; });
        const newAccess = await refreshPromise;
        if (newAccess) {
          original.headers.Authorization = `Bearer ${newAccess}`;
          return apiClient(original);
        }
      }
      toLogin();
    }

    const body = err.response?.data as { error?: string; detail?: string; traceId?: string } | undefined;
    const serverMessage = body?.error ?? body?.detail;
    if (serverMessage) err.message = serverMessage;

    const traceId = typeof body?.traceId === 'string' && body.traceId ? body.traceId : undefined;
    if (traceId) (err as ApiErrorWithTrace).traceId = traceId;

    // Пишем в буфер ВСЕ дошедшие сюда неудачи, включая сетевые (status 0): «сервер не ответил» —
    // ровно тот случай, о котором сообщают чаще всего, и в форме он должен быть виден. 401, после
    // которого токен обновился успешно, сюда не доходит (ветка выше повторяет запрос и выходит) —
    // и правильно: для пользователя ничего не случилось.
    recordApiError({
      at: new Date().toLocaleTimeString('ru-RU'),
      method: (original?.method ?? 'GET').toUpperCase(),
      url,
      status: err.response?.status ?? 0,
      traceId,
    });

    return Promise.reject(err);
  },
);
