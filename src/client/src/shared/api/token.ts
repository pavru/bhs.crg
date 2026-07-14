/**
 * Хранилище access-токена с поддержкой «Запомнить меня».
 *
 * remember = true  → localStorage (переживает закрытие вкладки/браузера);
 * remember = false → sessionStorage (живёт только до закрытия вкладки).
 *
 * Чтение всегда смотрит в оба хранилища, очистка — тоже, чтобы не оставить
 * «висящий» токен при смене режима или логауте.
 */
const KEY = 'access_token';

export function getToken(): string | null {
  return localStorage.getItem(KEY) ?? sessionStorage.getItem(KEY);
}

export function setToken(token: string, remember: boolean): void {
  if (remember) {
    localStorage.setItem(KEY, token);
    sessionStorage.removeItem(KEY);
  } else {
    sessionStorage.setItem(KEY, token);
    localStorage.removeItem(KEY);
  }
}

export function clearToken(): void {
  localStorage.removeItem(KEY);
  sessionStorage.removeItem(KEY);
}
