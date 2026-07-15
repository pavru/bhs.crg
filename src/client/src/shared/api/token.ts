/**
 * Хранилище токенов сессии (issue #148 follow-up): короткий access-JWT + долгоживущий
 * refresh-токен. «Запомнить меня» выбирает хранилище: localStorage (переживает вкладку)
 * либо sessionStorage (до закрытия вкладки). Чтение/очистка смотрят оба.
 */
const ACCESS = 'access_token';
const REFRESH = 'refresh_token';

export function getToken(): string | null {
  return localStorage.getItem(ACCESS) ?? sessionStorage.getItem(ACCESS);
}

export function getRefreshToken(): string | null {
  return localStorage.getItem(REFRESH) ?? sessionStorage.getItem(REFRESH);
}

/** Сохранить пару токенов в выбранное хранилище (при логине). */
export function setTokens(access: string, refresh: string, remember: boolean): void {
  const store = remember ? localStorage : sessionStorage;
  const other = remember ? sessionStorage : localStorage;
  store.setItem(ACCESS, access);
  store.setItem(REFRESH, refresh);
  other.removeItem(ACCESS);
  other.removeItem(REFRESH);
}

/** Обновить пару, сохранив текущее хранилище (после refresh-ротации или смены пароля). */
export function replaceTokens(access: string, refresh: string): void {
  const inSession = sessionStorage.getItem(ACCESS) !== null;
  const store = inSession ? sessionStorage : localStorage;
  store.setItem(ACCESS, access);
  store.setItem(REFRESH, refresh);
}

export function clearToken(): void {
  localStorage.removeItem(ACCESS);
  sessionStorage.removeItem(ACCESS);
  localStorage.removeItem(REFRESH);
  sessionStorage.removeItem(REFRESH);
}
