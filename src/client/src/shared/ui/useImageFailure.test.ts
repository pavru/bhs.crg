// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, renderHook } from '@testing-library/react';
import { useImageFailure } from './useImageFailure';

beforeEach(() => { vi.useFakeTimers(); });
afterEach(() => { cleanup(); vi.useRealTimers(); });

const hook = (mark: string | null) =>
  renderHook(({ m }: { m: string | null }) => useImageFailure(m), { initialProps: { m: mark } });

const failed = (h: ReturnType<typeof hook>) => h.result.current[0];
const fail = (h: ReturnType<typeof hook>) => act(() => h.result.current[1]());
const pass = (ms: number) => act(() => { vi.advanceTimersByTime(ms); });

describe('отказ загрузки картинки оформления', () => {
  it('помнится по метке и уходит с её сменой', () => {
    const h = hook('logo?v=1|значки-до');
    fail(h);
    expect(failed(h)).toBe(true);

    // Хранилище поднялось: значки построены, адрес логотипа прежний — метка другая.
    h.rerender({ m: 'logo?v=1|значки-после' });
    expect(failed(h)).toBe(false);
  });

  // Случайный сбой при уже построенных значках метку не меняет: без повтора слова «не читается»
  // стояли бы до перезагрузки страницы.
  it('снимается сам: картинку пробуют снова', () => {
    const h = hook('logo');
    fail(h);
    pass(19_999);
    expect(failed(h)).toBe(true);
    pass(1);
    expect(failed(h)).toBe(false);
  });

  // Файл, который картинкой не является, откажет и в третий раз — и тогда отказ остаётся: иначе
  // каждая открытая вкладка слала бы запрос за запросом без конца.
  it('после двух повторов остаётся', () => {
    const h = hook('logo');
    fail(h);
    pass(20_000);
    fail(h);
    pass(20_000);
    expect(failed(h)).toBe(false);
    fail(h);
    pass(10 * 60_000);
    expect(failed(h)).toBe(true);
  });

  it('новая метка начинает счёт повторов заново', () => {
    const h = hook('logo?v=1');
    fail(h); pass(20_000); fail(h); pass(20_000); fail(h);
    h.rerender({ m: 'logo?v=2' });
    fail(h);
    pass(20_000);
    expect(failed(h)).toBe(false);
  });

  it('без картинки отказа нет и запомнить его нельзя', () => {
    const h = hook(null);
    fail(h);
    expect(failed(h)).toBe(false);
  });
});
