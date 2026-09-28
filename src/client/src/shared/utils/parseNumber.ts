import { resolveLocale } from '@/shared/hooks/useLocale';

/**
 * Разбор числа из текста, набранного или вставленного человеком (Excel, PDF, буфер) — с оглядкой
 * на ту же региональную настройку, какой числа ФОРМАТИРУЮТСЯ (`formatNumber`, issue #953).
 *
 * <p>Разбор строгий (issue #1064). Из двух исходов — «не смог разобрать» и «разобрал неверно» —
 * второй дороже на порядок: первый человек видит и правит, второй уезжает в данные. Поэтому
 * значение либо укладывается в число ЦЕЛИКОМ, либо это отказ; разбор префикса, каким занимается
 * `parseFloat` (`parseFloat('12 шт') === 12`, `parseFloat('1.234,56') === 1.234`), запрещён.</p>
 *
 * <p>Читается запись в два шага. <b>Первый</b> — по локали: её десятичный знак, её разделитель
 * тысяч и пробелы (обычный, неразрывный, узкий — их даёт Excel). Получилось — ответ найден, и
 * `1.234` при `de-DE` это 1234, потому что там точка и есть разделитель тысяч. <b>Второй</b>, если
 * по локали не сложилось, — перебор: десятичным пробуется каждый из `.`/`,` и ни один, остальные
 * знаки считаются группирующими, а группы проверяются по три цифры. Ровно одно прочтение — оно и
 * берётся: `1.234,56` из выгрузки и `12.5` из английской таблицы читаются при `ru-RU` однозначно.
 * Два прочтения — отказ: `1.234` при `ru-RU` это и «тысяча двести тридцать четыре», и «1,234»,
 * и догадка здесь была бы ровно тем дефектом, ради которого написана функция.</p>
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
 * Одно прочтение записи: какой знак считаем десятичным (`null` — дробной части нет вовсе) и какие
 * знаки могут группировать. `null` — запись так не читается.
 */
function readWith(body: string, decimal: string | null, groupChars: Set<string>): number | null {
  let intPart = body;
  let fracPart = '';
  if (decimal) {
    const count = body.split(decimal).length - 1;
    if (count > 1) return null; // десятичный знак может быть только один
    if (count === 1) {
      const i = body.indexOf(decimal);
      intPart = body.slice(0, i) || '0';
      fracPart = body.slice(i + 1);
      if (!/^\d+$/.test(fracPart)) return null;
    }
  }

  // Целая часть: группы по три цифры, первая — от одной до трёх.
  const chunks: string[] = [];
  let current = '';
  for (const ch of intPart) {
    if (ch >= '0' && ch <= '9') { current += ch; continue; }
    if (!groupChars.has(ch)) return null; // знак, который в этом прочтении группировать не может
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
  return Number.isFinite(n) ? n : null;
}

/**
 * Разбор строки в число. `null` — отказ: значение не число целиком либо его запись читается двумя
 * способами. Отказ НЕ нуль и НЕ пустое значение: звать его так и есть дефект, ради которого
 * написана функция.
 *
 * @param storedLocale значение региональной настройки как есть, включая `system` (разрешается внутри).
 */
export function parseNumber(raw: string, storedLocale: string): number | null {
  const normalized = raw.trim().replace(GROUP_SPACES, ' ');
  // Только цифры, знак и разделители. Всё прочее (единицы измерения, %, буквы) — отказ.
  const m = /^([+-]?)([\d ,.]+)$/.exec(normalized);
  if (!m || !/\d/.test(m[2])) return null;
  const sign = m[1] === '-' ? -1 : 1;
  const body = m[2];

  const { decimal, group } = separatorsOf(resolveLocale(storedLocale));

  // Шаг 1: прочтение по локали. Оно главнее остальных — настройка для того и задана.
  const byLocale = readWith(body, decimal, new Set([' ', group]));
  if (byLocale !== null) return sign * byLocale;

  // Шаг 2: запись не по локали. Берём её, только если читается единственным способом.
  const values = new Set<number>();
  for (const candidate of [null, '.', ','] as const) {
    const groupChars = new Set([' ', '.', ',']);
    if (candidate) groupChars.delete(candidate);
    const value = readWith(body, candidate, groupChars);
    if (value !== null) values.add(value);
  }
  if (values.size !== 1) return null;
  return sign * [...values][0];
}
