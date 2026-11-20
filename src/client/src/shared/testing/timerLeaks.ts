/**
 * Сторож таймеров, переживших тест. В окружении jsdom таймеры — НЕ jsdom, а Node (`setTimeout` vitest на
 * окно не переносит), и `window.close()` их не снимает. Таймер, поставленный компонентом и не снятый при
 * размонтировании, срабатывает когда угодно — а если тест был последним в файле, то уже после того, как
 * vitest свернул jsdom: `window is not defined` из React или «parameter 1 is not of type 'Event'» из
 * `dispatchEvent`. Тесты при этом зелёные, а прогон красный — «Unhandled Errors», приписанные случайному
 * файлу; локально почти не воспроизводится, в CI под нагрузкой — то и дело.
 *
 * <p>Поэтому после каждого теста (после `cleanup` файла: хуки `after*` идут в обратном порядке, а этот
 * файл подключается раньше тестов) таймер НАШЕГО кода, ещё не сработавший и не снятый, роняет тест
 * ошибкой с местом, где таймер поставлен. «Наш» — первый кадр стека вне этого файла лежит в `src/`, а не в
 * `node_modules`. Не утечка: таймеры чужих библиотек (Radix FocusScope возвращает фокус нулевым таймером
 * после размонтирования, React Query держит таймеры сборки кэша) и наши таймеры уровня страницы
 * (`pageTimers` из `shared/utils` — модульное состояние, которое нарочно живёт дольше компонента). Их сторож
 * снимает молча в конце файла — пока jsdom ещё жив. Пойманный таймер тоже снимается: он не сработает
 * в следующем тесте.</p>
 *
 * <p>Чинить — в коде, а не в тесте: таймер компонента ставится через `useTimeouts` из `shared/hooks`
 * (снимается при размонтировании) или снимается в cleanup своего эффекта. Тесту с поддельными таймерами
 * (`vi.useFakeTimers`) сторож не мешает: поддельные он не видит.</p>
 *
 * Подключён в `vite.config.ts` (`test.setupFiles`); в окружении node окна нет, и файл ничего не делает.
 */
import { afterAll, afterEach } from 'vitest';

type Handle = ReturnType<typeof setTimeout>;
interface Tracked { handle: Handle; origin: Error; repeat: boolean }

const THIS_FILE = /shared[\\/]testing[\\/]timerLeaks\.ts/;
const PATCHED = Symbol.for('bhs.crg.timerLeaks');

/** Ключ таймера: у Node — объект `Timeout`, но `clearTimeout` принимает и его число. */
const keyOf = (h: unknown) => (h !== null && typeof h === 'object' ? Number(h) : h);

/** Таймеры уровня страницы — переживают компонент нарочно (`pageTimers`): утечкой не считаются. */
const PAGE_TIMERS = /shared[\\/]utils[\\/]pageTimers\.ts/;
/** Помощник таймеров компонента: место таймера — тот, кто его позвал. */
const HELPERS = /shared[\\/]hooks[\\/]useTimeouts\.ts/;

/** Кадр, поставивший таймер нашим кодом; `null` — таймер чужой библиотеки или уровня страницы. */
function ownFrame(origin: Error): string | null {
  const frames = (origin.stack ?? '').split('\n').slice(1).map(s => s.trim()).filter(f => !THIS_FILE.test(f));
  if (!frames[0] || PAGE_TIMERS.test(frames[0])) return null;
  const first = frames.find(f => !HELPERS.test(f)) ?? frames[0];
  const path = first.replace(/\\/g, '/');
  return path.includes('/node_modules/') || !path.includes('/src/') ? null : first;
}

const g = globalThis as typeof globalThis & { [PATCHED]?: true };

let takeLeaks: () => string[] = () => [];

/** Подключён ли сторож — в окружении jsdom обязан быть (сверяет `timerLeaks.test.tsx`). */
export const timerGuardOn = () => g[PATCHED] === true;

/**
 * Таймеры нашего кода, ещё не сработавшие и не снятые, — местами, где поставлены; найденные снимаются.
 * Зовётся после каждого теста, а тестом сторожа — чтобы проверить, что сторож ловит.
 */
export function takeLeakedTimers(): string[] {
  return takeLeaks();
}

if (typeof window !== 'undefined' && !g[PATCHED]) {
  g[PATCHED] = true;
  const pending = new Map<unknown, Tracked>();
  const realSetTimeout = globalThis.setTimeout;
  const realSetInterval = globalThis.setInterval;
  const realClearTimeout = globalThis.clearTimeout;
  const realClearInterval = globalThis.clearInterval;

  /** Свойства оригинала (в том числе `util.promisify.custom`) — обёртке, чтобы `promisify` вёл себя как раньше. */
  const withProps = <F extends object>(wrapped: F, real: object): F => {
    for (const key of Reflect.ownKeys(real)) {
      if (key === 'length' || key === 'name' || key === 'prototype') continue;
      const d = Object.getOwnPropertyDescriptor(real, key);
      if (d) Object.defineProperty(wrapped, key, d);
    }
    return wrapped;
  };

  const track = (real: typeof setTimeout, repeat: boolean) =>
    withProps(((cb: (...a: unknown[]) => void, ms?: number, ...args: unknown[]) => {
      const origin = new Error('таймер поставлен здесь');
      const box: { key?: unknown } = {};
      const handle = real((...a: unknown[]) => {
        if (!repeat) pending.delete(box.key);
        return typeof cb === 'function' ? cb(...a) : undefined;
      }, ms, ...args);
      box.key = keyOf(handle);
      pending.set(box.key, { handle, origin, repeat });
      return handle;
    }) as typeof setTimeout, real);

  const untrack = <F extends (h?: Handle) => void>(real: F) =>
    withProps(((h?: Handle) => { pending.delete(keyOf(h)); real(h); }) as F, real);

  globalThis.setTimeout = track(realSetTimeout, false);
  globalThis.setInterval = track(realSetInterval as unknown as typeof setTimeout, true) as unknown as typeof setInterval;
  globalThis.clearTimeout = untrack(realClearTimeout);
  globalThis.clearInterval = untrack(realClearInterval);

  const stop = (t: Tracked, key: unknown) => {
    pending.delete(key);
    (t.repeat ? realClearInterval : realClearTimeout)(t.handle);
  };

  takeLeaks = () => {
    const leaked: string[] = [];
    for (const [key, t] of [...pending]) {
      const frame = ownFrame(t.origin);
      if (frame === null) continue;
      leaked.push(`${t.repeat ? 'setInterval' : 'setTimeout'} ${frame}`);
      stop(t, key);
    }
    return [...new Set(leaked)];
  };

  afterEach(() => {
    const leaked = takeLeakedTimers();
    if (leaked.length === 0) return;
    throw new Error(
      `Таймер пережил тест (${leaked.length}): поставлен кодом приложения и не снят к концу теста — `
      + 'сработает когда угодно, в том числе после того, как vitest свернёт jsdom («window is not defined», '
      + '«Unhandled Errors» в CI). Таймер компонента — через useTimeouts (снимается при размонтировании), '
      + 'нарочно живущий дольше компонента — через pageTimers; см. '
      + `src/shared/testing/timerLeaks.ts.\n${leaked.map(l => `  ${l}`).join('\n')}`);
  });

  // Чужие таймеры (Radix, React Query) — до того, как vitest свернёт jsdom: сработав после, они роняли
  // прогон ошибками, которые наш код не поймает.
  afterAll(() => { for (const [key, t] of [...pending]) stop(t, key); });
}
