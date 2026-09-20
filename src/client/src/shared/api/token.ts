/**
 * Хранилище токенов сессии (issue #148 follow-up): короткий access-JWT + долгоживущий
 * refresh-токен. «Запомнить меня» выбирает хранилище: localStorage (переживает вкладку)
 * либо sessionStorage (до закрытия вкладки). Чтение/очистка смотрят оба.
 */
const ACCESS = 'access_token';
const REFRESH = 'refresh_token';

/**
 * Подписка на смену токена. Нужна потому, что токен меняется НЕ ТОЛЬКО через экран входа:
 * перехватчик ответов молча обменивает его по refresh после 401 — и делает это в обход React.
 *
 * С тех пор как смена роли обесценивает выданный токен (issue #946), это стало видно: сервер уже
 * знает новую роль, а состояние экрана — старую, и понижённый администратор до перезагрузки
 * страницы видит кнопки, которые отвечают отказом. Ровно тот случай, который выглядит как поломка,
 * а не как «вам больше нельзя».
 */
type TokenListener = (access: string | null) => void;
const listeners = new Set<TokenListener>();

export function onTokenChanged(listener: TokenListener): () => void {
  listeners.add(listener);
  return () => { listeners.delete(listener); };
}

function announce(access: string | null): void {
  for (const listener of listeners) listener(access);
}

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
  announce(access);
}

/** Обновить пару, сохранив текущее хранилище (после refresh-ротации или смены пароля). */
export function replaceTokens(access: string, refresh: string): void {
  const inSession = sessionStorage.getItem(ACCESS) !== null;
  const store = inSession ? sessionStorage : localStorage;
  store.setItem(ACCESS, access);
  store.setItem(REFRESH, refresh);
  announce(access);
}

export function clearToken(): void {
  localStorage.removeItem(ACCESS);
  sessionStorage.removeItem(ACCESS);
  localStorage.removeItem(REFRESH);
  sessionStorage.removeItem(REFRESH);
  announce(null);
}
