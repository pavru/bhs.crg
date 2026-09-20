import { describe, it, expect, beforeEach, vi } from 'vitest';

// Прогон идёт в node, без DOM: хранилища подставляем сами. Проверяется здесь подписка на смену
// токена, а не браузерное хранилище, поэтому двух методов хватает — но именно двух, иначе тест
// проверял бы заглушку.
function memoryStorage(): Storage {
  const data = new Map<string, string>();
  return {
    getItem: (k: string) => data.get(k) ?? null,
    setItem: (k: string, v: string) => void data.set(k, v),
    removeItem: (k: string) => void data.delete(k),
    clear: () => data.clear(),
    key: (i: number) => [...data.keys()][i] ?? null,
    get length() { return data.size; },
  } as Storage;
}

globalThis.localStorage = memoryStorage();
globalThis.sessionStorage = memoryStorage();

const { setTokens, replaceTokens, clearToken, getToken, onTokenChanged } = await import('./token');

/**
 * Смена токена обязана быть слышна (issue #946).
 *
 * Перехватчик ответов обменивает токен по refresh после 401 — молча и мимо React. Пока о таком
 * обмене никто не узнавал, роль на экране оставалась прежней до перезагрузки страницы: сервер уже
 * отказывает, а кнопки на месте.
 */
describe('подписка на смену токена', () => {
  beforeEach(() => {
    localStorage.clear();
    sessionStorage.clear();
  });

  it('слышит вход', () => {
    const heard = vi.fn();
    const off = onTokenChanged(heard);

    setTokens('access-1', 'refresh-1', true);

    expect(heard).toHaveBeenCalledWith('access-1');
    off();
  });

  it('слышит тихий обмен токена', () => {
    setTokens('access-1', 'refresh-1', true);
    const heard = vi.fn();
    const off = onTokenChanged(heard);

    replaceTokens('access-2', 'refresh-2');

    expect(heard).toHaveBeenCalledWith('access-2');
    expect(getToken()).toBe('access-2');
    off();
  });

  it('слышит выход — с пустым токеном, а не молчанием', () => {
    setTokens('access-1', 'refresh-1', true);
    const heard = vi.fn();
    const off = onTokenChanged(heard);

    clearToken();

    expect(heard).toHaveBeenCalledWith(null);
    off();
  });

  it('отписка прекращает уведомления', () => {
    const heard = vi.fn();
    onTokenChanged(heard)();

    setTokens('access-1', 'refresh-1', true);

    expect(heard).not.toHaveBeenCalled();
  });
});
