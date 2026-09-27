import { resolveLocale } from '@/shared/hooks/useLocale';

/**
 * Разбор числа из текста, набранного или вставленного человеком (Excel, PDF, буфер) — по той же
 * региональной настройке, какой числа ФОРМАТИРУЮТСЯ (`formatNumber`, issue #953). Зеркало к ней.
 *
 * <p>Разбор строгий (issue #1064). Из двух исходов — «не смог разобрать» и «разобрал неверно» —
 * второй дороже на порядок: первый человек видит и правит, второй уезжает в данные. Поэтому
 * значение либо укладывается в число ЦЕЛИКОМ, либо это отказ; разбор префикса, каким занимается
 * `parseFloat` (`parseFloat('12 шт') === 12`, `parseFloat('1.234,56') === 1.234`), запрещён.</p>
 *
 * <p>Неоднозначность снимает локаль, а не догадка. Десятичным считается ТОЛЬКО знак локали:
 * при `ru-RU` это запятая, и `12,5` — двенадцать с половиной; при `en-US` — точка, и `12,5`
 * отказ, потому что в этой записи запятая может быть лишь группирующей, а «5» не группа из трёх
 * цифр. Группируют: пробел (обычный, неразрывный, узкий — их даёт Excel), групповой знак локали
 * и — только рядом с десятичным знаком локали — противоположный знак. Последнее нужно ради
 * `1.234,56`: так числа приходят из выгрузок и PDF, и при явной десятичной запятой точка не
 * может быть ничем, кроме разделителя тысяч. Без неё (`1.234` при `ru-RU`) запись неоднозначна —
 * и это отказ, а не догадка.</p>
 */

/** Пробелы-разделители тысяч: обычный, неразрывный, узкий неразрывный, тонкий. */
const GROUP_SPACES = /[\s\u00a0\u202f\u2009]+/g;
const IS_SPACE = /^[\s\u00a0\u202f\u2009]$/;

/** Разделители локали. Построение идёт через `Intl`, поэтому ответ кэшируется: вставка зовёт разбор на каждую ячейку. */
const SEPARATORS = new Map<string, { decimal: string; group: string }>();

function separatorsOf(resolved: string): { decimal: string; group: string } {
  const cached = SEPARATORS.get(resolved);
  if (cached) return cached;
  const parts = new Intl.NumberFormat(resolved).formatToParts(12345.6);
  const decimal = parts.find(p => p.type === 'decimal')?.value ?? '.';
  const rawGroup = parts.find(p => p.type === 'group')?.value ?? '';
  // Групповой знак локали может быть пробелом (`ru-RU` — неразрывный): приводим к обычному, как и текст.
  const value = { decimal, group: IS_SPACE.test(rawGroup) ? ' ' : rawGroup };
  SEPARATORS.set(resolved, value);
  return value;
}

/**
 * Разбор строки в число по региональной настройке. `null` — отказ: значение не число целиком
 * либо его запись в этой локали неоднозначна. Отказ НЕ нуль и НЕ пустое значение: звать его так
 * и есть дефект, ради которого написана функция.
 *
 * @param storedLocale значение настройки как есть, включая `system` (разрешается внутри).
 */
export function parseNumber(raw: string, storedLocale: string): number | null {
  const normalized = raw.trim().replace(GROUP_SPACES, ' ');
  // Только цифры, знак и разделители. Всё прочее (единицы измерения, %, буквы) — отказ.
  const m = /^([+-]?)([\d ,.]+)$/.exec(normalized);
  if (!m || !/\d/.test(m[2])) return null;
  const sign = m[1] === '-' ? -1 : 1;
  const body = m[2];

  const { decimal, group } = separatorsOf(resolveLocale(storedLocale));
  const foreign = decimal === ',' ? '.' : ',';

  // Десятичный знак — знак локали, и он может быть только один.
  const decimalCount = body.split(decimal).length - 1;
  if (decimalCount > 1) return null;

  let intPart = body;
  let fracPart = '';
  if (decimalCount === 1) {
    const i = body.indexOf(decimal);
    intPart = body.slice(0, i) || '0';
    fracPart = body.slice(i + 1);
    if (!/^\d+$/.test(fracPart)) return null;
  }

  const groupChars = new Set([' ', group]);
  // Чужой знак группирует лишь там, где десятичный назван явно: `1.234,56` — да, `1.234` — нет.
  if (decimalCount === 1) groupChars.add(foreign);

  // Целая часть: группы по три цифры, первая — от одной до трёх.
  const chunks: string[] = [];
  let current = '';
  for (const ch of intPart) {
    if (ch >= '0' && ch <= '9') { current += ch; continue; }
    if (!groupChars.has(ch)) return null; // разделитель, который в этой локали здесь стоять не может
    chunks.push(current);
    current = '';
  }
  chunks.push(current);
  if (chunks.length === 1) {
    if (!/^\d+$/.test(chunks[0])) return null;
  } else if (!/^\d{1,3}$/.test(chunks[0]) || !chunks.slice(1).every(c => /^\d{3}$/.test(c))) {
    return null;
  }

  const n = Number(`${chunks.join('')}.${fracPart || '0'}`);
  return Number.isFinite(n) ? sign * n : null;
}
