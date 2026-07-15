import axios, { AxiosError, type InternalAxiosRequestConfig } from 'axios';
import { getToken, getRefreshToken, replaceTokens, clearToken } from './token';

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

    const serverMessage = (err.response?.data as { error?: string; detail?: string } | undefined)?.error
      ?? (err.response?.data as { detail?: string } | undefined)?.detail;
    if (serverMessage) err.message = serverMessage;
    return Promise.reject(err);
  },
);
