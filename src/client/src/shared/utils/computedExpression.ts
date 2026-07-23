/**
 * Клиентский движок расчётных полей (issue #368, фаза 3). Формула — это JS, поэтому предпросмотр и
 * проактивная валидация делаются прямо в браузере. Авторитет — бэкенд (Jint) при генерации; клиент
 * лишь помогает автору (мгновенная валидация) и показывает предварительное значение пользователю.
 *
 * Безопасность: `new Function` исполняет авторское (Admin) выражение — та же модель доверия, что у
 * Typst-шаблонов/userlib (админ доверенный). Не полноценная песочница.
 */

const GET_RE = /get\(\s*["']([^"']+)["']\s*\)/g;

/** Ключи полей, на которые ссылается выражение через get("…"). */
export function referencedKeys(expression: string): string[] {
  const keys = new Set<string>();
  let m: RegExpExecArray | null;
  GET_RE.lastIndex = 0;
  while ((m = GET_RE.exec(expression))) keys.add(m[1]);
  return [...keys];
}

/** Вычисляет выражение; get(key) читает values[key] (иначе undefined). Ошибка → { error }. */
export function evalComputed(expression: string, values: Record<string, unknown>): { value?: unknown; error?: string } {
  if (!expression.trim()) return { value: undefined };
  try {
    const get = (key: string) => values[key];
    // eslint-disable-next-line @typescript-eslint/no-implied-eval, no-new-func
    const fn = new Function('get', `"use strict"; return (${expression});`);
    const value = fn(get);
    return { value };
  } catch (e) {
    return { error: e instanceof Error ? e.message : String(e) };
  }
}

/** Синтаксическая проверка выражения (компиляция без запуска) + ссылки на несуществующие поля. */
export function validateComputed(
  expression: string,
  knownKeys: Set<string>,
): { syntaxError?: string; unknownRefs: string[] } {
  const unknownRefs = referencedKeys(expression).filter(k => !knownKeys.has(k));
  let syntaxError: string | undefined;
  if (expression.trim()) {
    try {
      // eslint-disable-next-line @typescript-eslint/no-implied-eval, no-new-func
      new Function('get', `"use strict"; return (${expression});`);
    } catch (e) {
      syntaxError = e instanceof Error ? e.message : String(e);
    }
  }
  return { syntaxError, unknownRefs };
}

/**
 * Ключи расчётных полей, попавших в цикл зависимостей (топосорт Kahn по ссылкам между computed-полями).
 * `computed` — карта ключ→выражение только расчётных полей.
 */
export function findComputedCycles(computed: Record<string, string>): Set<string> {
  const keys = Object.keys(computed);
  const keySet = new Set(keys);
  const deps: Record<string, string[]> = {};
  const indeg: Record<string, number> = {};
  for (const k of keys) {
    deps[k] = referencedKeys(computed[k]).filter(r => keySet.has(r) && r !== k);
    indeg[k] = 0;
  }
  // Самоссылка — тоже цикл.
  const selfCyclic = new Set(keys.filter(k => referencedKeys(computed[k]).includes(k)));
  const dependents: Record<string, string[]> = Object.fromEntries(keys.map(k => [k, []]));
  for (const k of keys) for (const d of deps[k]) { dependents[d].push(k); indeg[k]++; }

  const ready = keys.filter(k => indeg[k] === 0);
  const ordered: string[] = [];
  while (ready.length) {
    const n = ready.shift()!;
    ordered.push(n);
    for (const m of dependents[n]) if (--indeg[m] === 0) ready.push(m);
  }
  const cyclic = new Set(keys.filter(k => !ordered.includes(k)));
  for (const k of selfCyclic) cyclic.add(k);
  return cyclic;
}
