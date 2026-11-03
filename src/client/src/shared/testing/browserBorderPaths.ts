/**
 * Разбор снимка границы браузеров — один на генератор (`scripts/browser-border.mjs`, Node 22 читает
 * `.ts` сам) и на помощник теста (`browserBorder.ts`). Ключ, записанный одним и не узнанный другим,
 * значил бы молча удалённое допустимое или оставленное лишнее. Без импортов и без побочных действий.
 */

/** Встроенные объекты без своего глобального имени — через сами значения. */
const INTRINSICS: Record<string, () => object> = {
  '%TypedArray%': () => Object.getPrototypeOf(Uint8Array),
  // Не через глобал `Iterator`: на границе (Firefox 126, Safari 18) его нет, а помощник убирает его
  // раньше — и путь `Iterator.prototype` не нашёлся бы (change sharedui-pdf-border-gaps, D3).
  '%IteratorPrototype%': () => Object.getPrototypeOf(Object.getPrototypeOf([][Symbol.iterator]())),
};

/** `globalThis`, `Map.prototype`, `%TypedArray%.prototype`, `%IteratorPrototype%` → объект; нет такого — `undefined`. */
export function resolveBorderPath(path: string): object | undefined {
  const [head, ...rest] = path.split('.');
  const root = globalThis as unknown as Record<string, unknown>;
  let value: unknown = head === 'globalThis' ? globalThis : INTRINSICS[head]?.() ?? root[head];
  for (const part of rest) value = (value as Record<string, unknown> | undefined)?.[part];
  return (typeof value === 'object' && value !== null) || typeof value === 'function' ? value : undefined;
}

/**
 * Известные символы — запомнены при загрузке модуля: граница убирает и сами `Symbol.dispose`…, и
 * после этого ключ `[Symbol.dispose]` у других объектов иначе уже не узнавался бы.
 */
const WELL_KNOWN = new Map(Object.getOwnPropertyNames(Symbol)
  .map(name => [(Symbol as unknown as Record<string, unknown>)[name], name] as const)
  .filter((pair): pair is readonly [symbol, string] => typeof pair[0] === 'symbol'));

/** Строка — как есть, известный символ — `Symbol.iterator`; прочие символы не записываются. */
export function borderKeyName(key: string | symbol): string | undefined {
  if (typeof key === 'string') return key;
  const name = WELL_KNOWN.get(key);
  return name === undefined ? undefined : `Symbol.${name}`;
}
