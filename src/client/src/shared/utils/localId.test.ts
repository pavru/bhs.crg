import { describe, it, expect, afterEach, vi } from 'vitest';
import { newLocalId } from './localId';

/**
 * Проверяем не «выдаёт строку», а три платформы, на которых система реально живёт: HTTPS
 * (есть randomUUID), HTTP внутри доверенной сети (остаётся только getRandomValues) и совсем
 * бедное окружение. Второй случай — тот самый, где прямой вызов ронял экран (issue #848).
 */
const realCrypto = globalThis.crypto;

afterEach(() => {
  Object.defineProperty(globalThis, 'crypto', { value: realCrypto, configurable: true, writable: true });
  vi.restoreAllMocks();
});

function withCrypto(value: unknown) {
  Object.defineProperty(globalThis, 'crypto', { value, configurable: true, writable: true });
}

const UUID_V4 = /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/;

describe('newLocalId', () => {
  it('в защищённом контексте берёт готовый randomUUID', () => {
    const randomUUID = vi.fn(() => '11111111-2222-4333-8444-555555555555');
    withCrypto({ randomUUID, getRandomValues: realCrypto.getRandomValues.bind(realCrypto) });

    expect(newLocalId()).toBe('11111111-2222-4333-8444-555555555555');
    expect(randomUUID).toHaveBeenCalledTimes(1);
  });

  it('без randomUUID (установка по HTTP) собирает UUID v4 из getRandomValues', () => {
    withCrypto({ getRandomValues: realCrypto.getRandomValues.bind(realCrypto) });

    const id = newLocalId();
    expect(id).toMatch(UUID_V4);
  });

  it('без Web Crypto вовсе всё равно отдаёт идентификатор, а не падает', () => {
    withCrypto(undefined);

    expect(() => newLocalId()).not.toThrow();
    expect(newLocalId()).not.toBe(newLocalId());
  });

  it('значения не повторяются на любом из путей', () => {
    for (const c of [
      { randomUUID: realCrypto.randomUUID.bind(realCrypto), getRandomValues: realCrypto.getRandomValues.bind(realCrypto) },
      { getRandomValues: realCrypto.getRandomValues.bind(realCrypto) },
      undefined,
    ]) {
      withCrypto(c);
      const ids = new Set(Array.from({ length: 200 }, () => newLocalId()));
      expect(ids.size).toBe(200);
    }
  });
});
