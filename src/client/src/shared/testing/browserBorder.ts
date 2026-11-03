import border from './browserBorder.json';
import { borderKeyName, resolveBorderPath } from './browserBorderPaths';

/**
 * Граница браузеров просмотрщика в среде теста — только для тестов ([UI-21], change infra-node26, D6;
 * по самому слабому браузеру — change sharedui-pdf-border-gaps, D3).
 *
 * Импорт модуля убирает из встроенных объектов всё, чего нет в снимке (`browserBorder.json`,
 * генератор `scripts/browser-border.mjs`): на Node 26 `Map.getOrInsertComputed`, `Promise.try`,
 * итераторные помощники свои, и без этого сторож заплаток pdf.js молча зеленел бы. Импортируется
 * ПЕРВЫМ — до `pdfEngine`, который ставит заплатки там, где функции нет.
 *
 * Снимок — поверхность Node 22 без всего, чего по данным MDN нет хотя бы у одного браузера границы
 * (Chrome 126, Firefox 126, Safari 18): встроенное ES и веб-API, которые зовёт pdf.js (`Response`,
 * `Blob`, `ReadableStream`…). DOM он не моделирует. Нарочно оставленное (`kept` снимка) —
 * то, без чего не обойтись самой среде тестов.
 *
 * У глобального объекта трогаются только встроенные свойства — неперечисляемые и без `__` в начале:
 * служебное исполнителя тестов (`__vitest_worker__` — тоже неперечисляемое) остаётся, без него vitest
 * теряет своё состояние. Процесс воркера vitest у файла свой: удаление не утекает.
 */

export interface RemovedKey { path: string; name: string }

/** Удаляет лишнее и возвращает его списком. */
function enterBrowserBorder(): RemovedKey[] {
  // В браузере это снесло бы весь DOM: снимок знает только встроенное Node.
  const proc = (globalThis as { process?: { versions?: { node?: string } } }).process;
  if (typeof proc?.versions?.node !== 'string') throw new Error('browserBorder — только для тестов в Node');
  const removed: RemovedKey[] = [];
  for (const [path, allowed] of Object.entries(border.objects)) {
    const target = resolveBorderPath(path);
    if (target === undefined) continue;
    const known = new Set(allowed);
    for (const key of Reflect.ownKeys(target)) {
      const name = borderKeyName(key);
      if (name === undefined || known.has(name)) continue;
      if (path === 'globalThis' && (name.startsWith('__') || Object.getOwnPropertyDescriptor(target, key)?.enumerable)) continue;
      if (Reflect.deleteProperty(target, key)) removed.push({ path, name });
    }
  }
  return removed;
}

export const REMOVED_KEYS = enterBrowserBorder();

/** Убранное — `путь.ключ`, для сообщений теста. */
export const REMOVED_AT_BORDER = REMOVED_KEYS.map(({ path, name }) => `${path}.${name}`);

/** Нет на границе, но оставлено ради среды тестов: ключ → причина. */
export const KEPT_AT_BORDER: Record<string, string> = border.kept;
