import border from './browserBorder.json';

/**
 * Граница браузеров просмотрщика в среде теста — только для тестов ([UI-21], change infra-node26, D6).
 *
 * Импорт модуля убирает из встроенных объектов всё, чего нет в снимке границы (`browserBorder.json`,
 * снят с Node 22 генератором `scripts/browser-border.mjs`): на Node 26 `Map.getOrInsertComputed`,
 * `Math.sumPrecise`, `Uint8Array.toBase64` свои, и без этого сторож заплаток pdf.js молча зеленел бы.
 * Импортируется ПЕРВЫМ — до `pdfEngine`, который ставит заплатки там, где функции нет.
 *
 * У глобального объекта трогаются только встроенные свойства — неперечисляемые и без `__` в начале:
 * служебное исполнителя тестов (`__vitest_worker__` — тоже неперечисляемое) остаётся, без него vitest
 * теряет своё состояние. Процесс воркера vitest у файла свой: удаление не утекает.
 */

/** Тот же разбор пути, что в генераторе снимка. */
function resolve(path: string): object | undefined {
  const [head, ...rest] = path.split('.');
  const root = globalThis as unknown as Record<string, unknown>;
  let value: unknown = head === 'globalThis' ? globalThis
    : head === '%TypedArray%' ? Object.getPrototypeOf(Uint8Array) : root[head];
  for (const part of rest) value = (value as Record<string, unknown> | undefined)?.[part];
  return typeof value === 'object' || typeof value === 'function' ? (value ?? undefined) : undefined;
}

function keyName(key: string | symbol): string | undefined {
  if (typeof key === 'string') return key;
  const name = key.description?.startsWith('Symbol.') ? key.description.slice(7) : undefined;
  return name !== undefined && (Symbol as unknown as Record<string, unknown>)[name] === key ? `Symbol.${name}` : undefined;
}

/** Удаляет лишнее и возвращает его списком `путь.ключ`. */
function enterBrowserBorder(): string[] {
  const removed: string[] = [];
  for (const [path, allowed] of Object.entries(border.objects)) {
    const target = resolve(path);
    if (target === undefined) continue;
    const known = new Set(allowed);
    for (const key of Reflect.ownKeys(target)) {
      const name = keyName(key);
      if (name === undefined || known.has(name)) continue;
      if (path === 'globalThis' && (name.startsWith('__') || Object.getOwnPropertyDescriptor(target, key)?.enumerable)) continue;
      if (Reflect.deleteProperty(target, key)) removed.push(`${path}.${name}`);
    }
  }
  return removed;
}

export const REMOVED_AT_BORDER = enterBrowserBorder();
