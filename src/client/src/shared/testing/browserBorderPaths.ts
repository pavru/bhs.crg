/**
 * Разбор снимка границы браузеров — один на генератор (`scripts/browser-border.mjs`, Node 22 читает
 * `.ts` сам) и на помощник теста (`browserBorder.ts`). Ключ, записанный одним и не узнанный другим,
 * значил бы молча удалённое допустимое или оставленное лишнее. Без импортов и без побочных действий.
 */

/** `globalThis`, `Map.prototype`, `%TypedArray%.prototype` → объект; нет такого — `undefined`. */
export function resolveBorderPath(path: string): object | undefined {
  const [head, ...rest] = path.split('.');
  const root = globalThis as unknown as Record<string, unknown>;
  let value: unknown = head === 'globalThis' ? globalThis
    : head === '%TypedArray%' ? Object.getPrototypeOf(Uint8Array) : root[head];
  for (const part of rest) value = (value as Record<string, unknown> | undefined)?.[part];
  return (typeof value === 'object' && value !== null) || typeof value === 'function' ? value : undefined;
}

/** Строка — как есть, известный символ — `Symbol.iterator`; прочие символы не записываются. */
export function borderKeyName(key: string | symbol): string | undefined {
  if (typeof key === 'string') return key;
  const name = key.description?.startsWith('Symbol.') ? key.description.slice(7) : undefined;
  return name !== undefined && (Symbol as unknown as Record<string, unknown>)[name] === key ? `Symbol.${name}` : undefined;
}
