import border from './browserBorder.json';
import { borderKeyName, resolveBorderPath } from './browserBorderPaths';

/**
 * Граница браузеров просмотрщика в среде теста — только для тестов ([UI-21], change infra-node26, D6).
 *
 * Импорт модуля убирает из встроенных объектов ES всё, чего нет в снимке (`browserBorder.json`, снят
 * с Node 22 генератором `scripts/browser-border.mjs`): на Node 26 `Map.getOrInsertComputed`,
 * `Promise.try`, `Uint8Array.toBase64` свои, и без этого сторож заплаток pdf.js молча зеленел бы.
 * Импортируется ПЕРВЫМ — до `pdfEngine`, который ставит заплатки там, где функции нет.
 *
 * Снимок — поверхность Node 22, то есть не новее Chrome 126. Чего нет у Firefox 126 и Safari 18 при
 * том, что есть у Node 22 (итераторные помощники, методы `Set`), и веб-API (`Response`, `Blob`)
 * он НЕ убирает.
 *
 * У глобального объекта трогаются только встроенные свойства — неперечисляемые и без `__` в начале:
 * служебное исполнителя тестов (`__vitest_worker__` — тоже неперечисляемое) остаётся, без него vitest
 * теряет своё состояние. Процесс воркера vitest у файла свой: удаление не утекает.
 */

/** Удаляет лишнее и возвращает его списком `путь.ключ`. */
function enterBrowserBorder(): string[] {
  // В браузере это снесло бы весь DOM: снимок знает только встроенное Node.
  const proc = (globalThis as { process?: { versions?: { node?: string } } }).process;
  if (typeof proc?.versions?.node !== 'string') throw new Error('browserBorder — только для тестов в Node');
  const removed: string[] = [];
  for (const [path, allowed] of Object.entries(border.objects)) {
    const target = resolveBorderPath(path);
    if (target === undefined) continue;
    const known = new Set(allowed);
    for (const key of Reflect.ownKeys(target)) {
      const name = borderKeyName(key);
      if (name === undefined || known.has(name)) continue;
      if (path === 'globalThis' && (name.startsWith('__') || Object.getOwnPropertyDescriptor(target, key)?.enumerable)) continue;
      if (Reflect.deleteProperty(target, key)) removed.push(`${path}.${name}`);
    }
  }
  return removed;
}

export const REMOVED_AT_BORDER = enterBrowserBorder();
